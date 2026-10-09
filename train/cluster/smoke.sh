#!/bin/sh
# Stage 1 of docs/train-t4.md inside the pod: environment, smoke test, a check against the trial results, a speed grid.
# Everything lands in $OUT; summary.json is the machine-readable result.
set -u
OUT=${OUT:-/work/squad/smoke/$BUNDLE}
mkdir -p "$OUT"
exec >> "$OUT/log.txt" 2>&1
echo "=== smoke $(date -Is) bundle $BUNDLE"
nvidia-smi --query-gpu=index,name,memory.used,memory.total --format=csv
echo "nproc $(nproc)  cpu.max $(cat /sys/fs/cgroup/cpu.max 2>/dev/null)  mem.max $(cat /sys/fs/cgroup/memory.max 2>/dev/null)"
python -c "import torch, numpy; print('torch', torch.__version__, 'cuda', torch.cuda.is_available(), torch.cuda.device_count(), 'numpy', numpy.__version__)"
python train/tests/smoke.py || { echo "SMOKE FAILED"; exit 1; }

echo "=== trial checkpoint on held-out seeds (trial run: train-duel-gen 79 %)"
python train/eval.py --checkpoint train/checkpoints/trial-cpu/stage-duel.pt --scenario sim/scenarios/train-duel-gen.json \
  --matches 400 --device cuda:0 --threads 16 || exit 1

speed() {  # name config stage threads envs device
  rm -rf "$OUT/speed/$1"
  python train/train.py --config "$2" --stage "$3" --steps 1.5e6 --threads "$4" --envs "$5" --device "$6" \
    --run "$OUT/speed/$1" > "$OUT/speed-$1.out" 2>&1
}
echo "=== speed grid"
speed mlp-t16 train/configs/duel-long.json duel 16 256 cuda:0
speed mlp-t8 train/configs/duel-long.json duel 8 256 cuda:0
speed gru-t16 train/configs/curriculum-gru.json duel 16 256 cuda:1
speed mlp-t9-pair train/configs/duel-long.json duel 9 256 cuda:0 &
speed gru-t9-pair train/configs/curriculum-gru.json duel 9 256 cuda:1 &
wait
python - "$OUT" <<'PY'
import json, os, sys
out = sys.argv[1]
res = {}
for name in sorted(os.listdir(os.path.join(out, "speed"))):
    rows = [json.loads(l) for l in open(os.path.join(out, "speed", name, "metrics.jsonl"))]
    res[name] = {"sps": rows[-1]["sps"], "rollout_sps": rows[-1]["rollout_sps"], "steps": rows[-1]["steps"]}
    print(f"{name:14s} {res[name]['sps']:8.0f} st/s  rollout {res[name]['rollout_sps']:8.0f}")
json.dump(res, open(os.path.join(out, "summary.json"), "w"), indent=1)
PY
echo "=== smoke done $(date -Is)"
