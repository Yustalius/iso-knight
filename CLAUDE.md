# iso-knight — инструкции для Claude

Прототип изометрической игры. Отвечать пользователю по-русски.

- `sim/` — правила игры и боты на C# (.NET 8), без графики: основа будущей игры на Godot 4 и обучения ботов
  (замысел — `docs/bots.md`, устройство и API — `docs/sim.md`).
- `index.html` + `src/` — визуальный прототип рыцаря на three.js 0.180 (ES-модули, без сборки), только референс.

## Симуляция `sim/`

- Проверка: `cd sim && dotnet test Squad.Tests`. Инструменты: `dotnet run --project Squad.Tools -c Release -- <mapgen|render|arena|crosstab|bench|replay>`
  (`docs/sim.md`). Кадры `render`/`mapgen` пишутся в `sim/out/` (в .gitignore) — смотреть их глазами после правок поведения ботов.
- `Squad.Sim` не зависит ни от чего (ни Godot, ни пакетов). В правилах только `DMath` (не `Math.Sin/Cos/Atan2/Exp/Log/Pow`),
  случайность только из `Rng` в состоянии матча, обход только массивами по индексу — иначе ломаются реплеи и кроссплатформенность.
  `Step` не должен выделять память (тест `StepAllocatesNothing`, `bench`): структуры, пулы, `Span`, generic-визиторы вместо лямбд.
- Боты (`Squad.Bots`) видят только `AgentView`. Новое знание о мире для ботов — поле в `AgentView` (поднять `AgentView.Version`),
  а не доступ к внутреннему состоянию матча. Всевидение — только флаг `Omniscient` для упражнений.
- Числа баланса — в `Balance` (переопределяются из сценария), не в коде правил. После правок ботов или баланса прогнать `arena`
  по затронутым сценариям и `crosstab`: нет таймаутов-стоялок, нет стратегии, которая выигрывает у всех.
- Если менялись правила, обновить эталон в `Golden.cs` только при осознанной смене `DMath`; хэши реплеев меняются вместе с правилами.

## Обучение `train/` + `sim/Squad.Train`

- Устройство и запуск — `docs/train.md`. Среда (`VecEnv`, `Codec`) — C#, собирается Native AOT в `train/native/squad.so`
  (`train/build_native.sh`, в .gitignore); Python (`train/squad`) зовёт её через ctypes, буферы — numpy без копий.
- После правок C# пересобрать библиотеку и прогнать `python3 train/tests/smoke.py` (numpy, без torch) и `dotnet test`.
- Вход сети — только из `AgentView` (тест `HiddenEnemyDoesNotLeakIntoTheInput`). Любое изменение раскладки входа или
  действий: поднять `Obs.Version`/`Act.Version` в `Codec.cs` и `OBS_VERSION`/`ACT_VERSION` в `train/squad/env.py`,
  поправить константы в `train/squad/model.py`.
- Новое в экспортах: исключения не должны выходить из `[UnmanagedCallersOnly]` (try/catch → `squad_error`).
  Всё, что читается рефлексией (JSON → поля), должно быть в `TrimmerRootAssembly`, иначе AOT молча обрежет поля.
- torch в облачной среде Claude: только `pip install torch` с PyPI целиком (~5 ГБ, CUDA-библиотеки нужны даже для CPU),
  в отдельный venv в scratchpad; индекс CPU-сборок pytorch.org закрыт.

## Прототип рыцаря (three.js)

### Как проверять изменения

1. `npm run check` — синтаксис всех модулей.
2. `npm start &` — сервер на :8777 (three.js из `vendor/`, сеть не нужна).
3. Скриншоты: `npm i` (ставит playwright), при необходимости `npx playwright install chromium`, затем
   `PLAN='[...]' node tools/shots.cjs shots`. На Linux используется встроенный Chromium; `PW_CHANNEL=chrome` — системный Chrome.
   План — массив шагов: `{"reset":[x,z,yawDeg]}` ставит героя, `{"view":[camYawDeg, zoomMeters]}` — камера,
   `{"input":{"mx":1,"mz":0,"run":true,"block":false}}` — удерживаемый ввод (экранные оси), `{"attack":1}` — удар,
   `{"step":0.3}` — промотать симуляцию (60 Гц), `{"name":"x","clip":{...}}` — сохранить `shots/x.png`.
   Пример: `PLAN='[{"reset":[0.3,1.4,90],"view":[0,3.2]},{"attack":1,"step":0.19,"name":"windup"}]'`.
   `python -I tools/sheet.py out.png shots/*.png` собирает контактный лист (нужен Pillow).
4. Смотреть кадры глазами перед тем, как считать анимацию готовой. Ошибки страницы печатаются в конце вывода shots.

### Соглашения

- 1 единица = 1 м, +Y вверх, персонаж смотрит в +Z, правая рука (меч) на −X, левая (щит) на +X.
- Камера: ортографическая, возвышение 30°, позиция привязана к пиксельной сетке рендера.
- Анимация процедурная (`src/knight-rig.js`), клипов нет. Новые движения — ключи в `ATTACKS` или слои в `update()`.
- Палитра мира берётся из текстур рыцаря (`makeMaterial`/`makeTexture` в `knight-model.js`), новые объекты делать так же: 64×64, NearestFilter, flatShading.
- `reference/` — чужой эталон, не редактировать.
