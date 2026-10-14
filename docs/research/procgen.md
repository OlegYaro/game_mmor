# Процедурная генерация мира без зависимости от движка (исследование, 14.10.2026)

Контекст: Core на чистом C#, мир 512² в MVP (1024²/2048² позже, 1024² ≤ 30 с), один seed → побайтово тот же мир на macOS arm64 и Windows x64. Конвейер: высоты (0–3) → влажность/температура → биомы → реки → ресурсы → точки интереса → поселения → стартовая точка.

## Краткие выводы

1. **Шум: FastNoiseLite (MIT, v1.1.1 от 05.03.2024) — официальный C#-порт лежит прямо в репозитории (`CSharp/FastNoiseLite.cs`), один файл, без NuGet.** Копируем файл в Core. Он на `float` (переключается на `double` одной строкой), fixed-point версии нет. Из `System.Math` использует только `Math.Sqrt` (Cellular), FMA/SIMD не использует, хэш — целочисленный (seed ^ x·PrimeX ^ y·PrimeY, умножение на 0x27d4eb2d). [S1][S2][S3]
2. **Детерминизм float между arm64 и x64 реален при условиях:** базовые операции IEEE 754 (+ − × ÷ √) округляются одинаково на любом совместимом железе при том же порядке операций; ломают его FMA-контракция, трансцендентные функции (`Math.Sin`…) и расширенная точность x87. В .NET FMA — только явный вызов `Math.FusedMultiplyAdd` («rounded as one ternary operation»). Спецификация C# формально разрешает повышенную точность, поэтому обязателен CI-тест «золотой хэш» на обеих платформах. [S4][S5][S6]
3. **Патент Perlin на симплекс-шум (US 6867776 B2) истёк 08.01.2022** — Simplex можно использовать; OpenSimplex2 (CC0) и так патент не затрагивал. [S7][S8]
4. **Биомы — таблица «высота × влажность(× температура)»** как у Amit Patel (адаптированный Whittaker) и Dwarf Fortress (elevation → rainfall × drainage), а не nearest-point в 6D как в Minecraft. Гладкие границы: низкочастотные поля (red noise) + мажоритарный фильтр, порог только по сглаженному полю. [S9][S10][S11][S12]
5. **Реки: D8-спуск по непрерывной высоте с предварительным priority-flood** (Barnes 2014, O(n log n)) — гарантирует каждому тайлу строго нисходящий путь к воде/краю; 4 ступени высоты хранить отдельно от float-высоты, иначе плато без градиента. [S13][S14][S15]
6. **Объекты: Bridson Poisson-disk (O(N), k=30) с масками по биому; жилы — Worley/Cellular F1 + CellValue; леса — клеточный автомат «4-5» с flood-fill связности.** [S16][S17][S18][S19]
7. **Старт и «злые ≥120»: фиксированный детерминированный список кандидатов + скоринг (как Minecraft: 66 точек на кольцах, затем 862 точки тонкого поиска), злые поселения ставить после старта с маской расстояния — гарантия по построению.** [S20]
8. **Производительность: 1024² ≤ 30 с — с большим запасом.** FastNoiseLite в C++ даёт ~71 М сэмплов/с OpenSimplex2 2D (i7-7820X); при ~15–20 сэмплах/тайл и 1 М тайлов это ~0.3 с в C++ и единицы секунд в C# даже без параллелизма. Параллелить по чанкам 64×64 с под-seed'ами из хэша (seed, stage, cx, cy). [S21]
9. **Хэш карты — `XxHash3.HashToUInt64` из `System.IO.Hashing` 10.0.13 (13.10.2026)** по массивам тайлов в фиксированном порядке полей; хранить `WorldGenVersion` и не обещать совместимость seed между версиями (Factorio и Minecraft её не держат). [S22][S23][S24][S25]

## Подробности

### 1. Шумовые функции на чистом C#

