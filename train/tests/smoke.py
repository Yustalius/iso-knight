"""Smoke test of the native environment without a network: shapes, NaNs, determinism, speed.

    python train/tests/smoke.py [--steps 3000] [--envs 64]
"""
import argparse
import os
import sys
import time

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "train"))
from squad.env import SquadEnv, random_actions  # noqa: E402


def run(scenario, envs, steps, seed=1, learner="team0"):
    env = SquadEnv({"scenarios": [scenario], "envs": envs, "seed": seed, "learner": learner}, base_dir=ROOT)
    obs, masks, alive = env.reset()
    assert obs.shape == (env.slots, env.obs_size) and masks.shape == (env.slots, env.mask_size)
    rng = np.random.default_rng(seed)
    checksum, ended, outcomes = 0.0, 0, []
    t0 = time.perf_counter()
    for t in range(steps):
        obs, masks, rew, done, alive, info = env.step(random_actions(masks, env.heads, rng))
        assert np.isfinite(obs).all() and np.isfinite(rew).all(), "NaN in observations or rewards"
        checksum += float(obs.sum()) * ((t % 5) + 1) + float(rew.sum())
        fin = info[:, 0] == 1
        ended += int(fin.sum())
        outcomes += info[fin, 1].tolist()
    dt = time.perf_counter() - t0
    env.close()
    return checksum, ended, outcomes, steps * env.slots / dt, env


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--steps", type=int, default=2000)
    ap.add_argument("--envs", type=int, default=64)
    a = ap.parse_args()
    for sc in ("sim/scenarios/train-aim.json", "sim/scenarios/train-duel-gen.json"):
        c1, ended, outs, sps, env = run(sc, a.envs, a.steps)
        c2, *_ = run(sc, a.envs, min(a.steps, 300))
        c3, *_ = run(sc, a.envs, min(a.steps, 300))
        assert c2 == c3, "two runs with the same seed differ"
        wins = sum(o > 0 for o in outs)
        print(f"{os.path.basename(sc):24s} slots {env.slots:4d}  obs {env.obs_size}  heads {env.heads}  "
              f"{sps:9.0f} agent-steps/s  episodes {ended:5d}  random-policy wins {wins / max(1, ended):.0%}")
    # self-play layout: every soldier is a learner slot
    _, ended, _, sps, env = run("sim/scenarios/team-4v4-mixed.json", 8, 300, learner="all")
    print(f"{'team-4v4 self-play':24s} slots {env.slots:4d}  {sps:9.0f} agent-steps/s  episodes {ended}")
    print("smoke OK")


if __name__ == "__main__":
    main()
