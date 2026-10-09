# Пробный прогон на CPU (4 ядра, 64 матча)

Чекпоинты программы `train/configs/curriculum-1v1.json`, вход/действия версии 1 (см. docs/train.md).

| Файл | Стадия | Шагов всего |
|---|---|---|
| `stage-aim.pt` | манекены, 99 % побед | 1,0 млн |
| `stage-turret.pt` | турель easy/normal 71 % / 44 % | 2,4 млн |
| `stage-duel.pt` | 1×1 против ботов: 79 % на сгенерированных картах, 71 % на duel | 4,4 млн |

Продолжить в облаке: `python train/train.py --config train/configs/curriculum-1v1.json --stage duel --resume train/checkpoints/trial-cpu/stage-duel.pt --device cuda:0`.
Оценить: `python train/eval.py --checkpoint train/checkpoints/trial-cpu/stage-duel.pt --scenario sim/scenarios/train-duel-gen.json --matches 400`.
Логи и метрики — `log-*.txt`, `metrics-*.jsonl`.