- **FastNoiseLite** (Auburn): MIT; порты C#, C++, C, Java, Rust, Go, JS и др. в одном репозитории; последний релиз v1.1.1 — 05.03.2024 (правки портов C/GLSL/Rust). Типы: OpenSimplex2, OpenSimplex2S, Perlin, Cellular (Voronoi), Value, ValueCubic; фракталы `FBm`, `Ridged`, `PingPong`, `DomainWarpProgressive`, `DomainWarpIndependent`; domain warp `OpenSimplex2`, `OpenSimplex2Reduced`, `BasicGrid`. В C#-файле `using FNLfloat = System.Single;` (закомментирован вариант `System.Double`). Официального NuGet нет (VL.FastNoiseLite — обёртка для vvvv, Tedd.FastNoise — сторонний SIMD-порт). [S1][S2][S3]
- **Хэш-шум и детерминизм.** Градиенты выбираются целочисленным хэшем координат решётки и seed (`Hash(seed, xPrimed, yPrimed) = (seed ^ xPrimed ^ yPrimed) * 0x27d4eb2d`), октавы меняют seed (`seed++`). Целочисленная часть детерминирована везде; float-часть (интерполяция, скалярные произведения) состоит из + − × и `Math.Sqrt`, которые IEEE 754 требует округлять корректно — результат одинаков на любом CPU, если компилятор не меняет порядок операций и не сливает `a*b+c` в FMA. [S3][S4]
- **Fixed-point версии нет** ни у FastNoiseLite, ни у OpenSimplex2. Принять float с оговоркой: (а) не использовать `Math.Sin/Cos/Pow/Exp` в генерации, (б) не вызывать `Math.FusedMultiplyAdd`/`Vector<T>`, (в) золотой хэш в CI на macOS arm64 и Windows x64. Если тест когда-то разойдётся — план Б: квантовать выход шума в `int` (например, ×4096) сразу после сэмпла и все дальнейшие стадии вести в целых. [S4][S5][S6]
- **OpenSimplex2** (KdotJPG): CC0 (часть каталогов Unlicense); `csharp/OpenSimplex2.cs` — `static class`, `long seed`, координаты `double`, выход `float`, вызовов `System.Math` нет; варианты `Noise2`, `Noise2_ImproveX`, `Noise3_ImproveXY`…; последний коммит 29.01.2024. 2S («smooth») — для ridged-шума. Без фракталов и domain warp — их надо писать самому. [S8]
- **Simplex vs Perlin vs OpenSimplex2.** Perlin — сеточные артефакты по осям; Simplex — быстрее и без них, патент истёк 08.01.2022; OpenSimplex2 — визуально как Simplex, свободен изначально. Для 2D-карты разница между ними косметическая; FastNoiseLite даёт все три одним API. [S7][S8]
- **Фрактальные октавы** (Red Blob): сумма шумов с удвоением частоты и амплитудами `1, 1/2, 1/4…` (persistence/gain), нормировка на сумму амплитуд, редистрибуция `pow(e, k)` для плоских долин; октавы сдвигать по seed. [S10]
- **Domain warping** (Quilez): `f(p) = fbm(p + fbm(p))`, второй уровень `fbm(p + fbm(p + fbm(p)))`; в FastNoiseLite — `SetDomainWarpType` + `DomainWarp(ref x, ref y)` перед `GetNoise`. Даёт «органичные» берега вместо круглых пятен. [S26]

```csharp
// Core/WorldGen/Noise.cs — вся работа с шумом через один файл FastNoiseLite.cs (MIT), float.
var n = new FastNoiseLite(seedHeight);
n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
n.SetFractalType(FastNoiseLite.FractalType.FBm); n.SetFractalOctaves(5); n.SetFractalGain(0.5f);
n.SetFrequency(1f / 128f);
var w = new FastNoiseLite(seedHeight ^ 0x5bd1e995); w.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
w.SetDomainWarpAmp(24f); w.SetFrequency(1f / 256f);
float x = tx, y = ty; w.DomainWarp(ref x, ref y);
float h01 = n.GetNoise(x, y) * 0.5f + 0.5f;           // [0,1]
```

