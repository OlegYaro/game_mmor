# ИИ жителей: доска задач, резервирование, utility, приоритеты (14.10.2026)

## Краткие выводы

1. **RimWorld — это не utility AI, а строгие ярусы.** Пешка берёт всю работу приоритета 1, прежде чем посмотреть на приоритет 2; внутри яруса — порядок колонок вкладки «Работы» слева направо, внутри типа работы — порядок WorkGiver'ов; расстояние учитывается только на последнем шаге (ближайшая достижимая цель). Вики прямо предупреждает: «They have no regard for efficiency». Для нашего GDD (приоритеты 1–4) это и есть базовая модель. [RimWorld Work](https://rimworldwiki.com/wiki/Work)
2. **Резервирование — отдельный менеджер, а не флаг на предмете.** В RimWorld `WorkGiver` делает только проверку `CanReserve()`, а само резервирование выполняет `JobDriver.TryMakePreToilReservations()`; если оно не удалось, работа не стартует. Сигнатура `pawn.Reserve(target, job, maxPawns = 1, stackCount = -1, layer = null)` показывает три измерения: сколько пешек, сколько единиц стопки, слой (вещь/клетка/слот). [Mehni WorkGiver](https://github.com/Mehni/ExampleJob/wiki/1b)-Job-Assignment-via-WorkGiver), [Mehni JobDriver](https://github.com/Mehni/ExampleJob/wiki/4)-JobDriver)
3. **Недостижимость режется до A\*.** Тайнан ещё в 2013 г. ввёл индексы регионов: «If they don't match, don't try to path». Без этого пешки зависают у запертой вещи, а A\* сканирует всю карту. Нам нужна та же проверка `RegionOf(a) == RegionOf(b)` до выбора цели. [Ludeon: Reachability at last](https://ludeon.com/blog/2013/07/reachability-at-last)
4. **ONI: ярус → суб-приоритет → скрытый порядок → (опционально) близость; текущее поручение не прерывается более приоритетным.** Это хороший компромисс между «строго по приоритету» и «ближайшее». [ONI Priority](https://oxygennotincluded.wiki.gg/wiki/Priority), [ONI Errand](https://oxygennotincluded.wiki.gg/wiki/Errand)
5. **Utility (Dave Mark, IAUS) нужен не для выбора яруса, а для ранжирования внутри яруса и для потребностей.** Рецепт: нормализованные входы 0–1 → response curve → перемножение; против осцилляций — бонус текущему действию, cooldown и runtime-считалки (Graham, Lewis). Dill: «rank + weight» (двойная полезность) формализует «ярус строго, внутри — по весу». [Graham, GAIP ch.9](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter09_An_Introduction_to_Utility_Theory.pdf), [Lewis, GAIP3 ch.13](http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter13_Choosing_Effective_Utility-Based_Considerations.pdf), [Dill, GAIP2 ch.3](http://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter03_Dual-Utility_Reasoning.pdf)
6. **Тело задачи — линейный список шагов (toils) с условиями провала, не GOAP.** RimWorld строит список toils один раз при старте и каждый тик проверяет `FailOn*`; GOAP (Оркин) ценен для динамического перепланирования в шутере, а не для «подойти → взять → отнести». [Mehni Toils](https://github.com/Mehni/ExampleJob/wiki/5)-Toils), [Orkin, GDC 2006](https://pages.cs.wisc.edu/~dyer/cs540/handouts/gdc2006_orkin_jeff_fear.pdf)
7. **Потребности: порог старта с запасом + гистерезис через расписание.** RimWorld: еда — начать есть при 30 % (сам статус «голоден» наступает при 25 %), в блоке Sleep ложиться при отдыхе < 75 %, в Anything — < 30 %, просыпаться при 100 %; в блоке Work отдых игнорируется, еда — нет. [Saturation](https://rimworldwiki.com/wiki/Saturation), [Schedule](https://rimworldwiki.com/wiki/Schedule), [Rest](https://rimworldwiki.com/wiki/Rest)
8. **Референсы на C#:** DwarfCorp (MIT-код, `TaskManager` с cost-матрицей и `Act`-деревьями как корутинами) и Project Porcupine (GPL-3, `JobManager.GetJob` — ярус приоритета, потом минимальное время пути). Оба — для чтения, не как зависимость. [DwarfCorp TaskManager](https://raw.githubusercontent.com/Blecki/dwarfcorp/master/DwarfCorp/TaskManagement/TaskManager.cs), [Porcupine JobManager](https://raw.githubusercontent.com/OrderOfThePorcupine/ProjectPorcupine/master/Assets/Scripts/Models/Job/JobManager.cs)

## Подробности

### 1. RimWorld

- **Приоритеты.** Ручной режим: 1 (высший) … 4, пусто = не делать. «All available work of one priority will be done before the next priority». При равном приоритете — колонка левее. Внутри типа работы WorkGiver'ы упорядочены полем `priorityInType`: «Tasks higher on the list will be prioritized over tasks lower on the list in the same work-type». Порядок колонок на вики: Firefight, Patient, Doctor, Bed rest, Childcare, Basic, Warden, Handle, Cook, Hunt, Construct, Grow, Mine, Plant cut, Smith, Tailor, Art, Craft, Fish, Haul, Clean, Dark study, Research. Навык влияет на скорость и отображение, но не на выбор задачи. Ручные приказы идут отдельной очередью и прерывают текущую работу. [Work](https://rimworldwiki.com/wiki/Work)
- **Цепочка классов.** ThinkTree (XML-дерево `ThinkNode_*`) → `JobGiver_Work` → `WorkGiver_Scanner` (`PotentialWorkThingRequest` / `HasJobOnThing` / `JobOnThing`, `ShouldSkip`) → `Job(JobDef, targets)` → `JobDriver` (`TryMakePreToilReservations`, `MakeNewToils`) → `Toil` (`initAction`/`tickAction`, `Toils_Goto`, `Toils_Haul`). ThinkTree «there for stateless decisions, not for tasks with a lot of state»; задачи из него «will be cancelled with little-to-no notification». Toils строятся один раз: «All Toils are evaluated only once: when the job is started», циклы — через `Toils_Jump`. Провал — декларативно: `FailOnDespawnedNullOrForbidden`, `FailOnDowned`, `FailOnCannotTouch`. [Mehni JobGiver](https://github.com/Mehni/ExampleJob/wiki/1a)-Job-Assignment-via-JobGiver), [Mehni JobDriver](https://github.com/Mehni/ExampleJob/wiki/4)-JobDriver), [Mending job](https://rimworldwiki.com/wiki/Modding_Tutorials/Code_MendingJob)
- **Резервирование.** «Pawns must do a .CanReserve() check before being assigned the job. They'll do the actual reservation in the JobDriver». Пример `this.pawn.Reserve(target, this.job, 1, -1, null)`: `maxPawns` (сколько пешек могут делить цель, например кровать на двоих или стопка еды), `stackCount` (–1 = вся стопка), `layer` (`ReservationLayerDef`: отдельно резервируется вещь и отдельно клетка, чтобы строитель и носильщик не мешали друг другу на одной клетке). Резервации снимаются при завершении/провале работы. [Mehni WorkGiver](https://github.com/Mehni/ExampleJob/wiki/1b)-Job-Assignment-via-WorkGiver)
- **Расстояние.** Выбор ближайшей цели делается внутри WorkGiver через поиск по регионам (`GenClosest.ClosestThingReachable`, BFS по регионам) — поэтому пешка берёт «ближайшее в текущем ярусе», а не «ближайшее вообще». [Reachability at last](https://ludeon.com/blog/2013/07/reachability-at-last)
- **«Jobs of opportunity».** Попутная переноска в ваниле ограничена; её расширяют моды («While You're Up», «Pick Up and Haul» — пример `WorkGiver HaulToInventory` разобран в вики Mehni). Страницу мода открыть не удалось (HTTP 429), детали не подтверждены.
- **Застревания.** Типовой симптом в отчётах игроков — «started 10 jobs in one tick»: защита движка, которая ловит цикл «взять работу → провалить на первом тике → снова взять». Причины: исключение в `JobDriver`, цель формально доступна, но `JobOnThing` возвращает работу, которую нельзя начать (нет резервации, запрещён предмет, нет пути). Страницы с этими отчётами я видел только в поисковой выдаче Steam-обсуждений, не открывал. Декомпиляция для изучения разрешена EULA с оговорками; публичные зеркала (Chillu1/RimWorldDecompiled) отдают HTTP 451. [Decompiling](https://rimworldwiki.com/wiki/Modding_Tutorials/Decompiling_source_code)
- **Доклад Тайнана** (GDC 2017, «Contrarian, Ridiculous, and Impossible Design Methods») — о «story generator», а не об ИИ; для job-системы не пригодился. [GDC Vault](https://gdcvault.com/play/1024232/-RimWorld-Contrarian-Ridiculous-and)

### 2. Dwarf Fortress / ONI / Prison Architect / Going Medieval

- **Dwarf Fortress.** Нет ярусов приоритета: labor — жёсткий выключатель («completely refuse to do unassigned labors»); «an idle dwarf with that labor enabled will be assigned the job». Инструменты (кирка/топор) исключают друг друга. Вики честно пишет: «Prioritization of work is based on several factors that are not fully understood». Есть/спать — не labor, создаются автоматически. Вывод: модель «идлер берёт первую подходящую» порождает хаотичные пробежки; нам нужна явная оценка. [DF Job](https://dwarffortresswiki.org/index.php/DF2014:Job)
- **Oxygen Not Included.** Цепочка выбора: (1) персональный уровень по категории (very high … very low, disabled), (2) суб-приоритет здания 1–9, (3) скрытый встроенный порядок, (4) при включённом «Enable Proximity» — ближайшее среди равных. По умолчанию расстояние не учитывается. «High priority errands are always performed before low priority errands», но «keeps working on the current errand until it's complete, even if a higher priority errand becomes available». Потребности (туалет, еда, сон) всегда выше работы; отключает их только Red Alert; Yellow Alert игнорирует расписание, кроме сна. 15 категорий: Combat, Build, Care, Cook, Decorate, Dig, Farm, Life Support, Operate, Ranching, Research, Storage, Supply, Tidy, Toggle. Апдейт OC-253538 (2018): «Priorities are now 1-9», «Roles now affect priority within a single priority level». [Priority](https://oxygennotincluded.wiki.gg/wiki/Priority), [Errand](https://oxygennotincluded.wiki.gg/wiki/Errand), [OC-253538](https://oxygennotincluded.wiki.gg/wiki/Versions/OC-253538), [Duplicant](https://oxygennotincluded.wiki.gg/wiki/Duplicant)
- **Prison Architect.** Профессия привязана к комнате: игрок открывает слоты в Logistics → Prison Labour, «No jobs will be assigned automatically only due to the existence of the rooms», нужна пройденная программа обучения; работа только в блоке Work режима дня. Рабочие (workmen) ходят на стройки, при недостижимости «marked with a red X» со счётчиком попыток, при опасности стройка ставится на паузу. Это ближайший аналог нашего «занял место — получил профессию». [Prison Labour](https://prisonarchitect.paradoxwikis.com/Prison_Labour), [Workman](https://prisonarchitect.paradoxwikis.com/Workman)
- **Going Medieval.** Копия RimWorld: число меньше — приоритет выше, 16 типов (Tend, Convalesce, Hunt, Construction, Grow, Harvest, Mine, Cut plants, Cooking, Craft, Smithing, Carpentry, Tailoring, Research, Steward, Haul), яркость рамки = навык, звезда = любимая работа (мораль). [Going Medieval Jobs](https://eip.gg/going-medieval/guides/jobs/)

### 3. Utility AI

- **Формула.** Graham: входы нормализуются в 0–1 («normalized scores combine very easily»), каждый пропускается через response curve (линейная, квадратичная, логистическая, кусочная — «exactly what The Sims uses»), факторы комбинируются (среднее, произведение, max/min, веса). Lewis (IAUS): «These scores are then multiplied together to obtain the overall score»; расстояние нормализуется «bookends» (0 м = 0, 100 м = 1, выше — 1). Выбор — максимум или взвешенный случайный среди топ-N / в пределах 10 % от лучшего. [Graham](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter09_An_Introduction_to_Utility_Theory.pdf), [Lewis](http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter13_Choosing_Effective_Utility-Based_Considerations.pdf)
- **Dual utility (Dill).** «we assign two: a rank and a weight. Rank uses absolute utility to divide the options into categories, and we only select options from the best category» — это ровно «приоритет 1–4 строго, внутри — вес». [Dill](http://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter03_Dual-Utility_Reasoning.pdf)
- **Осцилляции.** Graham, §9.7 Inertia: «add a weight to any action that you are already currently engaged in», cooldown после решения, либо «stall making another decision … until the current action is finished» (Sims Medieval решает только при пустой очереди). Lewis: considerations *runtime* (гасит счёт после N повторов) и *cooldown* (счёт ≈ 0 до истечения таймера, «avoiding strobing between two otherwise competing decisions»). Реализация IAUS в UE (Project Borealis): «currently running behavior gets an inertia bonus … increases its score by a constant factor». [IAUS wiki](https://github.com/ProjectBorealis/IAUS/wiki)
- **Доклады.** GDC 2010 Mark & Dill «Improving AI Decision Modeling Through Utility Theory» (response curves, population distributions, weighted random; против «harsh transition from yes to no»); GDC 2015 Mark & Lewis «Building a Better Centaur» (IAUS + influence maps в GW2, пакеты поведения за минуты). Оба помечены «free content» в Vault. Сам IAUS: «data-driven», «rarely—if ever—gets stuck in a behavior», есть C# версия (коммерческая). [GDC 2010](https://gdcvault.com/play/1012410/Improving-AI-Decision-Modeling-Through), [GDC 2015](https://www.gdcvault.com/play/1021848/Building-a-Better-Centaur-AI), [gameai.com IAUS](https://gameai.com/iaus.php), [Wikipedia](https://en.wikipedia.org/wiki/Utility_system)
- **Детерминизм.** В первоисточниках utility-AI тема не поднимается; весь недетерминизм приходит от weighted random и float. Для нашего 10 Гц тика: только максимум (без случайного выбора) или seeded RNG симуляции, целочисленные/fixed-point счёты, стабильный tie-break по id.

### 4. BT / FSM / GOAP для «тел» задач

- **FSM/HFSM** (Dawe, GAIP ch.4): «conceptually simple and quick to code», но «ever more difficult and error-prone to add» после 10–30 состояний; HFSM лечит частично. [Dawe](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter04_Behavior_Selection_Algorithms.pdf)
- **Behaviour tree** (Simpson): Running/Success/Fail, последовательность = план («Walk to Door → Open Door → Walk Through → Close Door»; провал шага отбрасывает остаток), селектор = запасные варианты, Running «allows a node's processing to persist for many ticks». Явного механизма прерывания в статье нет — провал распространяется вверх. [Simpson](https://www.gamedeveloper.com/programming/behavior-trees-for-ai-how-they-work)
- **GOAP** (Оркин): три состояния Goto/Animate/UseSmartObject, A\* по действиям с ценой («We added a cost per action»), выгода — «decouple goals and actions» и перепланирование после провала (дверь держат → пнуть → в окно). Для колони-сима избыточно: наши планы фиксированы (подойти-взять-нести-положить), а стоимость отладки планировщика высокая. [Orkin](https://pages.cs.wisc.edu/~dyer/cs540/handouts/gdc2006_orkin_jeff_fear.pdf), [обзор](https://www.lancaster.ac.uk/stor-i-student-sites/tamas-papp/2020/04/23/fear-shortest-path-for-challenging-ai/)
- **Практика колони-симов.** RimWorld: линейный список toils + FailOn + Jump. DwarfCorp: `Act` — корутина `IEnumerable<Status>` (Running/Fail/Success) с `Children` и рекурсивным `OnCanceled()`, т. е. BT на yield. Project Porcupine: `Job` с `SuspendCantReach()` / `SuspendWaitingForInventory()` и реактивацией по событию изменения комнаты/инвентаря. [DwarfCorp Act](https://raw.githubusercontent.com/Blecki/dwarfcorp/master/DwarfCorp/TaskManagement/Act.cs), [Porcupine Job](https://raw.githubusercontent.com/OrderOfThePorcupine/ProjectPorcupine/master/Assets/Scripts/Models/Job/Job.cs)
- **Прерывание и возобновление.** Общий паттерн у всех: прогресс хранится на цели (чертёж знает % постройки), а не в задаче; прерванная задача снимает резервации и возвращается на доску; переносимый предмет кладётся на пол. ONI сознательно не прерывает текущее поручение более приоритетным — прерывают только потребности и тревоги.

### 5. Открытые реализации на C# (GitHub, проверено 14.10.2026)

| Репозиторий | Лицензия | Что смотреть | Оценка |
|---|---|---|---|
| [Blecki/dwarfcorp](https://github.com/Blecki/dwarfcorp) (631★, XNA/FNA) | MIT для кода, ассеты проприетарные | `DwarfCorp/TaskManagement/`: `Task` (`Priority` NotSet/Eventually/Low/Medium/High/Urgent, `MaxAssignable`, `IsFeasible`, `ComputeCost`, `CreateScript → Act`), `TaskManager.GetBestTask` (фильтр категории/лимита/feasibility, затем `cost * (1 + AssignedCreatures.Count)`), `AssignTasks` через матрицу стоимости и `FindAssignments()` (венгерский алгоритм) | Лучший C#-референс по доске задач и батч-назначению; `AssignedCreatures` + `MaxAssignable` вместо отдельной резервации |
| [OrderOfThePorcupine/ProjectPorcupine](https://github.com/OrderOfThePorcupine/ProjectPorcupine) (325★, Unity; старый [TeamPorcupine](https://github.com/TeamPorcupine/ProjectPorcupine) 493★) | GPL-3.0 | `Models/Job/JobManager.cs`: очереди `HashSet<Job>` по категории, `GetJob` — обход уровней приоритета персонажа → лучший `Job.Priority` → минимальный `Pathfinder.FindMinPathTime`; флаг `IsBeingWorked`; `Job.SuspendCantReach()` | Читать за идею «ярус → ближайшее по времени пути» и материалы (`RequestedItem`); GPL — копировать нельзя, `HashSet` ломает детерминизм |
| [crashkonijn/GOAP](https://github.com/crashkonijn/GOAP) (≈1,8k★) | Apache-2.0 | Goals/Actions/Sensors, планировщик на Unity Jobs | Зависит от Unity; как иллюстрация GOAP, не для Core |
| [ProjectBorealis/IAUS](https://github.com/ProjectBorealis/IAUS/wiki) | не указана | C++/UE, но вики описывает считалки, кривые, inertia bonus | Только как описание IAUS |
| [rwmt/Multiplayer](https://github.com/rwmt/Multiplayer) (674★) | MIT | Deterministic lockstep поверх RimWorld; сервер «ticks at a rate of 16.66 ms per tick» | Доказательство, что job-систему RimWorld можно сделать детерминированной; детали desync в [dev-wiki](https://hackmd.io/@rimworldmultiplayer/dev-general) скудные |

Репозитория «SharpAI» для колони-симов не нашёл; pure-.NET BT-библиотеки (Paradise.BT, FlatTree, MistreevousSharp) видел только в поисковой выдаче NuGet, не открывал — при 4–6 типах шагов своя реализация проще зависимости.

### 6. Типичные баги и тесты

| Баг | Причина | Защита | Тест-сценарий |
|---|---|---|---|
| Двое идут за одной вещью | выбор без резервации или резервация после похода | `Reserve` в момент выбора (как `TryMakePreToilReservations`), атомарно для всех целей задачи | 2 жителя, 1 бревно: ровно один `Reserved`, второй берёт другую задачу/idle |
| Зависание у недостижимого | A\* до цели без проверки региона; «reachability check does not inspect cells only rooms» | `RegionId` на клетку, инкрементальный пересчёт при стене/двери; `FailOnCannotReach` каждый N тик | бревно за стеной: задача помечена Blocked, A\* не вызывается |
| Бесконечный перезапуск | задача создаётся, проваливается на тике 0, снова создаётся (RimWorld «10 jobs in one tick») | счётчик стартов за тик + `FailedTargets[target] = now + cooldown` на жителе + `Job.FailCount` → Blocked | мокнутый провал на 1-м шаге: ≤ 1 старт/тик, задача через 3 провала в Blocked |
| Перехват предмета | предмет забрали/запретили/съели пока шли | `FailOnDespawnedNullOrForbidden`, повторная проверка резервации при `StartCarry` | удалить предмет на пути: шаг Fail, резервация снята, доска обновлена |
| Хаотичные пробежки через карту (DF, Haul=1 в RimWorld) | ярус без учёта расстояния | в ярусе — вес по расстоянию (bookends), «batch» для уборки/сбора | 10 задач в двух кластерах: житель заканчивает ближний кластер первым |
| Осцилляция еда/работа на пороге | один порог | порог старта 0.30 vs статус 0.25; после еды не оценивать голод до > порога; бонус текущему действию | еда колеблется у 0.30: число переключений ≤ 1 |
| Утечка резерваций | смерть/прерывание без Release | `ReleaseAll(worker)` в одном месте (конец задачи любого исхода) | тревога во время переноски: все резервации жителя = 0 |

Источники по строке «реальный мир»: [Ludeon Reachability](https://ludeon.com/blog/2013/07/reachability-at-last), [RimWorld Work](https://rimworldwiki.com/wiki/Work), [DF Job](https://dwarffortresswiki.org/index.php/DF2014:Job), [Mehni JobDriver](https://github.com/Mehni/ExampleJob/wiki/4)-JobDriver), [Prison Architect Workman](https://prisonarchitect.paradoxwikis.com/Workman).

### 7. Потребности и расписание (RimWorld как эталон чисел)

- **Еда.** Взрослый теряет 1,6 единицы/день при ёмкости 1,0; статусы: Fed > 25 %, Hungry 12,5–25 % (настроение –6), Ravenously Hungry 0–12,5 % (–12), Malnourished 0 % (–20). «Pawns will attempt to eat as their next action once their saturation level reaches 30 % to avoid being hungry». Перебор не отслеживается (simple meal 0,9 при 30 % — часть пропадает). [Saturation](https://rimworldwiki.com/wiki/Saturation), [Food](https://rimworldwiki.com/wiki/Food)
- **Сон.** Падение каждые 150 тиков (1/400 дня), зависит от уровня: Rested 0,237 %, Drowsy 0,166 %, Tired 0,071 %, Exhausted 0,142 % (≈95 %/день бодрствования на уровне Rested). Пороги: Rested ≥ 28 %, Drowsy 14–28 % (–6), Tired 1–14 % (–12), Exhausted ≤ 1 % (–18); при 0 % «immediately begin sleeping on the ground». Восстановление 0,571 % × эффективность кровати × множитель здоровья за 150 тиков; 0 → 100 % = 10,5 ч. [Rest](https://rimworldwiki.com/wiki/Rest)
- **Расписание (24 слота/час).** Anything: работа, пока отдых < 30 %, еда < 30 %, развлечение < 35 %; Work: только еда < 30 %, спящего будит; Sleep: ложится при отдыхе < 75 % или еде < 30 %, просыпается при 100 % отдыха, при еде < 12,5 % (если еда в доступе) или при смене блока на Work; Recreation: развлечение < 95 %. [Schedule](https://rimworldwiki.com/wiki/Schedule)
- **ONI для сравнения.** Калории: голод при 3000 ккал, едят только в Downtime (< 3300), голод прерывает сон; дыхание прерывает активность при 72,7 %; туалет — 100 % во время работы, > 40 % в отдых. То есть ONI переносит потребности в блоки расписания, а не прерывает работу при любом пороге. [Duplicant](https://oxygennotincluded.wiki.gg/wiki/Duplicant)

## Сравнение архитектур задач

| Архитектура | Где применяется | Плюсы | Минусы | Для нас |
|---|---|---|---|---|
| Строгие ярусы + порядок типов + ближайшее (RimWorld) | RimWorld, Going Medieval | предсказуемо, объяснимо игроку, детерминировано | «no regard for efficiency», пробежки через карту, нет учёта навыка | основа выбора яруса |
| Ярус + суб-приоритет + близость как tie-break, без вытеснения (ONI) | ONI | мало переключений, игрок управляет и жителем, и зданием | скрытый порядок непрозрачен | взять суб-приоритет чертежа и правило «не вытеснять работой» |
| Идлер берёт первую подходящую (DF) | Dwarf Fortress | нулевая логика | хаос, рандомные инструменты, «not fully understood» | нет |
| Слоты комнат = профессия (Prison Architect) | PA | профессия привязана к месту, обучение, расписание | жёсткость | взять для Workplace/Profession |
| Cost-матрица, венгерское назначение (DwarfCorp) | DwarfCorp | глобальный оптимум, батч | O(n³) при 100 жителей × сотни задач на каждом пересчёте; сложно объяснить игроку | нет, разве что для носильщиков батчем |
| Dual utility: rank (ярус) + weight (расстояние, навык, срочность, возраст) | Dill, IAUS | гибко, лечит пробежки, учитывает навык | нужны кривые и тюнинг, риск осцилляций | внутри яруса |
| GOAP | F.E.A.R. | перепланирование | планировщик, отладка, недетерминизм по умолчанию | нет |
| Toils / BT-корутина для тела | RimWorld, DwarfCorp | линейно, FailOn, прерывается | состояние в шаге надо сериализовать | да |

## Рекомендация для проекта

**Сущности (Core, без движка):**

```csharp
public enum WorkType { Firefight, Doctor, Haul, Build, Cook, Farm, Chop, Mine, Craft, Clean, Research } // порядок = tie-break слева направо
public enum JobState { Open, Claimed, Running, Blocked, Done, Cancelled }

public sealed class Job {
    public int Id;                       // стабильный, растущий — tie-break
    public WorkType Type;
    public int SitePriority;             // 1..9 от чертежа/здания (как ONI sub-priority), по умолчанию 5
    public int MinSkill;                 // 0 = любой
    public EntityId? Workplace;          // null = свободная работа (haul/build/chop)
    public TargetRef[] Targets;          // вещь, клетка, слот — всё, что будет зарезервировано
    public IJobStep[] Steps;             // Goto, Pickup, Carry, Place, WorkAt(duration), Deposit
    public JobState State; public int FailCount; public int BlockedUntilTick; public int CreatedTick;
}

public readonly record struct Reservation(EntityId Target, ReservationLayer Layer, int JobId, EntityId Worker, int Count);
public enum ReservationLayer { Thing, Cell, WorkSlot }

public sealed class ReservationManager {
    public bool CanReserve(EntityId worker, TargetRef t, int count = -1);
    public bool TryReserveAll(EntityId worker, Job job);   // атомарно: либо все цели, либо ни одной
    public void ReleaseAll(EntityId worker);               // единственная точка снятия
}

public sealed class JobBoard {
    public void Post(Job job); public void Cancel(int jobId);
    public IEnumerable<Job> Open(WorkType type, int nowTick); // отсортировано по Id; Blocked с BlockedUntilTick > now не отдаётся
}

public sealed class Worker {
    public byte[] Priority;               // по WorkType: 0 = нет, 1..4
    public int[] Skill; public Needs Needs; public Schedule Schedule; // 24 блока: Work/Sleep/Anything/Meal
    public EntityId? WorkplaceSlot;        // занял слот → Profession = слот.Type (Minecraft-стиль)
    public Job? Current; public Queue<Job> Forced;
    public Dictionary<EntityId,int> FailedTargets;   // target → untilTick (cooldown 600 тиков)
    public int NextThinkTick;              // стаггер: (Id % 10) + 10 Гц → каждый житель думает 1 раз/с
}
```

**Алгоритм выбора (каждый `NextThinkTick`, детерминированный порядок по `Worker.Id`):**

1. Тревога (колокол/огонь/враг) — немедленно: `ReleaseAll`, предмет в руках на пол, `Current = Alarm job`. Единственное вытеснение работы работой.
2. `Forced` (приказы игрока) — как RimWorld, прерывают текущую.
3. Критические потребности: `Food < 0.125` или `Rest == 0` → прерывают любую работу.
4. Блок расписания: `Sleep` → спать при `Rest < 0.75`; `Meal` → есть при `Food < 0.5`; `Anything` → спать при `Rest < 0.30`; во всех блоках есть при `Food < 0.30`. Гистерезис: потребность, запущенная порогом, не переоценивается, пока не достигнет `wakeAt` (отдых 1.0, еда — до конца приёма).
5. Если `Current != null` и шаг `Running` — продолжать (как ONI: работа не вытесняет работу).
6. Работа: для `p` в 1..4, для `type` в порядке enum с `Priority[type] == p`: кандидаты `board.Open(type)` с фильтром `Region(worker) == Region(target)`, `!FailedTargets`, `Skill >= MinSkill`, `CanReserve`. Если `Workplace != null` — только житель с этим `WorkplaceSlot`. Счёт внутри яруса (целые, 0..1000):
   `score = w_dist * curve(1 - dist/dmax) * w_site * SitePriority/9 * w_skill * (1 + skill/20) * w_age * min(1, age/3000)`, перемножение как в IAUS; `dmax` = bookend (например 60 клеток). Текущая задача при переоценке получает `×1.15` (inertia). Первый ярус с кандидатом побеждает; tie → меньший `Job.Id`.
7. `TryReserveAll`; при неудаче — следующий кандидат (не ломая ярус).
8. Пусто → Idle: короткий wander рядом с `Workplace`/домом, следующая переоценка через 30 тиков (экспоненциально до 100).

**Тело задачи:** `IJobStep.Tick(ctx) → Running|Success|Fail`; `Job.FailOn` проверки раз в 10 тиков: цель исчезла/запрещена/регион изменился/резервация потеряна. `Fail` → `FailCount++`, `FailedTargets[target] = now + 600`; `FailCount >= 3` → `Blocked` на 3000 тиков + иконка «недостижимо» для игрока; `ReleaseAll` всегда. Прогресс (`% постройки`, `нарублено`) живёт на цели, а не в `Job`, поэтому возобновление = новая задача с тем же `Targets`.

**Детерминизм:** никакого `Dictionary`/`HashSet` в порядке обхода (списки, отсортированные по Id), целочисленный счёт, seeded RNG только для Idle wander, `NextThinkTick` вместо «каждый тик», один проход по жителям в фиксированном порядке. Тест-страж: 2 прогона одного seed → одинаковый хеш `(JobId, WorkerId, State)` на тике 10 000.

**Тест-сценарии (xUnit над Core):** (1) два жителя, одно бревно → одна резервация; (2) бревно за стеной → `Blocked`, A\* не вызван (счётчик вызовов = 0); (3) провал на 1-м шаге → не более 1 старта на тик, после 3 провалов Blocked; (4) удаление предмета в пути → Fail, резервации 0, задача снова Open; (5) `Haul = 1` у носильщика и 10 задач в двух кластерах → ближний кластер первым; (6) еда 0.31→0.29→0.31 → ровно одно переключение; (7) тревога во время переноски → предмет на полу, резервации 0, после отбоя задача снова Open; (8) занял слот кухни → `Profession = Cook`, задачи кухни доступны только ему; (9) 100 жителей × 500 задач: выбор ≤ 1 мс/житель на профилировщике (батч 10 жителей/тик при 10 Гц).

## Источники

- RimWorld wiki: [Work](https://rimworldwiki.com/wiki/Work), [Schedule](https://rimworldwiki.com/wiki/Schedule), [Rest](https://rimworldwiki.com/wiki/Rest), [Saturation](https://rimworldwiki.com/wiki/Saturation), [Food](https://rimworldwiki.com/wiki/Food), [Need](https://rimworldwiki.com/wiki/Need), [Modding: Mending job](https://rimworldwiki.com/wiki/Modding_Tutorials/Code_MendingJob), [Modding: Decompiling](https://rimworldwiki.com/wiki/Modding_Tutorials/Decompiling_source_code)
- Mehni, ExampleJob wiki: [обзор](https://github.com/Mehni/ExampleJob/wiki), [1a JobGiver](https://github.com/Mehni/ExampleJob/wiki/1a)-Job-Assignment-via-JobGiver), [1b WorkGiver](https://github.com/Mehni/ExampleJob/wiki/1b)-Job-Assignment-via-WorkGiver), [4 JobDriver](https://github.com/Mehni/ExampleJob/wiki/4)-JobDriver), [5 Toils](https://github.com/Mehni/ExampleJob/wiki/5)-Toils)
- Tynan Sylvester: [Reachability at last (2013)](https://ludeon.com/blog/2013/07/reachability-at-last), [GDC 2017](https://gdcvault.com/play/1024232/-RimWorld-Contrarian-Ridiculous-and)
- ONI wiki: [Priority](https://oxygennotincluded.wiki.gg/wiki/Priority), [Errand](https://oxygennotincluded.wiki.gg/wiki/Errand), [Duplicant](https://oxygennotincluded.wiki.gg/wiki/Duplicant), [OC-253538](https://oxygennotincluded.wiki.gg/wiki/Versions/OC-253538)
- [Dwarf Fortress wiki: Job](https://dwarffortresswiki.org/index.php/DF2014:Job); Prison Architect wiki: [Prison Labour](https://prisonarchitect.paradoxwikis.com/Prison_Labour), [Workman](https://prisonarchitect.paradoxwikis.com/Workman); [Going Medieval: Jobs](https://eip.gg/going-medieval/guides/jobs/)
- Utility: [Graham, GAIP ch.9](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter09_An_Introduction_to_Utility_Theory.pdf), [Dill, GAIP2 ch.3](http://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter03_Dual-Utility_Reasoning.pdf), [Lewis, GAIP3 ch.13](http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter13_Choosing_Effective_Utility-Based_Considerations.pdf), [Dawe, GAIP ch.4](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter04_Behavior_Selection_Algorithms.pdf), [оглавление Game AI Pro](https://www.gameaipro.com/), [GDC 2010 Mark & Dill](https://gdcvault.com/play/1012410/Improving-AI-Decision-Modeling-Through), [GDC 2015 Mark & Lewis](https://www.gdcvault.com/play/1021848/Building-a-Better-Centaur-AI), [IAUS (gameai.com)](https://gameai.com/iaus.php), [ProjectBorealis/IAUS wiki](https://github.com/ProjectBorealis/IAUS/wiki), [Wikipedia: Utility system](https://en.wikipedia.org/wiki/Utility_system)
- BT/GOAP: [Simpson, Behavior trees for AI](https://www.gamedeveloper.com/programming/behavior-trees-for-ai-how-they-work), [Orkin, Three States and a Plan (GDC 2006, PDF)](https://pages.cs.wisc.edu/~dyer/cs540/handouts/gdc2006_orkin_jeff_fear.pdf), [обзор F.E.A.R. GOAP](https://www.lancaster.ac.uk/stor-i-student-sites/tamas-papp/2020/04/23/fear-shortest-path-for-challenging-ai/)
- Код: [Blecki/dwarfcorp](https://github.com/Blecki/dwarfcorp) ([TaskManager.cs](https://raw.githubusercontent.com/Blecki/dwarfcorp/master/DwarfCorp/TaskManagement/TaskManager.cs), [Task.cs](https://raw.githubusercontent.com/Blecki/dwarfcorp/master/DwarfCorp/TaskManagement/Task.cs), [Act.cs](https://raw.githubusercontent.com/Blecki/dwarfcorp/master/DwarfCorp/TaskManagement/Act.cs), [Enums.cs](https://raw.githubusercontent.com/Blecki/dwarfcorp/master/DwarfCorp/TaskManagement/Enums.cs)), [OrderOfThePorcupine/ProjectPorcupine](https://github.com/OrderOfThePorcupine/ProjectPorcupine) ([JobManager.cs](https://raw.githubusercontent.com/OrderOfThePorcupine/ProjectPorcupine/master/Assets/Scripts/Models/Job/JobManager.cs), [Job.cs](https://raw.githubusercontent.com/OrderOfThePorcupine/ProjectPorcupine/master/Assets/Scripts/Models/Job/Job.cs)), [crashkonijn/GOAP](https://github.com/crashkonijn/GOAP), [rwmt/Multiplayer](https://github.com/rwmt/Multiplayer), [RimWorld MP dev wiki](https://hackmd.io/@rimworldmultiplayer/dev-general)
