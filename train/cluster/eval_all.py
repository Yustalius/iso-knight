"""Evaluate checkpoints on the held-out panel of docs/train-t4.md and write eval.json beside each (atomically).

    python train/cluster/eval_all.py --checkpoint runs/A/latest.pt --checkpoint train/checkpoints/trial-cpu/stage-duel.pt \
        --matches 1000 --device cuda:0 --threads 8 --out runs/A/eval.json
"""
import argparse
import json
import os
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "train"))

import torch  # noqa: E402

from eval import evaluate, wilson  # noqa: E402
from squad.ppo import load_policy, write_atomic  # noqa: E402

PANEL = ["train-duel", "train-duel-gen"] + [f"eval-duel-{b}" for b in
                                            ("rifleman", "holder", "hunter", "kiter", "marksman", "flanker", "rusher")]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--checkpoint", action="append", required=True)
    ap.add_argument("--matches", type=int, default=1000)
    ap.add_argument("--envs", type=int, default=64)
    ap.add_argument("--device", default="cuda:0" if torch.cuda.is_available() else "cpu")
    ap.add_argument("--threads", type=int, default=0)
    ap.add_argument("--panel", nargs="*", default=PANEL)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    result = {"time": time.time(), "matches": a.matches, "seed": 1_000_003, "checkpoints": {}}
    for ck_path in a.checkpoint:
        policy, ck = load_policy(ck_path, torch.device(a.device))
        policy.eval()
        rows = {}
        for name in a.panel:
            t0 = time.time()
            r = evaluate(policy, [os.path.join(ROOT, "sim", "scenarios", name + ".json")], a.matches, torch.device(a.device),
                         a.envs, threads=a.threads)
            r["ci"] = wilson(r["win"], r["matches"])
            rows[name] = r
            print(f"{os.path.basename(ck_path):24s} {name:22s} win {r['win']:.1%} [{r['ci'][0]:.1%}–{r['ci'][1]:.1%}] "
                  f"draw {r['draw']:.1%}  {time.time() - t0:.0f} s", flush=True)
        result["checkpoints"][ck_path] = {"steps": ck.get("steps", 0), "stage": ck.get("stage"), "panel": rows}
        write_atomic(a.out, json.dumps(result, indent=1))


if __name__ == "__main__":
    main()