### 2. Биомы по высоте / влажности / температуре

- **Whittaker (1975)** — диаграмма «средняя температура × осадки» → 9 биомов; это первоисточник всех игровых таблиц. [S9]
- **Amit Patel, «Polygonal Map Generation»:** высота = расстояние от берега (значит, спуск всегда ведёт к океану), влажность = расстояние до пресной воды, таблица 4 зоны высоты × 6 зон влажности (SNOW/TUNDRA/BARE/SCORCHED … TROPICAL RAIN FOREST/…/SUBTROPICAL DESERT); значения редистрибуируются под целевое распределение (`y = 1-(1-x)^2`). Высота там — прокси температуры. [S11]
- **Amit Patel, «Making maps with noise»:** два независимых поля (elevation, moisture с разными seed), `biome(e, m)` каскадом порогов; пороги «надо тюнить под игру». Для деревьев — не высокочастотный шум, а Poisson disc / jittered grid. [S10]
- **Dwarf Fortress:** сначала elevation (0–99 океан, 300–400 горы), внутри 100–299 — таблица rainfall × drainage (например, Swamp: rainfall 66–100, drainage 0–32; Forest: rainfall 66–100, drainage 33–100; Grassland: rainfall 10–19), затем температурные замены (≤ −5 → тундра/ледник, −4…9 → тайга). Порядок генерации (по интервью Toady One): базовые поля (elevation, rainfall, temperature, drainage, volcanism, wildness) → растительность/биомы → «средние высоты сглаживаются в равнины» → эрозия и реки → дождевые тени → цивилизации. [S12][S27]
- **Minecraft 1.18+:** 6 параметров (temperature, humidity, continentalness, erosion, weirdness, depth), биом = интервал в 6D-пространстве, при промахе — ближайший; рельеф — сплайны от continentalness/erosion/PV. Для нас избыточно, но идея «те же шумы задают и рельеф, и биом» полезна: температура = f(широта, высота), влажность = f(шум, близость воды). [S28]
- **Гладкие границы без «шахматки».** Причина шахматки — пороги по высокочастотному полю. Приёмы: (1) поля влажности/температуры делать низкочастотными (red/pink noise, Red Blob: «для ландшафта — pink/red noise»); (2) порог применять к полю, а не к сумме с «детальной» октавой; (3) после классификации — мажоритарный фильтр 3×3 (1–2 прохода) или правило «пиксель биома меняется, только если ≥5 из 8 соседей другие» (тот же CA «4-5»); (4) DF-приём: сглаживать средние высоты до классификации. [S29][S18]

```csharp
static byte Biome(byte h, float moist, float temp) {         // MVP: 4 биома
    if (h == 0) return Water;                                  // ступень 0 — вода/побережье (позже)
    if (h == 3) return Mountains;
    if (moist < 0.30f) return Wasteland;                        // позже: temp < 0.2 → Tundra
    return moist > 0.55f ? Forest : Meadow;                    // позже: moist > .8 && h == 1 → Swamp
}
```

### 3. Реки

