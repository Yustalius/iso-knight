"""PPO for the squad soldier (CleanRL style, one file, no extra dependencies).

Many matches run inside libsquad; every learner slot is a sample stream. Dead slots (alive = 0) are not decisions and are
left out of the loss; a death or the end of a match sets done, which cuts the advantage chain.
A curriculum is a list of stages (scenario mixes); a stage ends when its win rate passes the threshold or its step budget
runs out. Hint rewards (damage, kills) are scaled by a shaping factor that decays to zero within each stage.
"""
import collections
import json
import math
import os
import time

import numpy as np
import torch
import torch.nn as nn

from .env import ACT_VERSION, OBS_VERSION, SquadEnv
from .model import Policy, act, evaluate

DEFAULT_PPO = {
    "rollout": 128, "epochs": 4, "minibatches": 8, "lr": 3e-4, "gamma": 0.995, "lambda": 0.95,
    "clip": 0.2, "ent": [0.01, 0.002], "vf": 0.5, "maxGradNorm": 0.5, "hidden": 256, "recurrent": False,
}


def save_checkpoint(path, policy, optimizer, meta):
    torch.save({"model": policy.state_dict(), "optimizer": optimizer.state_dict() if optimizer else None,
                "obs_version": OBS_VERSION, "act_version": ACT_VERSION, **meta}, path)


def load_policy(path, device):
    ck = torch.load(path, map_location=device, weights_only=False)
    if ck.get("obs_version") != OBS_VERSION or ck.get("act_version") != ACT_VERSION:
        raise RuntimeError(f"{path}: input/action version {ck.get('obs_version')}/{ck.get('act_version')}, "
                           f"this code is {OBS_VERSION}/{ACT_VERSION} — the network must be retrained")
    policy = Policy(hidden=ck.get("hidden", 256), recurrent=ck.get("recurrent", False)).to(device)
    policy.load_state_dict(ck["model"])
    return policy, ck


