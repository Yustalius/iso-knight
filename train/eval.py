"""Evaluate a checkpoint on scenarios with held-out seeds: win/draw/loss with a 95% interval, optional replays.

    python train/eval.py --checkpoint runs/c1/latest.pt --scenario sim/scenarios/train-duel-gen.json --matches 400
    python train/eval.py --checkpoint ... --scenario ... --record out/replays --record-count 4
    dotnet run --project sim/Squad.Tools -c Release -- render --replay out/replays/ep0.rpl --every 3
"""
import argparse
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402
import torch  # noqa: E402

from squad.env import SquadEnv  # noqa: E402
from squad.model import act  # noqa: E402
from squad.ppo import load_policy  # noqa: E402


def wilson(p, n, z=1.96):
    if n == 0:
        return 0.0, 0.0
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return max(0.0, c - h), min(1.0, c + h)


def evaluate(policy, scenarios, matches, device, envs=32, seed=1_000_003, greedy=False, threads=0, record=None, record_count=0,
             base_dir="."):
    env = SquadEnv({"scenarios": scenarios, "envs": min(envs, matches), "seed": seed, "threads": threads}, base_dir=base_dir)
    if record:
        os.makedirs(record, exist_ok=True)
        for e in range(min(record_count, env.num_envs)):
            env.record(e, os.path.join(record, f"ep{e}.rpl"))
    obs, masks, alive = env.reset()
    hid = torch.zeros((env.slots, policy.trunk[2].out_features), device=device) if policy.recurrent else None
    outcomes, lengths = [], []
    while len(outcomes) < matches:
        with torch.no_grad():
            a, _, _, h2 = act(policy, torch.from_numpy(obs).to(device), torch.from_numpy(masks).to(device), hid, greedy=greedy)
        obs, masks, rew, done, alive, info = env.step(a.cpu().numpy())
        if hid is not None:
            keep = torch.from_numpy((1 - done) * alive).to(device).float()
            hid = h2 * keep.unsqueeze(-1)
        for row in info[info[:, 0] == 1]:
            outcomes.append(row[1])
            lengths.append(row[2])
    env.close()
    o = np.array(outcomes[:matches])
    return {"matches": len(o), "win": float((o > 0).mean()), "draw": float((o == 0).mean()), "loss": float((o < 0).mean()),
            "seconds": float(np.mean(lengths[:matches]))}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--scenario", action="append", required=True)
    ap.add_argument("--matches", type=int, default=200)
    ap.add_argument("--envs", type=int, default=32)
    ap.add_argument("--seed", type=int, default=1_000_003, help="held-out seeds: far from the training ones")
    ap.add_argument("--greedy", action="store_true", help="argmax instead of sampling")
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--threads", type=int, default=0)
    ap.add_argument("--record", default=None, help="directory for replays of the first episodes")
    ap.add_argument("--record-count", type=int, default=4)
    a = ap.parse_args()
    policy, ck = load_policy(a.checkpoint, torch.device(a.device))
    policy.eval()
    print(f"checkpoint {a.checkpoint}: stage {ck.get('stage')}, {ck.get('steps', 0) / 1e6:.1f} M steps")
    for sc in a.scenario:
        r = evaluate(policy, [os.path.abspath(sc)], a.matches, torch.device(a.device), a.envs, a.seed, a.greedy, a.threads,
                     a.record, a.record_count)
        lo, hi = wilson(r["win"], r["matches"])
        print(f"  {os.path.basename(sc):26s} {r['matches']} matches: win {r['win']:.1%} [{lo:.0%}–{hi:.0%}]  "
              f"draw {r['draw']:.1%}  loss {r['loss']:.1%}  mean {r['seconds']:.1f} s")
        a.record = None   # replays only for the first scenario


if __name__ == "__main__":
    main()
