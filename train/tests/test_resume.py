"""--continue picks up a stage where it stopped: step count, schedules and win window survive, a broken latest.pt falls
back to the previous one, a finished run does nothing.

    python train/tests/test_resume.py
"""
import json
import os
import shutil
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "train"))

import torch  # noqa: E402

from squad import ppo  # noqa: E402

CFG = {"envs": 8, "threads": 2, "seed": 3, "learner": "team0", "mapPool": 8, "logEvery": 1, "saveEvery": 2,
       "ppo": {"rollout": 16, "epochs": 1, "minibatches": 2, "hidden": 32},
       "stages": [{"name": "aim", "scenarios": [os.path.join(ROOT, "sim", "scenarios", "train-aim.json")],
                   "steps": 1500, "promote": None, "shapingAnneal": 0.5}]}


class Stop(Exception):
    pass


def rows(run):
    with open(os.path.join(run, "metrics.jsonl")) as f:
        return [json.loads(line) for line in f]


def main():
    run = tempfile.mkdtemp(prefix="squad-resume-")
    try:
        torch.manual_seed(0)
        t = ppo.Trainer(CFG, ROOT, run, "cpu", log=lambda m: None)
        real, calls = t.update, [0]

        def update(*a, **k):
            calls[0] += 1
            if calls[0] == 6:
                raise Stop()
            return real(*a, **k)
        t.update = update
        try:
            t.run()
        except Stop:
            pass
        ck = torch.load(os.path.join(run, "latest.pt"), weights_only=False)
        pos = ck["position"]
        assert pos["update"] == 4 and not pos["done"] and pos["steps"] > 0, pos
        assert ppo.verified(os.path.join(run, "latest.pt")) and os.path.exists(os.path.join(run, "latest.prev.pt"))
        before = rows(run)

        t2 = ppo.Trainer(CFG, ROOT, run, "cpu", log=lambda m: None, cont=True)
        assert t2.position["steps"] == pos["steps"] and t2.global_steps == ck["steps"]
        t2.run()
        after = rows(run)[len(before):]
        first = after[0]
        budget = CFG["stages"][0]["steps"]
        assert first["update"] == pos["update"] + 1, first
        assert first["steps"] > pos["steps"], (first["steps"], pos["steps"])
        # the schedules continue from the saved step count instead of starting over
        lr_expected = 3e-4 * max(0.1, 1.0 - pos["steps"] / budget)
        assert abs(first["lr"] - lr_expected) < 1e-9, (first["lr"], lr_expected)
        assert first["shaping"] < 1.0
        assert after[-1]["steps"] >= budget
        status = json.load(open(os.path.join(run, "status.json")))
        assert status["state"] == "done", status
        ck2 = torch.load(os.path.join(run, "latest.pt"), weights_only=False)
        assert ck2["position"]["done"]

        # a finished run continued again does nothing
        n = len(rows(run))
        ppo.Trainer(CFG, ROOT, run, "cpu", log=lambda m: None, cont=True).run()
        assert len(rows(run)) == n

        # a torn latest.pt is skipped in favour of latest.prev.pt
        with open(os.path.join(run, "latest.pt"), "r+b") as f:
            f.seek(100)
            f.write(b"broken")
        msgs = []
        t3 = ppo.Trainer(CFG, ROOT, run, "cpu", log=msgs.append, cont=True)
        assert any("checksum mismatch" in m for m in msgs), msgs
        assert t3.position is not None
        print("resume: OK")
    finally:
        shutil.rmtree(run, ignore_errors=True)


if __name__ == "__main__":
    main()
