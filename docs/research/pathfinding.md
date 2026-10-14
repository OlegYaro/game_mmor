# Поиск пути на большой сетке с сотнями агентов

Дата: 2026-10-14. Контекст: Core на чистом C#, сетка 512² (MVP) → 1024² (v1), 100 жителей + 300 существ, детерминированный тик 10 Гц (≤ 8 мс на всё), тайлы меняет игрок, обрывы (Δh ≥ 2 непроходим), враги ломают стены, в v1 — армии между поселениями.

Пометки: **[факт]** — взято из первоисточника (ссылка рядом); **[оценка]** — моя прикидка, не из источника.

## Краткие выводы

1. Базой остаётся обычный A* на сетке: целочисленные стоимости 10/14, binary heap, массив узлов на всю карту с «эпохой» вместо очистки, 8‑связность без срезания углов у стен. Так устроен RimWorld (`PathFinder.cs`: 13/18 тиков, `FastPriorityQueue`, `calcGrid[]` на карту, `SearchLimit = 160000`) [[RW-PF]](#src).
2. Вопрос «достижимо ли?» надо решать не A*, а картой компонент связности: 2013‑й RimWorld — индекс зоны на клетку, сравнение за O(1) [[Ludeon-2013]](#src); современный RimWorld — регионы ≤ 12×12 + BFS по графу регионов с кэшем по парам комнат [[RW-Region]](#src); Sean Barrett (вдохновлён DF) — двухуровневые компоненты, обновление ~0.25 мс на 1024² [[stbcc]](#src).
3. Иерархия (HPA*, Botea 2004) даёт до 10× ускорение при ≤ 1 % потери оптимальности, но требует пересчёта рёбер кластера при каждом изменении стен [[HPA]](#src). Для MVP достаточно регионов как источника эвристики (так делает RimWorld после 2000 открытых узлов у не‑колонистов) [[RW-PF]](#src); полноценный HPA* — кандидат для v1 на 1024² и армий.
4. Flow fields выгодны, когда много агентов идут к одной цели (Supreme Commander 2: сектора 10×10, поля cost/integration/flow, кэш полей по порталам) [[Emerson]](#src). Для отрядов ≤ 12 это не окупается; для армий набега в v1 — да.
5. Нагрузка: лимит узлов на тик (Factorio: «constant amount of nodes per tick») [[FFF-117]](#src), кэш путей с частичным переиспользованием и негативным кэшем [[FFF-121]](#src), запрос пути не чаще раза в N тиков [оценка].
6. Детерминизм: только `int`, очередь с полным порядком (f, затем h, затем индекс), обработка запросов в порядке id. Спецификация C# прямо разрешает считать `float`/`double` с большей точностью, чем тип результата [[CS-spec]](#src).
7. Готовые C#‑библиотеки (Roy‑T/AStar, valantonini/AStar) не заявляют ни детерминизма, ни нулевых аллокаций, Roy‑T использует евклидову эвристику и `Distance`/`Velocity` [[RoyT]](#src), [[valantonini]](#src). Писать своё (~300–500 строк) [оценка].
8. Пролом стен = «мягкое препятствие»: RimWorld в режиме `PassAllDestroyableThings` добавляет `70 + 0.2 × HitPoints` к стоимости клетки с разрушаемой постройкой, дверь при `canBash` стоит 300 [[RW-PF]](#src). Это ровно то, что нужно для набегов.

## Подробности

### 1. A* на сетке

- **Очередь**: binary heap; Амит: «For priority queues, use a binary heap instead of an array or sorted array» [[Amit-impl]](#src). RimWorld — `FastPriorityQueue<CostNode>` (BlueRaja), README заявляет стабильность FIFO при равных приоритетах [[BlueRaja]](#src).
- **Память узлов**: RimWorld держит `PathFinderNodeFast[] calcGrid` на всю карту и помечает open/closed через `statusOpenValue/statusClosedValue`, которые инкрементируются каждый поиск — без обнуления массива [[RW-PF]](#src). На 1024² при ~16 Б/узел это ~16 МБ [оценка]; ужать до 8 Б (`g:int32`, `parent:int32`, статус в эпохе) — ~8 МБ [оценка].
- **Стоимости**: RimWorld — 13 (кардинал) / 18 (диагональ) тиков [[RW-PF]](#src); классика 10/14; HPA* в статье — 1 / 1.42 [[HPA]](#src). Эвристика «diagonal distance»: `D*(dx+dy) + (D2-2D)*min(dx,dy)` [[Amit-heur]](#src).
- **Срезание углов**: RimWorld запрещает диагональ, если любая из двух ортогональных клеток непроходима или дверь (`BlocksDiagonalMovement`) [[RW-PF]](#src); Botea: «We do not allow diagonal moves between two blocked tiles» [[HPA]](#src).
- **Jump Point Search** (Harabor & Grastien, AAAI 2011): без памяти, оптимален, «speedups of an order of magnitude or more» — но только для **uniform-cost** сеток [[JPS]](#src). JPS+ (Rabin, GDC 2015) — ещё ~10×, но с препроцессингом, что плохо для меняющейся карты [[JPS+]](#src). У нас стоимость клеток неравномерна (двери, пролом, местность) → JPS не подходит для общего случая.
- **Бюджет и частичные пути**: RimWorld — жёсткий `SearchLimit = 160000` клеток, при превышении `PawnPath.NotFound` [[RW-PF]](#src). Factorio — лимит узлов на тик и возобновляемый поиск в несколько тиков [[FFF-117]](#src), [[FFF-317]](#src). Botea: «the complete path is not needed—only the first few moves… refine it gradually» [[HPA]](#src).
- **Взвешенная эвристика**: RimWorld умножает h на 1.0…2.8 (люди, по дистанции 40→120) и 1.75 (животные) — жертвует оптимальностью ради скорости [[RW-PF]](#src).

### 2. HPA* (Botea, Müller, Schaeffer 2004)

- Карта режется на кластеры (в статье 10×10), на границах — «entrances» (максимальные свободные отрезки); вход шириной < 6 даёт 1 переход, иначе 2. Внутри кластера предрассчитываются intra‑edges (A* между переходами), между кластерами inter‑edges [[HPA]](#src).
- Запрос: временно вставить S и G в абстрактный граф (локальный A* до переходов своего кластера), A* по абстрактному графу, затем уточнение; «insert G once and re‑use it» для многих агентов к одной цели [[HPA]](#src).
- Результат: до 10× быстрее A*, в пределах 1 % от оптимума после сглаживания; оптимум по узлам — кластер 10 [[HPA]](#src).
- Динамика: «When the grid topology changes… the intra‑ and inter‑edges of the affected local clusters need to be re‑computed» [[HPA]](#src). DHPA*/SHPA* (Kring et al., AIIDE 2010): на самой большой карте Baldur's Gate (809 кластеров по 10) кэш HPA* — 36 488 записей, 142 КБ; DHPA* — 547 441 запись, 4.2 МБ; HPA* выигрывает, если на одну перестройку приходится ≤ 20 запросов [[DHPA]](#src).
- Память на 1024²: ~10 500 кластеров 10×10; худший случай 2n узлов и n(2n−1) intra‑рёбер на кластер [[HPA]](#src); экстраполяция числа 142 КБ на 1024² ≈ 1.5–2 МБ, плюс массив меток регионов 1 М × 4 Б = 4 МБ [оценка].
- Factorio (FFF‑317) делает похожее, но хитрее: чанки 32×32, компоненты только по **тайлам** (сущности игнорируются), «enough to remember the components for the tiles on the perimeter of each chunk»; абстрактный поиск идёт от цели (Reverse Resumable A*) и служит **эвристикой** базового A*, а не заменой [[FFF-317]](#src).

### 3. Регионы RimWorld и connectivity map Dwarf Fortress

- **2013 (Ludeon, «Reachability at last»)**: «Every walkable square in the map is marked with a zone index number», flood fill, сравнение индексов перед поиском; полная регенерация 5.5 мс на 200×200, план — перестраивать только затронутые [[Ludeon-2013]](#src).
- **Современный RimWorld** (декомпиляция): `Region.GridSize = 12` — регион это flood‑fill внутри блока 12×12 (`RegionMaker.FloodFillAndAddCells` ограничен `extentsLimit`); регионы связаны `RegionLink`, сгруппированы в `Room`. Изменение проходимости клетки → `RegionDirtyer.Notify_WalkabilityChanged` помечает регионы грязными; `RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms` лениво перестраивает только их. `Reachability.CanReach` — BFS по графу регионов (`CheckRegionBasedReachability`) с `ReachabilityCache` по парам комнат; то есть не O(1), а O(регионов) с кэшем [[RW-Region]](#src). Регионы также дают эвристику A*: после 2000 открытых узлов (не колонист) / 100 000 (колонист) включается `RegionCostCalculator`, вес растёт по кривой 1→500 [[RW-PF]](#src).
- **Dwarf Fortress**: Tarn Adams — A* «not good enough by itself»; «they can't take advantage of these innovations [precomputed structures] since our map changes so much» [[SO-DF]](#src). Баг‑трекер DF подтверждает наличие «pathfinding group» ID на тайлах [[DF-bug]](#src). Устройство структуры публично не описано; алгоритм ниже — реконструкция Barrett'а, не Adams'а.
- **stb_connected_components** (Sean Barrett, «inspired by a Dwarf Fortress programmer»): два уровня — M×M карта делится на подсетки ~√M×√M, в каждой локальные компоненты, над ними DSF для глобальных; обновление = перестроить одну подсетку + смежность + глобальный DSF. На 1024²: ~0.25 мс типично, ~2.5 мс в худшем, против ~50 мс полного пересчёта; память ~8 МБ (≈8 Б/узел) [[stbcc]](#src). Это и есть ответ «достижимо?» за O(1) с инкрементальным обновлением.
- Для обрывов: компонент связности строится по той же маске проходимости, что и A* (ребро запрещено при |Δh| ≥ 2), поэтому ничего особенного — но ребро направленное, если спуск разрешён, а подъём нет; тогда нужны компоненты сильной связности или просто запрет обоих направлений [оценка].

### 4. Flow fields (Supreme Commander 2, Elijah Emerson)

- Первоисточник — глава 23 Game AI Pro (2013); доклад GDC 2011 найти не удалось (в GDC Vault по flow fields есть «Pathing in Age of Empires IV» 2022, Frank Cheng — flow fields + hierarchical A* + steering) [[Emerson]](#src), [[AoE4]](#src).
- Устройство: сектора 10×10 клеток, порталы на границах; три поля на сектор — cost (8 бит, 255 = стена), integration (16 бит стоимость + 8 бит флагов), flow (4 бита направление + 4 бита флагов). Запрос: A* по графу порталов, затем для каждого портала — запрос на построение flow‑тайла; «merging A*» у второго и последующих агентов прилипает к уже пройденным порталам. Кэш flow‑тайлов по id портала разделяется между запросами; грязные сектора и пути перестраиваются из priority queue с фиксированным time slice [[Emerson]](#src).
- Когда выгодно: «hundreds to thousands of individual agents», одна цель на много источников [[Emerson]](#src); Chris Taylor: «even if I have 500 units selected — I create one path» [[Taylor]](#src). Для отряда ≤ 12 выгоднее один общий путь лидера + следование (см. п. 5). Полное поле на 1024² — 1 МБ (flow) + 3 МБ (integration) на цель [оценка по форматам Emerson].

### 5. Распределение нагрузки

- **Бюджет на тик**: Factorio — «Path finder can handle only a constant amount of nodes per tick»; постоянная цена старта поиска делает много коротких запросов дороже одного длинного; короткие (≤ 5 клеток) заменены на прямое движение с обходом [[FFF-117]](#src).
- **Кэш путей** (Factorio 0.13): фиксированный размер, вытеснение по «трудности вычисления», переиспользование любого **участка** кэшированного пути (запрос A→B превращается в A→X и Y→B), негативный кэш недостижимых целей [[FFF-121]](#src).
- **Группы**: HPA* — вставить G один раз и амортизировать [[HPA]](#src); Emerson — merging A* + разделяемый кэш тайлов [[Emerson]](#src).
- **RimWorld**: поиск синхронный, но ограничен 160 000 клеток и взвешенной эвристикой; нет очереди [[RW-PF]](#src).
- Для проекта [оценка]: 400 агентов, перепланирование не чаще 1 раза в 10–20 тиков (1–2 с) + по событию (изменился регион на пути) → 20–40 запросов/с, т.е. 2–4 на тик; при лимите ~20 000 узлов/тик и ~50–100 нс/узел это ≤ 2 мс.

### 6. Детерминизм

- Спецификация C# §8.3.7: «Floating‑point operations may be performed with higher precision than the result type of the operation» [[CS-spec]](#src) → в Core только `int` (10/14, 13/18 как в RimWorld).
- Тай‑брейк: Амит отмечает, что встроенные очереди «may order equal‑valued nodes differently» между языками [[Amit-impl]](#src). Ключ heap = `(f, h, nodeIndex)` — полный порядок без зависимости от реализации; порядок соседей фиксирован (кардиналы раньше диагоналей [[Amit-impl]](#src)).
- Очередь запросов: сортировка по `(priority, entityId, requestSeq)`; никаких потоков в MVP — Aron Granberg (A* Pathfinding Project): сам поиск целочисленный и детерминирован, но асинхронность ломает лок‑степ, спасает только `BlockUntilCalculated()` [[Granberg]](#src).
- Кэш путей/регионов детерминирован, если его инвалидация привязана к тику изменения тайла, а не к времени.

### 7. Готовые C#‑библиотеки

| Библиотека | Что есть | Проблемы для нас |
|---|---|---|
| Roy‑T/AStar (MIT, .NET Standard 2.0) | граф+сетка, свой `MinHeap`, `ClosestApproach`, ~10 мс на 10k узлов/40k рёбер [[RoyT]](#src) | евклидова эвристика, `Distance`/`Velocity` (float), детерминизм и аллокации не заявлены |
| valantonini/AStar (MIT) | `short[,]` сетка, 4 эвристики, лимит поиска, штраф поворотов [[valantonini]](#src) | README: «A* optimises speed over accuracy»; аллокации/детерминизм не описаны |
| BlueRaja FastPriorityQueue (MIT) | стабильный FIFO heap, используется RimWorld [[BlueRaja]](#src) | приоритет `float`; проще написать свой int‑heap на 40 строк |
| HPA* на C# | не найдено; есть Rust `hierarchical_pathfinding`, C++ HOG [[HPA-impl]](#src) | — |

Вывод: писать своё. Ядро A* с массивом на карту, int‑heap и фиксированным порядком соседей — 300–500 строк [оценка]; компоненты связности по Barrett'у — ещё ~400 [оценка].

### 8. Враги ломают стены

- RimWorld (декомпиляция `PathFinder.cs`): режим `TraverseMode.PassAllDestroyableThings` — непроходимая клетка с разрушаемой постройкой стоит `+70 + (int)(HitPoints * 0.2)`; неразрушаемая — непроходима; дверь при `canBash` — 300, дверь в режиме «ломать» — `50 + HitPoints*0.2`; при этом диагонали через такие клетки запрещены (`flag8`) [[RW-PF]](#src). Константы: `Cost_BlockedWallBase = 70`, `Cost_BlockedWallExtraPerHitPoint = 0.2f`, `Cost_DoorToBash = 300`.
- Breach‑рейды появились в 1.3 («breaking through walls with unique weapons»), в 1.3.3200 — доля breachers ~⅓ и фокус на «impassable buildings like key defensive walls» [[RW-1.3]](#src), [[RW-1.3.3200]](#src). Код 1.3 (`BreachingGrid`/`BreachingUtility`) по первоисточнику проверить не удалось — не цитирую.
- Emerson: cost field 1–254 для «дорогих» клеток, 255 — стена; «cost stamps» для построек игрока с пометкой секторов грязными [[Emerson]](#src).
- Для проекта: стоимость пролома = `breakTicks(wall, attacker) / ticksPerCardinalMove × 10` в тех же единицах, что и шаг; для набега использовать отдельную маску проходимости (стены = мягкие), поэтому и отдельный слой компонент связности «с проломом» [оценка].

## Сравнение

| Подход | Сложность реализации | Время запроса | Память на 1024² | Инкрементальность | Детерминизм |
|---|---|---|---|---|---|
| A* на сетке (int, heap, epoch‑массив) | низкая (300–500 строк) [оц.] | O(узлов); RimWorld режет 160k клеток [[RW-PF]] | ~8–16 МБ массив узлов [оц.] | не нужна (читает сетку) | да при int и полном порядке |
| + JPS | средняя | ~10× быстрее A* [[JPS]] | 0 доп. | не нужна (JPS+/goal bounding — препроцессинг) | да |
| Компоненты связности (stbcc‑style) | средняя | O(1) сравнение меток | ~8 МБ [[stbcc]] | ~0.25 мс/изменение, 2.5 мс худш. [[stbcc]] | да |
| Регионы RimWorld (12×12 + BFS + кэш) | средняя | O(регионов) BFS, с кэшем ~O(1) [[RW-Region]] | меток 4 МБ + объекты [оц.] | грязные регионы, ленивая перестройка [[RW-Region]] | да, если обход упорядочен |
| HPA* | высокая | до 10× быстрее A*, ≤ 1 % субоптимум [[HPA]] | ~1.5–2 МБ граф [оц., из 142 КБ на 320²] | пересчёт рёбер кластера [[HPA]] | да при int |
| Flow fields (сектора 10×10) | высокая | амортизируется на группу [[Emerson]] | 4 Б/клетка на активную цель [оц.] | грязные сектора + time slice [[Emerson]] | да при int‑интеграции |

## Рекомендация для проекта

**MVP (512², 400 агентов):**
1. `GridAStar`: int‑стоимости 10/14, 8‑связность, запрет диагонали при непроходимом ортогональном соседе, `NodeRecord[] nodes` на карту с `searchEpoch`, свой binary heap с ключом `(f, h, index)`, фиксированный порядок соседей, лимит узлов на запрос (например, 20 000) и возврат частичного пути до узла с минимальным h.
2. `ConnectivityMap` по схеме Barrett'а: подсетки 32×32, локальные компоненты + DSF; обновление в том же тике, что и изменение тайла; `CanReach(a,b)` = сравнение меток перед любым A*. Два слоя масок: «обычная» и «с проломом» (для набегов).
3. `PathRequestQueue`: запросы сортируются по `(priority, entityId, seq)`, обрабатываются до исчерпания бюджета узлов на тик; остаток переносится. Перепланирование агента не чаще раза в 10 тиков или по инвалидации (изменилась метка компонента/клетка на пути). Отряд ≤ 12: один путь лидера, остальные следуют со смещением.
4. Пролом стен: мягкие препятствия с `cost = 10 × ticksToBreak / ticksPerMove`, как в RimWorld `70 + 0.2×HP`.
Оценка трудоёмкости: ~1.5–2.5 недели на всё с тестами детерминизма (replay + hash состояния) [оценка]. Ожидаемая нагрузка ≤ 2 мс/тик при 2–4 запросах на тик [оценка].

**v1 (1024², армии):**
- Иерархия над компонентами: граф «компонент подсетки ↔ соседние компоненты» уже есть в ConnectivityMap — использовать его как абстрактный уровень по Factorio (обратный возобновляемый A* как эвристика) [[FFF-317]] вместо полного HPA*; это даёт дальние пути армий без пересчёта intra‑edges.
- Flow fields по секторам для армии набега к одной цели (общая цель, десятки‑сотни юнитов) [[Emerson]]; отряды ≤ 12 остаются на общем пути.
- Кэш путей по Factorio: частичное переиспользование и негативный кэш [[FFF-121]].
- JPS — только если появится слой с действительно равномерной стоимостью (напр., межпоселенческая карта без местности).

## Источники <a name="src"></a>

- [RW-PF] RimWorld, декомпилированный `Verse.AI/PathFinder.cs` (josh‑m/RW‑Decompile, ILSpy): https://github.com/josh-m/RW-Decompile/blob/master/Verse.AI/PathFinder.cs
- [RW-Region] RimWorld, `Verse/Region.cs`, `RegionMaker.cs`, `RegionDirtyer.cs`, `RegionAndRoomUpdater.cs`, `Reachability.cs` в том же репозитории: https://github.com/josh-m/RW-Decompile/tree/master/Verse
- [Ludeon-2013] Tynan Sylvester, «Reachability at last» (2013): https://ludeon.com/blog/2013/07/reachability-at-last
- [RW-1.3] RimWorld Wiki, Version 1.3: https://rimworldwiki.com/wiki/Version_1.3
- [RW-1.3.3200] Ludeon, «Update 1.3.3200 improves defense tools and raid strategies»: https://devtrackers.gg/rimworld/p/e9fa9d0a-update-1-3-3200-improves-defense-tools-and-raid-strategies
- [HPA] Botea, Müller, Schaeffer, «Near Optimal Hierarchical Path‑Finding», J. Game Dev. 2004: http://webdocs.cs.ualberta.ca/~mmueller/ps/2004/hpastar.pdf
- [DHPA] Kring, Champandard, Samarin, «DHPA* and SHPA*: Efficient Hierarchical Pathfinding in Dynamic and Static Game Worlds», AIIDE 2010: https://cdn.aaai.org/ojs/12397/12397-52-15925-1-2-20201228.pdf
- [HPA-impl] HOG (C++) HPA*: https://webdocs.cs.ualberta.ca/~nathanst/hog/classhpa_star.html ; Rust `hierarchical_pathfinding`: https://github.com/mich101mich/hierarchical_pathfinding
- [JPS] Harabor, Grastien, «Online Graph Pruning for Pathfinding on Grid Maps», AAAI 2011: https://ojs.aaai.org/index.php/AAAI/article/view/7994
- [JPS+] Steve Rabin, «JPS+: Over 100x Faster than A*», GDC 2015 (GDC Vault): https://gdcvault.com/play/1022094/JPS-Over-100x-Faster-than
- [stbcc] Sean Barrett, «Connected Components Algorithm» (stb_connected_components.h): https://stb.handmade.network/blog/p/1136
- [SO-DF] Stack Overflow Blog, интервью с Tarn Adams (2021): https://stackoverflow.blog/2021/12/31/700000-lines-of-code-20-years-and-one-developer-how-dwarf-fortress-is-built/
- [DF-bug] DF bug tracker #10838 («pathfinding group»): https://dwarffortressbugtracker.com/view.php?id=10838
- [Emerson] Elijah Emerson, «Crowd Pathfinding and Steering Using Flow Field Tiles», Game AI Pro, гл. 23 (PDF): https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf
- [Taylor] Engadget, интервью Chris Taylor о Supreme Commander 2 (2009): https://www.engadget.com/2009-06-09-interview-chris-taylor-on-supreme-commander-2-part-two.html
- [AoE4] Frank Cheng, «Pathing in Age of Empires IV: Flow Fields and Steering Behaviors», GDC 2022 (GDC Vault): https://gdcvault.com/play/1027659/Pathing-in-Age-of-Empires
- [FFF-117] Factorio, Friday Facts #117 «Path Finder Optimisation I»: https://www.factorio.com/blog/post/fff-117
- [FFF-121] Factorio, Friday Facts #121 «Path Finder Optimisation II»: https://www.factorio.com/blog/post/fff-121
- [FFF-317] Factorio, Friday Facts #317 «New pathfinding algorithm»: https://www.factorio.com/blog/post/fff-317
- [Amit-impl] Amit Patel, «Implementation of A*»: https://www.redblobgames.com/pathfinding/a-star/implementation.html
- [Amit-heur] Amit Patel, «Heuristics»: https://theory.stanford.edu/~amitp/GameProgramming/Heuristics.html
- [CS-spec] C# Language Specification §8.3.7 Floating‑point types: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/types#837-floating-point-types
- [Granberg] A* Pathfinding Project forum, «Pathfinding for deterministic simulation»: https://forum.arongranberg.com/t/pathfinding-for-deterministic-simulation/53
- [RoyT] Roy‑T/AStar: https://github.com/roy-t/AStar
- [valantonini] valantonini/AStar: https://github.com/valantonini/AStar
- [BlueRaja] High‑Speed Priority Queue for C#: https://github.com/BlueRaja/High-Speed-Priority-Queue-for-C-Sharp
