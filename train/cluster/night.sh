#!/bin/sh
# Stage 2 of docs/train-t4.md inside the pod: branches A (MLP, from the trial duel checkpoint) and B (GRU, whole
# curriculum) side by side, one GPU each, then the held-out panel. Safe to rerun after a pod restart: train.py
# --continue picks up from latest.pt, finished runs are skipped, finished evaluations are kept.
set -u
R=${RUNS:-/work/squad/runs}
A=$R/${RUN_A:-A}
B=$R/${RUN_B:-B}
mkdir -p "$A" "$B"
echo "=== night $(date -Is) bundle $BUNDLE restart ${RESTART:-?}" | tee -a "$A/job.out" "$B/job.out"
python train/train.py --config train/configs/duel-long.json --resume train/checkpoints/trial-cpu/stage-duel.pt --continue \
  --run "$A" --device cuda:0 --threads "${THREADS_A:-8}" --envs "${ENVS:-256}" >> "$A/job.out" 2>&1 &
pa=$!
python train/train.py --config train/configs/curriculum-gru.json --continue \
  --run "$B" --device cuda:1 --threads "${THREADS_B:-8}" --envs "${ENVS:-256}" >> "$B/job.out" 2>&1 &
pb=$!
wait $pa; ra=$?
wait $pb; rb=$?
echo "train exit: A $ra, B $rb"
[ $ra -eq 0 ] && [ $rb -eq 0 ] || exit 1
# the trial checkpoint is the baseline on the same seeds; it is measured once, in A's panel
[ -f "$A/eval.json" ] || python train/cluster/eval_all.py --checkpoint "$A/latest.pt" \
  --checkpoint train/checkpoints/trial-cpu/stage-duel.pt --matches "${EVAL_MATCHES:-1000}" --device cuda:0 \
  --threads 9 --out "$A/eval.json" >> "$A/job.out" 2>&1 &
pa=$!
[ -f "$B/eval.json" ] || python train/cluster/eval_all.py --checkpoint "$B/latest.pt" \
  --matches "${EVAL_MATCHES:-1000}" --device cuda:1 --threads 9 --out "$B/eval.json" >> "$B/job.out" 2>&1 &
pb=$!
wait $pa || exit 1
wait $pb || exit 1
echo "=== night done $(date -Is)" | tee -a "$A/job.out" "$B/job.out"
