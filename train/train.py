"""Train a soldier with PPO through a curriculum of scenarios.

    python train/train.py --config train/configs/curriculum-1v1.json --device cuda:0 --run runs/c1
    python train/train.py --config ... --stage aim --steps 3e6 --device cpu        # one stage, own budget
    python train/train.py --config ... --resume runs/c1/latest.pt --stage duel      # start a stage from these weights
    python train/train.py --config ... --run runs/c1 --continue                     # pick up exactly where runs/c1 stopped

Two GPUs: start two runs (different --seed or --config) with --device cuda:0 and cuda:1. The simulation runs on the CPU
cores (config "threads", 0 = all), so with two runs give each half the cores.
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import torch  # noqa: E402

from squad.ppo import Trainer  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--device", default="cuda:0" if torch.cuda.is_available() else "cpu")
    ap.add_argument("--run", default=None, help="output directory (default runs/<config>-<time>)")
    ap.add_argument("--stage", default=None, help="train only this stage")
    ap.add_argument("--steps", default=None, help="step budget overriding the stage's (e.g. 3e6)")
    ap.add_argument("--resume", default=None, help="start from these weights; stage schedules start fresh")
    ap.add_argument("--continue", dest="cont", action="store_true",
                    help="if the run directory has a whole latest.pt, carry on from it exactly (stage, schedules); "
                         "otherwise start as usual. Safe to pass on every launch of a restartable job")
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--envs", type=int, default=None)
    ap.add_argument("--threads", type=int, default=None, help="CPU threads for the simulation")
    a = ap.parse_args()

    with open(a.config) as f:
        cfg = json.load(f)
    for k in ("seed", "envs", "threads"):
        if getattr(a, k) is not None:
            cfg[k] = getattr(a, k)
    torch.manual_seed(cfg.get("seed", 1))
    if a.device == "cpu":
        # the simulation and the network take turns, so both may use every core
        torch.set_num_threads(max(1, int(cfg.get("torchThreads", os.cpu_count() or 1))))
    name = os.path.splitext(os.path.basename(a.config))[0]
    run = a.run or os.path.join("runs", f"{name}-{time.strftime('%Y%m%d-%H%M%S')}")
    os.makedirs(run, exist_ok=True)
    with open(os.path.join(run, "config.json"), "w") as f:
        json.dump(cfg, f, indent=2)
    log_file = open(os.path.join(run, "log.txt"), "a")

    def log(msg):
        line = f"{time.strftime('%H:%M:%S')} {msg}"
        print(line, flush=True)
        log_file.write(line + "\n")
        log_file.flush()

    trainer = Trainer(cfg, os.path.dirname(os.path.abspath(a.config)), run, a.device, resume=a.resume, log=log, cont=a.cont)
    try:
        trainer.run(only_stage=a.stage, max_steps=a.steps)
    except BaseException as e:
        trainer.write_status({"state": "error", "error": f"{type(e).__name__}: {e}"})
        raise
    log(f"done: checkpoints in {run}")


if __name__ == "__main__":
    main()
