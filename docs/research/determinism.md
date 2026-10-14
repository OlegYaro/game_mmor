# Детерминированная симуляция: тик, RNG, float, хэш, реплей

Дата исследования: 14.10.2026. Контекст: `Core` на чистом C#, тик 10 Гц, единый seed, тест-реплей в CI (NFR-04), одинаковый результат на macOS arm64 и Windows x64, сохранения = seed + дифф тайлов + состояние симуляции (GDD §сохранения).

## Краткие выводы

1. **Тик отделяем от кадра аккумулятором** («Free the physics»), рендер интерполирует между двумя последними снимками с `alpha = accumulator / dt`; число шагов за кадр ограничиваем, чтобы не попасть в «spiral of death» ([Fiedler, Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/)).
2. **`System.Random` для симуляции не годится.** С .NET 6 `new Random()` без seed использует xoshiro256** (на 64-бит), `new Random(seed)` и наследники остаются на старом алгоритме Кнута ради совместимости ([Toub, .NET 6 perf](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-6/), [PR #47085](https://github.com/dotnet/runtime/pull/47085)). Документация прямо говорит, что алгоритм «isn't guaranteed to remain the same across major versions» ([learn: Random](https://learn.microsoft.com/en-us/dotnet/api/system.random)). `Random.Shared` нельзя засеять ([learn: Random.Shared](https://learn.microsoft.com/en-us/dotnet/api/system.random.shared)). Поэтому RNG пишем сами: xoshiro256** с засевом через SplitMix64 ([Vigna](https://prng.di.unimi.it/)).
3. **Базовая арифметика float/double в .NET 64-бит совпадает на x64 и arm64.** RyuJIT использует SSE2 (без x87) ([roslyn#7333](https://github.com/dotnet/roslyn/issues/7333)), сам не сливает `a*b+c` в FMA ([runtime#10137](https://github.com/dotnet/runtime/issues/10137)) и не автовекторизует циклы ([runtime#11263](https://github.com/dotnet/runtime/issues/11263)). **Но** `Math.Sin/Cos/Pow/Exp` вызывают C-рантайм ОС, и результаты «may differ between different operating systems or architectures» ([learn: Math.Sin](https://learn.microsoft.com/en-us/dotnet/api/system.math.sin), [runtime#43428](https://github.com/dotnet/runtime/issues/43428)).
4. **В критичной логике используем целые и fixed-point**, float оставляем рендеру и UI. Так делает Photon Quantum: `FP` «completely replaces all usages of float and double» ([Quantum: Fixed Point](https://doc.photonengine.com/quantum/v3/manual/quantum-ecs/fixed-point)).
5. **Хэш состояния считаем по каноническому байтовому буферу** (little-endian через `BinaryPrimitives`) алгоритмом `XxHash3`/`XxHash64` из `System.IO.Hashing` 10.0.13 ([NuGet](https://www.nuget.org/packages/System.IO.Hashing), [learn](https://learn.microsoft.com/en-us/dotnet/api/system.io.hashing)). `string.GetHashCode` и `HashCode` рандомизированы на процесс, сохранять их нельзя ([learn: String.GetHashCode](https://learn.microsoft.com/en-us/dotnet/api/system.string.gethashcode), [learn: HashCode](https://learn.microsoft.com/en-us/dotnet/api/system.hashcode)).
6. **Реплей устроен как в Factorio и AoE:** seed + команды с номером тика, хэш на каждом тике, ищем первый тик расхождения, на нём дампим и сравниваем полное состояние ([FFF-47](https://factorio.com/blog/post/fff-47), [1500 Archers](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond), [Riot: Fixing Divergences](https://technology.riotgames.com/node/84)).
7. **Главные ловушки:** порядок `Dictionary`/`HashSet` не определён ([learn: Dictionary](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2)), `Array.Sort`/`List.Sort` нестабильны ([learn: Array.Sort](https://learn.microsoft.com/en-us/dotnet/api/system.array.sort)), `Parallel.For` и PLINQ не сохраняют порядок ([learn: pitfalls](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/potential-pitfalls-in-data-and-task-parallelism)), время статической инициализации не контролируется ([learn: static constructors](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/static-constructors)).

## Подробности

### 1. Фиксированный тик и рендер

Fiedler: рендер «производит» реальное время, симуляция «потребляет» его шагами `dt`; остаток копится в аккумуляторе и не теряется. Для плавности рендер смешивает предыдущее и текущее состояние: `state = current*alpha + previous*(1-alpha)`. Если кадр долгий, симуляция берёт ещё больше шагов, чтобы догнать, и отстаёт всё сильнее («spiral of death»). Защиты две: ограничить `frameTime` (у Fiedler 0.25 с) или число шагов за кадр ([источник](https://gafferongames.com/post/fix_your_timestep/)).

Для нас: `dt = 100 мс` по счётчику тиков `long Tick`. Время симуляции существует только как номер тика, без секунд в `double`. Godot держит два последних снимка `Core` и интерполирует **только визуальные** позиции. При скорости ×4 кадр выполняет не больше 4 тиков (NFR-02). Если тики не успевают, игра замедляется, а не «догоняет» без конца. Скорость игры меняет число тиков за кадр, сам `dt` всегда один.

```csharp
// Godot side (_Process): drives Core, never mutates it directly
_acc += delta * speedMultiplier;
int steps = 0;
while (_acc >= TickSeconds && steps < MaxStepsPerFrame) {
    _sim.Step(_pendingCommands.DrainFor(_sim.Tick)); // commands stamped with tick
    _acc -= TickSeconds; steps++;
}
if (steps == MaxStepsPerFrame) _acc = 0;          // drop backlog, slow down instead
float alpha = (float)(_acc / TickSeconds);        // render-only interpolation
```

### 2. Детерминированный RNG

- **Почему не `System.Random`.** В исходнике `Random.cs` прямо выбирается реализация: без seed берётся `XoshiroImpl`, с seed `CompatSeedImpl`, для наследника `CompatDerivedImpl` ([source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Random.cs)). Сегодня `Random(seed)` по факту стабилен, но документация этого не обещает. Кроме того, методы вроде `Next(int, int)` и `NextDouble` поверх ядра тоже могут поменяться. Отдельной записи о breaking change для .NET 6 нет, официальные источники здесь только блог и PR.
- **Алгоритм.** xoshiro256** (Blackman & Vigna) хорошо подходит для общих задач. Засевать его рекомендуется через SplitMix64: «If you have a 64-bit seed, we suggest to seed a splitmix64 generator and use its output to fill s» ([prng.di.unimi.it](https://prng.di.unimi.it/), [xoshiro256starstar.c](https://prng.di.unimi.it/xoshiro256starstar.c), [splitmix64.c](https://prng.di.unimi.it/splitmix64.c)). Альтернатива: PCG32 с явными потоками ([pcg-random.org](https://www.pcg-random.org/), [pcg-c-basic](https://github.com/imneme/pcg-c-basic)).
- **Потоки по подсистемам.** У каждой подсистемы свой генератор (worldgen, бой, лут, AI, рассказчик). Тогда новая подсистема или лишний бросок в бою не сдвигает генерацию лута и мира. Сам Vigna для независимых подпоследовательностей предлагает `jump()`/`long_jump()` (2^128 непересекающихся отрезков). Выводить seed потока через SplitMix64 от `(worldSeed, streamId)` широко принято, но на его странице это прямо не рекомендовано (не подтверждено как рекомендация автора). Для генерации чанков удобен seed от `(worldSeed, chunkX, chunkY)`: чанк можно сгенерировать в любом порядке.

```csharp
public struct Xoshiro256 // value type, zero allocations; state is serialized into saves
{
    private ulong _s0, _s1, _s2, _s3;
    public Xoshiro256(ulong seed) {
        _s0 = SplitMix(ref seed); _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed); _s3 = SplitMix(ref seed);
    }
    public static Xoshiro256 ForStream(ulong worldSeed, ulong streamId) {
        ulong x = worldSeed ^ (streamId * 0x9E3779B97F4A7C15UL);
        return new Xoshiro256(SplitMix(ref x));
    }
    private static ulong SplitMix(ref ulong x) {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public ulong NextUInt64() {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }
    // Unbiased [0, bound) via Lemire's multiply-shift with rejection; integer-only
    public uint NextUInt(uint bound) {
        ulong m = (ulong)(uint)(NextUInt64() >> 32) * bound;
        if ((uint)m < bound) {
            uint threshold = (0u - bound) % bound;
            while ((uint)m < threshold) m = (ulong)(uint)(NextUInt64() >> 32) * bound;
        }
        return (uint)(m >> 32);
    }
}
```

Состояние генератора (4×`ulong`) **входит в сохранение и в хэш состояния**. В тестах сверяем первые N выходов с эталонным C-кодом Vigna (golden-вектор). Готовый C#-порт с SplitMix64 тоже есть ([atomknack/DeterRandom](https://github.com/atomknack/DeterRandom), MIT), но код занимает 40 строк, и надёжнее держать его у себя.

### 3. Float на arm64 и x64

- **Что гарантировано.** IEEE 754 даёт корректно округлённые `+ − × ÷ sqrt`. По словам tannergooding, «IEEE 754 floating-point arithmetic is, by spec, deterministic» ([discussion #110666](https://github.com/dotnet/runtime/discussions/110666)). RyuJIT на 64-бит округляет «directly to the target precision» ([roslyn#7333](https://github.com/dotnet/roslyn/issues/7333)). Автоконтракции в FMA нет: в issue #10137 её отклонили именно потому, что результат меняется, а `(1e300*2)-1e300` даёт Inf без слияния и 1e300 со слиянием ([#10137](https://github.com/dotnet/runtime/issues/10137)). Автовекторизации нет, issue закрыт как «unlikely … in the near or even mid-term future» ([#11263](https://github.com/dotnet/runtime/issues/11263)). `Math.Sqrt` идёт как JIT-интринсик в аппаратную инструкцию, а IEEE требует для неё корректного округления. Отдельной фразы в документации о точности `Sqrt` нет (не подтверждено документацией, только исходник и стандарт).
- **Что не гарантировано.** `Math.Sin/Cos/Tan/Pow/Exp/Log/Atan2` объявлены `InternalCall` и вызывают libm платформы ([Math.CoreCLR.cs](https://github.com/dotnet/runtime/blob/main/src/coreclr/System.Private.CoreLib/src/System/Math.CoreCLR.cs)). Расхождения возможны для любой пары ОС и архитектуры ([#43428](https://github.com/dotnet/runtime/issues/43428)), а `Pow`/`Exp` различаются в последнем бите даже между двумя x64-машинами ([#42747](https://github.com/dotnet/runtime/issues/42747)). Явный `Math.FusedMultiplyAdd` без аппаратного FMA3 может округлять неверно ([#98704](https://github.com/dotnet/runtime/issues/98704)). В .NET 9 появились управляемые `Vector128.Sin/Cos/Exp`, портированные из AMD AOCL-LibM ([VectorMath.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/VectorMath.cs)), но скалярные `Math.*` по-прежнему идут через CRT. Побитовую одинаковость векторных версий на всех платформах официально никто не обещает (не подтверждено).
- **Конверсия float→int.** В .NET 9 её сделали насыщающей и одинаковой на всех платформах, раньше результат для NaN и выхода за диапазон зависел от платформы ([learn: breaking change 9.0](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/9.0/fp-to-integer)). Это довод собирать `Core` под net9+ (лучше под net10 LTS) и всё равно не допускать таких конверсий в логике.
- **Опыт индустрии.** Fiedler: float детерминирован «if… same compiler, same architecture, same build», а на разных платформах ждать совпадения «incredibly naive» без строгой дисциплины. Отдельный риск у него составляют трансцендентные функции ([Floating Point Determinism](https://gafferongames.com/post/floating_point_determinism/)). Quantum вообще запрещает float в симуляции: `FP.FromFloat_UNSAFE` «will cause desyncs 100% of the time» ([Quantum FP](https://doc.photonengine.com/quantum/v3/manual/quantum-ecs/fixed-point)). Unity Burst ради кроссплатформенности держит отдельный режим `FloatMode.Deterministic` ([Unity docs](https://docs.unity3d.com/Manual/burst/float-precision-determinism.html)).
- **Готовые fixed-point библиотеки:** [asik/FixedMath.Net](https://github.com/asik/FixedMath.Net) (Q31.32 `Fix64`), [XMunkki/FixPointCS](https://github.com/XMunkki/FixPointCS) (Q32.32/Q16.16, «bit-identical results across languages and compilers», MIT).

### 4. Хэш состояния

- `string.GetHashCode`: «two subsequent runs of the same program may return different hash codes… should never be persisted» ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.string.gethashcode)). `HashCode` использует «statically initialized random seed» ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.hashcode)). Значит, `GetHashCode()` не годится ни для хэша состояния, ни для ключей сортировки, ни для seed.
- **Схема.** `StateWriter` пишет поля в фиксированном порядке в переиспользуемый `byte[]` через `BinaryPrimitives.WriteInt64LittleEndian` и аналоги ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.binary.binaryprimitives)), а результат хэшируется `XxHash3.HashToUInt64`. Порядок обхода такой: сущности по возрастанию id, компоненты в объявленном порядке, коллекции по возрастанию ключа, строки как UTF-8 с длиной. Этот же писатель делает основу бинарного сохранения, поэтому хэш и сейв не расходятся по содержанию.
- **Иерархия хэшей.** Кроме общего хэша считаем хэши по подсистемам (тайлы, жители, существа, фракции, RNG). Riot отказались от единой чек-суммы памяти, потому что с ней «difficult to drill down» до строки кода, и перешли на иерархический лог «ключ–значение» ([Riot](https://technology.riotgames.com/node/84)). Хэш по подсистемам — дешёвый компромисс, а полный дамп снимаем только на первом расходящемся тике.

### 5. Тест-реплей

- **AoE.** Все машины выполняют одну и ту же симуляцию с одинаковыми командами. Команды хода 1000 исполняются на ходу 1002, ход длится около 200 мс. Расхождение помечается как «out of sync» и останавливает игру. Чек-суммы охватывали мир, объекты и поиск пути, генераторы случайных чисел засевались синхронно ([1500 Archers](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond)).
- **Factorio.** В отладке считается CRC всей карты на каждом тике, при реплее несовпадение CRC выбрасывает исключение, а сохранение на каждом тике позволяет сравнить две карты ([FFF-47](https://factorio.com/blog/post/fff-47)). В мультиплеере используется быстрая неполная CRC ([FFF-55](https://factorio.com/blog/post/fff-55)). Heavy mode сохраняет и загружает игру на каждом тике и так ловит несохраняемое состояние ([wiki: Desynchronization](https://wiki.factorio.com/Desynchronization)). Реальные десинки дали порядок обхода таблиц и кэш, пересчитанный после загрузки ([FFF-340](https://factorio.com/blog/post/fff-340)).
- **Riot (LoL).** Время было «the single largest barrier»: нашли 8 экземпляров часов в 6 реализациях и свели их к одним. Множество PRNG заменили одним XOR-shift с seed от ID игры. Порядок ordered map с ключами-указателями непредсказуем, поэтому указатели заменили детерминированными хэндлами. Значение имеет только первое расхождение. Каждый день прогоняют 2–3 тысячи реальных игр ([Intro](https://technology.riotgames.com/node/67), [Implementation](https://technology.riotgames.com/node/68), [Unified Clock](https://technology.riotgames.com/node/73), [Fixing Divergences](https://technology.riotgames.com/node/84)).
- **Photon Quantum.** Реплей перезапускает те же библиотеки, ассеты и ввод. Чек-суммы можно записать и сверить при воспроизведении ([Quantum Replay](https://doc.photonengine.com/quantum/v3/manual/replay)). В релизе их выключают из-за стоимости ([cheat protection](https://doc.photonengine.com/quantum/v2/manual/cheat-protection)).
- **Supercell (Clash Royale).** Первоисточника о lockstep не нашёл, есть только догадка стороннего блога ([gemserk](https://blog.gemserk.com/2016/09/05/analyzing-clash-royale-multiplayer-solution/)). Не подтверждено.

### 6. Ловушки и tie-break

| Ловушка | Почему | Что делаем |
|---|---|---|
| Обход `Dictionary`/`HashSet` | «The order… is undefined» ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2)). На практике порядок вставки держится до первого `Remove`+`Add`, потом слоты переиспользуются ([source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs), [msbuild#8974](https://github.com/dotnet/msbuild/pull/8974)) | Dictionary только для поиска. Обход по отсортированному массиву или `SortedDictionary`, плюс анализатор, запрещающий `foreach` по словарю в `Core` |
| `Array.Sort`/`List.Sort` | Интросорт, «performs an unstable sort» ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.array.sort), [List.Sort](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.sort)) | Компаратор с полным порядком: `(key, entityId)`. Тогда стабильность не нужна. `OrderBy` стабилен ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable.orderby)), но аллоцирует |
| `Parallel.For`, PLINQ | Не зависеть от порядка итераций ([learn](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/potential-pitfalls-in-data-and-task-parallelism)), PLINQ не сохраняет порядок ([learn](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/order-preservation-in-plinq)) | Тик однопоточный. Если понадобится параллельность: чистые вычисления пишут в слот по индексу, слияние последовательное, RNG в параллельной части не используется |
| События и коллбэки | Порядок подписчиков зависит от порядка подписки и загрузки | Очередь событий тика, разбираемая в фиксированном порядке систем. Godot получает события только на чтение |
| `DateTime.Now`, `Stopwatch`, `Environment.TickCount64` | Настенные и монотонные часы ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.datetime.now)) | Запрещены в `Core` (BannedApiAnalyzers). Время равно `Tick` |
| Статическая инициализация | «no control on when the static constructor is executed», `beforefieldinit` даёт «implementation-dependent time» ([learn](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/static-constructors), [spec](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/classes#1512-static-constructors)) | Никакого изменяемого статического состояния: всё живёт в `World`. Static только для `readonly`-констант |
| Порядок файлов контента | `Directory.GetFiles` не гарантирует порядок ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.getfiles)) | Сортировать пути через `StringComparer.Ordinal`. Хэш контента записывать в реплей и сейв |
| Строки и культура | Сравнение по культуре идёт через ICU, версии которого на разных ОС различаются ([learn: ICU](https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu)) | Только `Ordinal`. Числа парсить с `InvariantCulture` (у ru-RU десятичный разделитель — запятая) |
| Ссылки и хэш-коды объектов | `RuntimeHelpers.GetHashCode` и адреса разные в каждом запуске (Riot про указатели, [node/84](https://technology.riotgames.com/node/84)) | Ссылки между сущностями только через generational id |

## Рекомендация для проекта

1. **Время.** `Core` знает только `long Tick` (10 Гц). Godot крутит аккумулятор, делает не больше 4 тиков за кадр и интерполирует визуал по двум снимкам. Команды игрока получают штамп `ExecuteTick = currentTick + 1`.
2. **RNG.** Свой `struct Xoshiro256` (код выше) с потоками `Worldgen`, `Combat`, `Loot`, `Ai`, `Storyteller`, `Faction(i)` через `ForStream(seed, id)`, генерация чанков по `(seed, cx, cy)`. Состояние потоков идёт в сейв и в хэш. Golden-тест против эталонного C-кода.
3. **Числа.** Логика (позиции, HP, урон, потребности, экономика) на `int`/`long` и fixed-point: позиция в 1/256 тайла, проценты и промилле. Формулу урона из GDD (`1 + 0.05×(A−D)`, клэмп 0.3–3.0) считаем в целых процентах с явным правилом округления. В шум worldgen допускаются только `+ − × ÷ sqrt` над `double`, без `Math.Sin/Pow/Exp`; надёжнее целочисленный value/simplex-шум. `float` живёт только в Godot.
4. **Хэш.** Канонический `StateWriter` (LE, порядок по id) плюс `XxHash3` из `System.IO.Hashing` 10.0.13, общий хэш и хэши подсистем на каждом тике в отладке и в CI.
5. **Тест-реплей в CI.** Файл реплея: `{formatVersion, seed, contentHash, commands[(tick, payload)]}`. Тесты: (а) два прогона в одном процессе дают одинаковые хэши; (б) хэши совпадают с golden-файлом в репозитории, прогон в матрице `macos` (arm64) и `windows` (x64) против одного эталона; (в) save на тике N, load, продолжение: хэши совпадают с непрерывным прогоном (аналог heavy mode Factorio); (г) при расхождении тест печатает первый тик и дифф дампов подсистем.
6. **Анализаторы.** `Microsoft.CodeAnalysis.BannedApiAnalyzers` запрещает в `Core` `System.Random`, `DateTime.Now`, `Stopwatch`, `Math.Sin/Cos/Pow/Exp`, `Parallel`, `GetHashCode` в логике и `foreach` по `Dictionary` (через ревью и тесты). Целевой фреймворк `net10.0` (LTS), чтобы конверсии float→int вели себя одинаково.

## Источники

- Fiedler: [Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/), [Floating Point Determinism](https://gafferongames.com/post/floating_point_determinism/)
- Factorio: [FFF-47](https://factorio.com/blog/post/fff-47), [FFF-55](https://factorio.com/blog/post/fff-55), [FFF-188](https://factorio.com/blog/post/fff-188), [FFF-340](https://factorio.com/blog/post/fff-340), [Wiki: Desynchronization](https://wiki.factorio.com/Desynchronization)
- Bettner & Terrano: [1500 Archers on a 28.8](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond)
- Riot: [Intro](https://technology.riotgames.com/node/67), [Implementation](https://technology.riotgames.com/node/68), [Unified Clock](https://technology.riotgames.com/node/73), [Fixing Divergences](https://technology.riotgames.com/node/84)
- Photon Quantum: [Intro](https://doc.photonengine.com/quantum/v3/quantum-intro), [Fixed Point](https://doc.photonengine.com/quantum/v3/manual/quantum-ecs/fixed-point), [Replay](https://doc.photonengine.com/quantum/v3/manual/replay)
- PRNG: [prng.di.unimi.it](https://prng.di.unimi.it/), [pcg-random.org](https://www.pcg-random.org/), [dotnet Xoshiro256StarStarImpl](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Random.Xoshiro256StarStarImpl.cs)
- .NET: [Random](https://learn.microsoft.com/en-us/dotnet/api/system.random), [.NET 6 perf blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-6/), [PR #47085](https://github.com/dotnet/runtime/pull/47085), [runtime#10137 FMA](https://github.com/dotnet/runtime/issues/10137), [runtime#11263 autovec](https://github.com/dotnet/runtime/issues/11263), [runtime#43428 CRT math](https://github.com/dotnet/runtime/issues/43428), [fp→int .NET 9](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/9.0/fp-to-integer), [System.IO.Hashing на NuGet](https://www.nuget.org/packages/System.IO.Hashing)
- Unity Burst: [Float precision and determinism](https://docs.unity3d.com/Manual/burst/float-precision-determinism.html)