class Trainer:
    def __init__(self, cfg, cfg_dir, run_dir, device, resume=None, log=print):
        self.cfg, self.cfg_dir, self.run_dir, self.device, self.log = cfg, cfg_dir, run_dir, torch.device(device), log
        self.p = {**DEFAULT_PPO, **cfg.get("ppo", {})}
        os.makedirs(run_dir, exist_ok=True)
        if resume:
            self.policy, ck = load_policy(resume, self.device)
            self.global_steps = ck.get("steps", 0)
        else:
            self.policy = Policy(hidden=self.p["hidden"], recurrent=self.p["recurrent"]).to(self.device)
            ck, self.global_steps = None, 0
        self.opt = torch.optim.Adam(self.policy.parameters(), lr=self.p["lr"], eps=1e-5)
        if ck and ck.get("optimizer"):
            try:
                self.opt.load_state_dict(ck["optimizer"])
            except ValueError:
                pass
        self.metrics = open(os.path.join(run_dir, "metrics.jsonl"), "a")
        n_params = sum(p.numel() for p in self.policy.parameters())
        self.log(f"policy: {n_params / 1e6:.2f} M parameters, device {self.device}, recurrent {self.p['recurrent']}")

    # ------------------------------------------------------------------ stages

    def run(self, only_stage=None, max_steps=None):
        stages = self.cfg["stages"]
        for si, st in enumerate(stages):
            if only_stage and st["name"] != only_stage:
                continue
            budget = int(float(max_steps if max_steps else st["steps"]))
            self.run_stage(si, st, budget)

    def make_env(self, scenarios, envs, seed):
        conf = {"scenarios": scenarios, "envs": envs, "seed": seed, "threads": self.cfg.get("threads", 0),
                "learner": self.cfg.get("learner", "team0"), "mapPool": self.cfg.get("mapPool", 128)}
        return SquadEnv(conf, base_dir=self.cfg_dir)

    def run_stage(self, si, st, budget):
        p = self.p
        env = self.make_env(st["scenarios"], self.cfg.get("envs", 64), self.cfg.get("seed", 1) + 1000 * si)
        N, T = env.slots, p["rollout"]
        dev = self.device
        names = [s if isinstance(s, str) else s["file"] for s in st["scenarios"]]
        names = [os.path.splitext(os.path.basename(n))[0] for n in names]
        self.log(f"=== stage {st['name']}: {names}, {env.num_envs} matches × {env.learners} learner slots, budget {budget / 1e6:.1f} M steps")

        obs_b = torch.zeros((T, N, env.obs_size), device=dev)
        mask_b = torch.zeros((T, N, env.mask_size), dtype=torch.uint8, device=dev)
        act_b = torch.zeros((T, N, len(env.heads)), dtype=torch.long, device=dev)
        logp_b = torch.zeros((T, N), device=dev)
        val_b = torch.zeros((T, N), device=dev)
        rew_b = torch.zeros((T, N), device=dev)
        done_b = torch.zeros((T, N), device=dev)
        alive_b = torch.zeros((T, N), device=dev)
        hid = torch.zeros((N, p["hidden"]), device=dev) if p["recurrent"] else None
        h0_b = None

        obs, masks, alive = env.reset()
        outcomes = collections.deque(maxlen=int(st.get("window", 400)))
        by_scen = collections.defaultdict(lambda: collections.deque(maxlen=200))
        ep_len = collections.deque(maxlen=400)
        steps, update, t_start = 0, 0, time.time()
        shaping_until = float(st.get("shapingAnneal", 0.6)) * budget
        promote, min_steps = st.get("promote"), float(st.get("minSteps", 0.25 * budget))

        while steps < budget:
            frac = steps / budget
            shaping = max(0.0, 1.0 - steps / max(1.0, shaping_until)) * float(st.get("shaping", 1.0))
            env.set_shaping(shaping)
            lr = p["lr"] * max(0.1, 1.0 - frac)
            for g in self.opt.param_groups:
                g["lr"] = lr
            ent_coef = p["ent"][0] + (p["ent"][1] - p["ent"][0]) * frac
            if hid is not None:
                h0_b = hid.clone()

            t_roll = time.time()
            for t in range(T):
                o = torch.from_numpy(obs).to(dev, non_blocking=True)
                mk = torch.from_numpy(masks).to(dev, non_blocking=True)
                al = torch.from_numpy(alive).to(dev).float()
                with torch.no_grad():
                    a, lp, v, hid_new = act(self.policy, o, mk, hid)
                obs_b[t], mask_b[t], act_b[t], logp_b[t], val_b[t], alive_b[t] = o, mk, a, lp, v, al
                obs, masks, rew, done, alive, info = env.step(a.cpu().numpy())
                rew_b[t] = torch.from_numpy(rew).to(dev)
                d = torch.from_numpy(done).to(dev).float()
                done_b[t] = d
                if hid is not None:
                    keep = (1 - d) * torch.from_numpy(alive).to(dev).float()
                    hid = hid_new * keep.unsqueeze(-1)
                ended = info[:, 0] == 1
                for row in info[ended]:
                    outcomes.append(row[1])
                    by_scen[int(row[7])].append(row[1])
                    ep_len.append(row[2])
            roll_time = time.time() - t_roll

            with torch.no_grad():
                _, next_v, _ = self.policy(torch.from_numpy(obs).to(dev), hid)
            adv = torch.zeros_like(rew_b)
            last = torch.zeros(N, device=dev)
            for t in reversed(range(T)):
                nv = next_v if t == T - 1 else val_b[t + 1]
                nonterm = 1.0 - done_b[t]
                delta = rew_b[t] + p["gamma"] * nv * nonterm - val_b[t]
                last = delta + p["gamma"] * p["lambda"] * nonterm * last
                adv[t] = last
            ret = adv + val_b

            stats = self.update(obs_b, mask_b, act_b, logp_b, val_b, adv, ret, alive_b, done_b, h0_b, ent_coef)
            valid = int(alive_b.sum().item())
            steps += valid
            self.global_steps += valid
            update += 1

            if update % int(self.cfg.get("logEvery", 5)) == 0 or steps >= budget:
                elapsed = time.time() - t_start
                win = float(np.mean([o > 0 for o in outcomes])) if outcomes else 0.0
                draw = float(np.mean([o == 0 for o in outcomes])) if outcomes else 0.0
                rec = {"stage": st["name"], "update": update, "steps": steps, "global_steps": self.global_steps,
                       "sps": steps / elapsed, "rollout_sps": valid / roll_time, "episodes": len(outcomes), "win": win, "draw": draw,
                       "ep_seconds": float(np.mean(ep_len)) if ep_len else 0.0, "shaping": shaping, "lr": lr,
                       "reward_per_step": float(rew_b.sum().item() / max(1, valid)),
                       "by_scenario": {names[k]: float(np.mean([o > 0 for o in q])) for k, q in by_scen.items() if q}, **stats}
                self.metrics.write(json.dumps(rec) + "\n")
                self.metrics.flush()
                self.log(f"[{st['name']}] {steps / 1e6:6.2f} M  {rec['sps']:7.0f} st/s  win {win:5.1%} draw {draw:5.1%}  "
                         f"ep {rec['ep_seconds']:4.1f}s  ent {stats['entropy']:.2f}  kl {stats['kl']:.4f}  "
                         f"vloss {stats['vloss']:.3f}  ev {stats['ev']:.2f}  shaping {shaping:.2f}  "
                         + " ".join(f"{k}:{v:.0%}" for k, v in rec["by_scenario"].items()))
            if update % int(self.cfg.get("saveEvery", 50)) == 0:
                self.save(st["name"], "latest.pt")
            if promote is not None and steps >= min_steps and len(outcomes) >= outcomes.maxlen // 2 \
                    and np.mean([o > 0 for o in outcomes]) >= promote:
                self.log(f"stage {st['name']} passed: win rate {np.mean([o > 0 for o in outcomes]):.1%} ≥ {promote:.0%} at {steps / 1e6:.2f} M steps")
                break
        self.save(st["name"], f"stage-{st['name']}.pt")
        self.save(st["name"], "latest.pt")
        env.close()

    def save(self, stage, name):
        save_checkpoint(os.path.join(self.run_dir, name), self.policy, self.opt,
                        {"steps": self.global_steps, "stage": stage, "hidden": self.p["hidden"],
                         "recurrent": self.p["recurrent"], "config": self.cfg})

    # ------------------------------------------------------------------ PPO update

    def update(self, obs_b, mask_b, act_b, logp_b, val_b, adv_b, ret_b, alive_b, done_b, h0_b, ent_coef):
        p, pol = self.p, self.policy
        T, N = alive_b.shape
        stats = collections.defaultdict(list)
        if not p["recurrent"]:
            valid = alive_b.reshape(-1) > 0
            O = obs_b.reshape(T * N, -1)[valid]
            M = mask_b.reshape(T * N, -1)[valid]
            A = act_b.reshape(T * N, -1)[valid]
            LP, V, ADV, RET = (x.reshape(-1)[valid] for x in (logp_b, val_b, adv_b, ret_b))
            n = O.shape[0]
            mb = max(1, n // p["minibatches"])
            for _ in range(p["epochs"]):
                perm = torch.randperm(n, device=O.device)
                for i in range(0, n, mb):
                    idx = perm[i:i + mb]
                    self.step_loss(O[idx], M[idx], A[idx], LP[idx], ADV[idx], RET[idx], None, ent_coef, stats)
        else:
            # recurrent: minibatches are groups of slots with their whole rollout, the GRU re-run from the stored start state
            slots = torch.arange(N, device=obs_b.device)
            per = max(1, N // p["minibatches"])
            for _ in range(p["epochs"]):
                perm = slots[torch.randperm(N, device=obs_b.device)]
                for i in range(0, N, per):
                    s = perm[i:i + per]
                    self.recurrent_loss(obs_b[:, s], mask_b[:, s], act_b[:, s], logp_b[:, s], adv_b[:, s], ret_b[:, s],
                                        alive_b[:, s], done_b[:, s], h0_b[s], ent_coef, stats)
        # explained variance on valid samples
        valid = alive_b > 0
        y, yp = ret_b[valid], val_b[valid]
        var = torch.var(y)
        ev = float(1 - torch.var(y - yp) / var) if var > 0 else 0.0
        return {k: float(np.mean(v)) for k, v in stats.items()} | {"ev": ev}

    def step_loss(self, O, M, A, LP, ADV, RET, h, ent_coef, stats):
        logp, ent, v = evaluate(self.policy, O, M, A, h)
        self.apply_loss(logp, ent, v, LP, ADV, RET, torch.ones_like(LP), ent_coef, stats)

    def recurrent_loss(self, O, M, A, LP, ADV, RET, AL, DN, h0, ent_coef, stats):
        T = O.shape[0]
        h = h0
        logps, ents, vals = [], [], []
        for t in range(T):
            lp_t, ent_t, v_t, h = self._eval_step(O[t], M[t], A[t], h)
            logps.append(lp_t); ents.append(ent_t); vals.append(v_t)
            h = h * ((1 - DN[t]) * AL[t]).unsqueeze(-1)
        logp, ent, v = torch.stack(logps), torch.stack(ents), torch.stack(vals)
        self.apply_loss(logp.reshape(-1), ent.reshape(-1), v.reshape(-1), LP.reshape(-1), ADV.reshape(-1), RET.reshape(-1),
                        AL.reshape(-1), ent_coef, stats)

    def _eval_step(self, o, m, a, h):
        from .model import masked_dists
        logits, v, h = self.policy(o, h)
        dists = masked_dists(logits, m)
        lp = sum(d.log_prob(a[:, i]) for i, d in enumerate(dists))
        ent = sum(d.entropy() for d in dists)
        return lp, ent, v, h

    def apply_loss(self, logp, ent, v, LP, ADV, RET, W, ent_coef, stats):
        p = self.p
        w = W / W.sum().clamp(min=1)
        adv = (ADV - (ADV * w).sum()) / (torch.sqrt((((ADV - (ADV * w).sum()) ** 2) * w).sum()) + 1e-8)
        ratio = torch.exp(logp - LP)
        pg = torch.max(-adv * ratio, -adv * torch.clamp(ratio, 1 - p["clip"], 1 + p["clip"]))
        loss_pg = (pg * w).sum()
        loss_v = 0.5 * (((v - RET) ** 2) * w).sum()
        loss_ent = (ent * w).sum()
        loss = loss_pg + p["vf"] * loss_v - ent_coef * loss_ent
        self.opt.zero_grad()
        loss.backward()
        nn.utils.clip_grad_norm_(self.policy.parameters(), p["maxGradNorm"])
        self.opt.step()
        with torch.no_grad():
            log_ratio = logp - LP
            stats["kl"].append(float((((ratio - 1) - log_ratio) * w).sum()))
            stats["clipfrac"].append(float((((ratio - 1).abs() > p["clip"]).float() * w).sum()))
            stats["vloss"].append(float(loss_v))
            stats["entropy"].append(float(loss_ent))
            stats["pg"].append(float(loss_pg))