- **Red Blob 2010:** реки идут от случайных вершин в горах по самому крутому нисходящему ребру до берега; поток суммируется (A+B при слиянии), ширина ~ √flow. Связность гарантируется тем, что высота — расстояние от берега: локальных минимумов нет. [S11]
- **Red Blob 2017 (drainage basins):** обратный подход — выращивать дерево рек от края/океана внутрь (гибрид BFS и Дейкстры с шумовым приоритетом), Strahler-порядок для ширины; связность по построению, но высоту под такие реки подогнать трудно — автор перешёл к другой схеме в mapgen4. [S13]
- **Dwarf Fortress:** «фальшивые реки» стартуют на склонах гор и текут вниз, прорезая канал в поле высот, если не находят путь к морю. [S27]
- **Дискретная сетка с 4 ступенями:** D8 (O'Callaghan & Mark 1984) направляет клетку к самому низкому из 8 соседей; при равенстве — первый рассмотренный, при отсутствии нижнего — тупик. С высотой 0–3 почти вся карта — плато, поэтому: хранить `float heightF` для гидрологии и `byte height` для геймплея; перед трассировкой прогнать **priority-flood** (Barnes et al. 2014: заливка от краёв через priority queue, O(n log n) для float; выход без впадин, у каждой клетки есть путь вниз); с `epsilon`-вариантом плоскости получают монотонный уклон. [S14][S15]
- **Гарантия на практике:** после priority-flood с epsilon по `heightF` каждый сухопутный тайл имеет строго нисходящего соседа → трассировка D8 завершается за ≤ N шагов в океане (h==0) или на краю карты. Истоки — top-K тайлов по аккумуляции потока среди `height==3`; река «прорезает» ступень: тайлы русла получают флаг `River`, а не меняют `height` (чтобы не ломать биомы).

```csharp
// Flow direction after priority-flood: deterministic tie-break = fixed neighbour order.
static readonly (int dx,int dy)[] N8 = {(1,0),(0,1),(-1,0),(0,-1),(1,1),(-1,1),(-1,-1),(1,-1)};
int Downhill(int i) { int best = -1; float bh = hf[i];
    foreach (var (dx,dy) in N8) { int j = Idx(X(i)+dx, Y(i)+dy); if (j >= 0 && hf[j] < bh) { bh = hf[j]; best = j; } }
    return best; }   // best == -1 only for ocean/edge cells after flood-fill
```

### 4. Размещение объектов

- **Poisson disk (Bridson 2007, «Fast Poisson Disk Sampling in Arbitrary Dimensions», SIGGRAPH sketches):** фоновая сетка с ячейкой `r/√2`, активный список, `k = 30` попыток в кольце `[r, 2r]`, O(N). Детерминизм: свой PRNG (например, SplitMix64 на под-seed) и выбор из активного списка по индексу от PRNG, а не `HashSet`-порядок. Ограничения: маска биома (отбрасываем кандидата вне допустимых биомов) и переменный `r` по типу объекта (POI: `r ≈ 40` даёт ~1 на 40×40; поселения: `r_faction`). Red Blob рекомендует именно Poisson disc/jittered grid для объектов. [S16][S10]
- **Жилы ресурсов — Worley/Cellular (1996):** F1 = расстояние до ближайшей случайной точки; `F1 < порог` → пятно, `CellValue` → тип руды пятна. В FastNoiseLite: `NoiseType.Cellular`, `CellularReturnType.Distance`/`CellValue`, `SetCellularJitter`. Внутри пятна — порог по второму мелкому шуму для «рваных» краёв. Аналог в Factorio («spot noise», FFF-258): карта режется на регионы, в каждом — список случайных точек с density/radius/favorability, точки берутся по убыванию favorability до целевого количества. [S17][S19]
- **Леса — клеточный автомат (RogueBasin):** 45 % заполнение, правило «4-5» (стена, если ≥5 из 9 в 3×3), 4–5 итераций; вариант с 5×5 для меньших «дыр»; связность — flood-fill, малые компоненты отбрасывать. Для леса: семя = `moist > t` плюс blue-noise шум, 2–3 итерации — получаются «опушки», а не шахматка. [S18]

### 5. Стартовая точка и «злые ≥ 120»

- **Minecraft (референс детерминированного перебора):** 66 фиксированных координат на кольцах радиусов 0/512/1024/1536/2048, скоринг = расстояние от (0,0) + выход климата за пороги; затем 862 точки на 16 кольцах с шагом 32 вокруг лучшей; затем первый «topsolid» блок в радиусе 5 чанков. Список кандидатов фиксирован → результат детерминирован. [S20]
- **Для проекта:** кандидаты = тайлы с шагом 4 по всей карте (≈16 К на 512²), порядок обхода фиксирован (y, затем x). Скоринг в радиусе 40 (целочисленное `dx²+dy² ≤ 1600`): `forest ≥ F`, `stone ≥ S`, `water ≥ W`, нет `Mountains`-стены вокруг, нет врагов. Берём лучший по (score, затем y, x). Если ни один не проходит — ослабляем пороги по фиксированной лестнице, а не меняем seed (иначе seed «врёт»).
- **Гарантия «злые не ближе 120» — по построению:** старт выбирается до фракций; затем злые поселения размещаются Poisson-disk'ом с маской `dist²(start) ≥ 120²` и собственным `r`; дружественные — без этой маски. Тест проверяет инвариант, а не обеспечивает его.

### 6. Производительность и параллелизм

- **Оценка сэмплов на тайл:** высота 5 октав + warp (~3 сэмпла) ≈ 8; влажность 4; температура 2; cellular для руд 1–2; blue-noise для деревьев 1 → ~16–18 сэмплов/тайл. 1024² = 1 048 576 тайлов → ~18 М сэмплов. FastNoiseLite C++ (clang, i7-7820X 4.9 GHz, README FastNoise2): OpenSimplex2 2D 71.3 М/с, Perlin 2D 92.8 М/с, Cellular 2D 39.2 М/с → ~0.3 с; C#-порт обычно в 2–4 раза медленнее → ~1 с однопоточно. Priority-flood 1 М клеток — доли секунды; CA 5 проходов по 1 М — доли секунды; Poisson O(N). **Итог: 1024² за ≤ 30 с реалистично с 10-кратным запасом, 2048² (4.2 М) — ~4–6 с однопоточно.** Опубликованных бенчмарков C#-порта нет — замерить BenchmarkDotNet'ом на первой итерации. [S21]
- **Где параллелить без потери детерминизма:** стадии «чистой функции от (seed, x, y)» (шум, warp, классификация биомов, cellular) — по чанкам 64×64, каждый поток пишет в свой диапазон массива; результат не зависит от расписания. Стадии с глобальным состоянием (priority-flood, трассировка рек, Poisson disk, старт) — последовательно или с фиксированным порядком редукции. Под-seed'ы: `Derive(seed, stageId, cx, cy)` через хэш, никогда — один `Random` на всех.

```csharp
static ulong Derive(ulong seed, uint stage, int cx = 0, int cy = 0) {      // SplitMix64-style mixer
    ulong z = seed ^ ((ulong)stage << 48) ^ ((ulong)(uint)cx << 24) ^ (uint)cy;
    z += 0x9E3779B97F4A7C15UL; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return z ^ (z >> 31); }
```

### 7. Хэш карты и версионирование генератора

- **`System.IO.Hashing`** (NuGet 10.0.13, 13.10.2026; есть сборка под netstandard2.0): `XxHash3.HashToUInt64(ReadOnlySpan<byte>, long seed)`, `XxHash64`, `XxHash128`, `Crc32/64`; байтовый вывод — big-endian. Хэшировать `MemoryMarshal.AsBytes` каждого массива в фиксированном порядке полей (height, biome, river, resource, poi, settlements, start), склеивая через `Append`. Для теста без зависимостей достаточно FNV-1a (16 строк), но xxHash3 в разы быстрее на мегабайтах. [S22][S23]
- **Factorio:** map exchange string начинается с 4-байтовой версии игры и содержит seed + настройки, но не логику генератора; разработчики отказались держать старые версии генератора («colossal task with minimal benefit»); 2.0.7 — «map generation system reworked». [S24][S30]
- **Minecraft:** «обновления алгоритма генерации означают, что тот же seed больше не даёт ту же местность»; уже сгенерированные чанки сохраняются, новые — новым генератором (отсюда «швы» на границах чанков); текстовые seed — `String.hashCode()` (32 бита), числовые — 64 бита. [S25]
- **Вывод для проекта:** `const int WorldGenVersion` в Core; в сейве — `(Seed, WorldGenVersion, MapHash)`; золотые хэши в тестах — по одному на версию (`seed=1 → 0x…`); при изменении пайплайна версия растёт и золотой хэш обновляется осознанно; совместимость seed между версиями не обещать (сейв хранит готовый мир, а не seed).

### 8. Открытые C#-референсы (не зависимости)

- `Auburn/FastNoiseLite` → `CSharp/FastNoiseLite.cs` (MIT) — берём как есть. [S1]
- `KdotJPG/OpenSimplex2` → `csharp/OpenSimplex2.cs` (CC0) — запасной шум/сверка. [S8]
- `jongallant/WorldGeneratorFinal` (MIT, Unity C#, последний коммит 02.05.2019) — полный мир: height/heat/moisture → реки → таблица биомов heat × moisture; хорош как структура кода, но Unity-зависим. [S31]
- `SebLague/Procedural-Landmass-Generation` (MIT, Unity C#) — октавы, falloff, чанки. [S32]
- `Scrawk/Procedural-Noise` (MIT, Unity C#) — Perlin/Value/Simplex/Voronoi/Worley в едином фрактальном каркасе. [S33]
- `SebLague/Poisson-Disc-Sampling` (MIT, C#) — компактный Bridson. [S34]
- Не-C#, но эталон алгоритмов: исходники к «Polygonal Map Generation» (mapgen2) и mapgen4 Amit Patel. [S11][S13]

## Сравнение шумовых библиотек

| Библиотека | Лицензия | Последний релиз/коммит | C# порт | Детерминизм | Замечания |
|---|---|---|---|---|---|
| FastNoiseLite (Auburn) | MIT | v1.1.1, 05.03.2024 | Официальный, в репо (`CSharp/`) | int-хэш + float (+ − × √), без FMA/SIMD, без `Sin/Cos` | Один файл; FBm/Ridged/PingPong, domain warp, Cellular; `float`↔`double` одной строкой; NuGet официального нет |
| OpenSimplex2 (KdotJPG) | CC0 (часть Unlicense) | коммит 29.01.2024 | Официальный (`csharp/`) | int-хэш (64-бит простые), `double` вход/`float` выход, без `System.Math` | Только базовый шум: фракталы/warp писать самому; 2S — для ridged |
| FastNoise2 (Auburn) | MIT | активен | Только C++ ядро + binding (`FastNoise2Bindings`) | SIMD (AVX2/NEON) — порядок редукций отличается по платформам | Нативная зависимость, для кросс-платформенного побайтового равенства не подходит |
| Scrawk/Procedural-Noise | MIT | без релизов | Да (Unity) | обычный float | Учебный, Unity-зависим; Worley есть |
| Tedd.FastNoise (NuGet) | — | 1.0.5 | Да | SIMD-версия FastNoiseLite | Сверяется с FastNoiseLite на равенство, но лицензию/поддержку проверить до использования |

## Рекомендация для проекта

**Конвейер (все стадии — чистые функции от `WorldGenParams{Seed, Size, Version}`; шум — `FastNoiseLite.cs` в `Core/WorldGen/Noise/`):**

1. `HeightField`: domain warp + OpenSimplex2 FBm (5 октав) → `float[] heightF` в [0,1]; редистрибуция `pow(e,1.3)`; island-mask по краю (`lerp(e, 1-d, 0.6)`), чтобы океан был на краю и реки имели сток. Квантование: `byte[] height = Quantize(heightF, thresholds[3])`.
2. `Climate`: `float[] moisture` (низкочастотный FBm 3–4 октавы + бонус у воды после рек), `float[] temperature` (широта + `−k·heightF` + слабый шум). Параллельно по чанкам.
3. `Biomes`: таблица `Biome(height, moisture, temperature)` + 1–2 прохода мажоритарного фильтра 3×3 → `byte[] biome`.
4. `Rivers`: priority-flood (epsilon) по `heightF` → D8 → аккумуляция потока → истоки top-K в `height==3` → трассировка → `byte[] river` (флаг + ширина 1–3 по √flow); `moisture += falloff(dist to river)` и пересчёт биомов в полосе ±3.
5. `Resources`: Cellular F1/CellValue для руд/камня, CA «4-5» для плотности леса внутри `Forest`, blue-noise для одиночных деревьев → `byte[] resource` (+ `ushort[] amount`).
6. `PointsOfInterest`: Bridson `r=40`, маска «суша, не река», `List<Poi>`.
7. `Start`: перебор кандидатов шагом 4, скоринг в радиусе 40 → `(int x,int y) start`.
8. `Settlements`: дружественные — Poisson `r=90`; злые — Poisson `r=150` с маской `dist²(start) ≥ 120²` → `List<Settlement{Faction, Pos}>`.
9. `Finalize`: `MapHash = XxHash3(height|biome|river|resource|poi|settlements|start)`; сохраняем `(Seed, Version, MapHash)`.

**Структуры данных:** struct-of-arrays `WorldMap { int W,H; byte[] height; float[] heightF; float[] moisture, temperature; byte[] biome; byte[] river; byte[] resource; List<Poi> pois; List<Settlement> settlements; Point start; ulong mapHash; }` — индексация `y*W+x`, никаких `Dictionary` в горячих стадиях. Все расстояния — целочисленные квадраты. PRNG — собственный `SplitMix64`/xoshiro на под-seed'ах (не `System.Random`: его алгоритм менялся между версиями .NET).

**Тесты:** (а) золотой хэш `seed=1,2,3` на macOS arm64 и Windows x64 в CI; (б) инварианты: каждая речная клетка имеет нисходящего соседа или выходит в воду/край; `min dist(start, evil) ≥ 120`; в радиусе 40 от старта есть лес, камень, вода; ровно ~`W·H/1600` POI ± 20 %; (в) бенчмарк 1024² ≤ 30 с однопоточно (ожидаемо ≤ 5 с).

## Источники

- [S1] Auburn/FastNoiseLite — README (порты, лицензия MIT): https://github.com/Auburn/FastNoiseLite
- [S2] FastNoiseLite v1.1.1 (05.03.2024): https://github.com/Auburn/FastNoiseLite/releases/tag/v1.1.1
- [S3] FastNoiseLite.cs (C#, `FNLfloat`, хэш, `Math.Sqrt`): https://raw.githubusercontent.com/Auburn/FastNoiseLite/master/CSharp/FastNoiseLite.cs
- [S4] Glenn Fiedler, Floating Point Determinism (IEEE 754, FMA, x87, трансцендентные): https://gafferongames.com/post/floating_point_determinism/
- [S5] Math.FusedMultiplyAdd — «rounded as one ternary operation»: https://learn.microsoft.com/en-us/dotnet/api/system.math.fusedmultiplyadd
- [S6] Спецификация C#, типы с плавающей точкой («may be performed with higher precision than the result type»): https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/types
- [S7] US 6867776 B2 «Standard for Perlin Noise», expired 2022-01-08: https://patents.google.com/patent/US6867776B2/en
- [S8] KdotJPG/OpenSimplex2 (CC0, csharp/): https://github.com/KdotJPG/OpenSimplex2 ; https://raw.githubusercontent.com/KdotJPG/OpenSimplex2/master/csharp/OpenSimplex2.cs
- [S9] Whittaker, Communities and Ecosystems (1970/1975) — обзор: https://en.wikipedia.org/wiki/Biome
- [S10] Amit Patel, Making maps with noise functions: https://www.redblobgames.com/maps/terrain-from-noise/
- [S11] Amit Patel, Polygonal Map Generation for Games (2010, обновл. 2025): http://www-cs-students.stanford.edu/~amitp/game-programming/polygon-map-generation/
- [S12] Dwarf Fortress wiki, Biome (пороги rainfall × drainage, 40d): https://dwarffortresswiki.org/index.php/40d:Biome ; текущая: https://dwarffortresswiki.org/index.php/Biome
- [S13] Amit Patel, Procedural river drainage basins (2017): https://redblobgames.com/x/1723-procedural-river-growing/
- [S14] Barnes, Lehman, Mulla, Priority-Flood (Computers & Geosciences 62, 2014): https://arxiv.org/abs/1511.04463
- [S15] D8 (O'Callaghan & Mark 1984), RichDEM flow metrics: https://richdem.readthedocs.io/en/latest/flow_metrics.html
- [S16] Bridson, Fast Poisson Disk Sampling in Arbitrary Dimensions (SIGGRAPH 2007 sketch): https://www.cs.ubc.ca/~rbridson/docs/bridson-siggraph07-poissondisk.pdf
- [S17] Worley, A Cellular Texture Basis Function (SIGGRAPH 1996): https://history.siggraph.org/?p=117007
- [S18] RogueBasin, Cellular Automata Method for Generating Random Cave-Like Levels: http://www.roguebasin.com/index.php/Cellular_Automata_Method_for_Generating_Random_Cave-Like_Levels
- [S19] Factorio FFF-258 New autoplace (spot noise): https://www.factorio.com/blog/post/fff-258
- [S20] Minecraft wiki, World spawn (алгоритм выбора): https://minecraft.wiki/w/World_spawn
- [S21] FastNoise2 README, Library Comparisons (М точек/с, FastNoiseLite vs FastNoise2): https://github.com/Auburn/FastNoise2
- [S22] NuGet System.IO.Hashing 10.0.13: https://www.nuget.org/packages/System.IO.Hashing
- [S23] XxHash3 Class (System.IO.Hashing): https://learn.microsoft.com/en-us/dotnet/api/system.io.hashing.xxhash3
- [S24] Factorio wiki, Map exchange string format (4-байтовая версия в начале): https://wiki.factorio.com/Map_exchange_string_format
- [S25] Minecraft wiki, Seed (level generation): https://minecraft.wiki/w/Seed_(level_generation)
- [S26] Inigo Quilez, Domain warping: https://iquilezles.org/articles/warp/
- [S27] Dwarf Fortress wiki, World generation (порядок фаз, реки): https://dwarffortresswiki.org/index.php/World_generation
- [S28] Minecraft wiki, World generation (1.18+ multi-noise): https://minecraft.wiki/w/World_generation
- [S29] Amit Patel, Noise functions and map generation (red/pink/blue noise): https://www.redblobgames.com/articles/noise/introduction.html
- [S30] Factorio forums, ответ разработчика про старые версии генератора: https://forums.factorio.com/viewtopic.php?p=387391 ; wiki Map generator: https://wiki.factorio.com/Map_generator
- [S31] jongallant/WorldGeneratorFinal (MIT): https://github.com/jongallant/WorldGeneratorFinal
- [S32] SebLague/Procedural-Landmass-Generation (MIT): https://github.com/SebLague/Procedural-Landmass-Generation
- [S33] Scrawk/Procedural-Noise (MIT): https://github.com/Scrawk/Procedural-Noise
- [S34] SebLague/Poisson-Disc-Sampling (MIT): https://github.com/SebLague/Poisson-Disc-Sampling
- Примечание: официального заявления .NET о том, что RyuJIT не сливает `a*b+c` в FMA автоматически, в найденных источниках нет (есть лишь сторонняя статья https://siboehm.com/articles/23/Inlining-FMA-FP-consistency с таким утверждением) — поэтому золотой хэш на двух платформах обязателен. Опубликованных бенчмарков C#-порта FastNoiseLite не найдено; цифры в п. 6 — C++ из [S21] плюс оценка.
