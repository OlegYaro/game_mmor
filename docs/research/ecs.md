# ECS в C#: готовые библиотеки против простых компонентов и систем

Дата исследования: 14.10.2026. Контекст: `Core` на чистом C#, около 100 жителей, 300 существ и сущности фракций (400–500 сущностей плюс снаряды и предметы), тик 10 Гц ≤ 8 мс, обязательный тест-реплей с хэшем и версионированные сохранения, macOS arm64 и Windows x64.

## Краткие выводы

1. **Ни одна из восьми библиотек не документирует детерминированный порядок итерации в запросах.** У Arch мейнтейнер отказался делать упорядоченные запросы: «use `world.GetEntities()`… and do the ordering yourself» ([Arch#227](https://github.com/genaray/Arch/issues/227)). У Entitas порядок «deterministic, but not ordered» из-за HashSet ([Entitas#768](https://github.com/sschmid/Entitas/issues/768)). Flecs даёт порядок только через `order_by` ([flecs Queries](https://www.flecs.dev/flecs/Queries.html)). У остальных тема не описана (не подтверждено).
2. **Живые проекты:** Friflo.Engine.ECS 3.6.0 (23.03.2026, MIT, есть net10, встроенная JSON-сериализация) ([NuGet](https://www.nuget.org/packages/Friflo.Engine.ECS)), fennecs 0.7.3 (05.08.2026, MIT, только net10, сериализации нет) ([NuGet](https://www.nuget.org/packages/fennecs)), Arch 2.1.0 (27.05.2025, Apache-2.0, коммиты в master до 09.2026) ([NuGet](https://www.nuget.org/packages/Arch)).
3. **Заброшены или почти заброшены:** DefaultEcs (последний стабильный релиз 2022, мейнтейнер «would not say "actively"», [#193](https://github.com/Doraku/DefaultEcs/issues/193)), Entitas (релиз 2022, коммиты до 2023), Svelto.ECS («feature complete», 3.5.2 от 07.2024), Flecs.NET (стабильный 4.0.3 от 11.2024, PR на flecs 4.1 не влиты). Morpeh ориентирована на Unity, хотя есть пакет для .NET. Последний релиз 2024.1.1 вышел 01.04.2025.
4. **Масштаб.** Библиотеки соревнуются на миллионах сущностей. На 500 сущностях простой цикл по массиву займёт микросекунды, так что выигрыш в скорости нам не нужен, а неявный порядок, внешний формат сериализации и зависимость от чужого релизного цикла становятся издержками.
5. **Рекомендация: свой тонкий слой.** Плотные массивы структур по типам компонентов, generational ids, детерминированный аллокатор id, итерация по возрастанию индекса, сериализация своим `StateWriter`, тем же, что считает хэш ([determinism.md](determinism.md)). Если понадобится готовая библиотека, первый кандидат Friflo, с обязательной сортировкой результатов запроса по id.

## Подробности

### 1. Библиотеки (проверено по GitHub и NuGet 14.10.2026)

| Библиотека | Последний релиз | Активность | TFM | Лицензия | Аллокации | Порядок итерации | Сериализация |
|---|---|---|---|---|---|---|---|
| [Arch](https://github.com/genaray/Arch) | 2.1.0, 27.05.2025 ([releases](https://github.com/genaray/Arch/releases)) | master до 22.09.2026, ~88 коммитов с 2025, 33 issue | netstandard2.1, net6.0, net8.0 | Apache-2.0 | Явного обещания zero-alloc в README нет | Не документирован. Упорядоченные запросы отклонены ([#227](https://github.com/genaray/Arch/issues/227)), вопрос о детерминизме закрыт без ответа ([#276](https://github.com/genaray/Arch/issues/276)), открыт [#269](https://github.com/genaray/Arch/issues/269) «consistent component id» | Отдельный пакет [Arch.Persistence](https://www.nuget.org/packages/Arch.Persistence) 2.0.0 (MessagePack/Utf8Json). Открыт баг [#154](https://github.com/genaray/Arch.Extended/issues/154) AccessViolation при разном порядке десериализации |
| [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS) | 3.6.0, 23.03.2026. Превью 4.0.0-preview.2, 01.05.2026, без release notes | коммиты до 13.10.2026, 38 issue | netstandard2.1 … net10.0 (csproj) | MIT | «Zero allocations after buffers are large enough» | Не документирован (не подтверждено) | Встроенный JSON `EntitySerializer` ([docs](https://friflo.gitbook.io/friflo.engine.ecs/documentation/entity#json-serialization)), только поля, без свойств |
| [DefaultEcs](https://github.com/Doraku/DefaultEcs) | 0.17.2, 20.02.2022. Бета 0.18.0-beta01, 2023 | последний коммит 01.2023 | netstandard1.1–2.1 | MIT-0 | Явных обещаний нет | Не документирован | Встроенные `BinarySerializer`/`TextSerializer` ([README](https://github.com/Doraku/DefaultEcs#serialization)) |
| [Flecs.NET](https://github.com/BeanCheeseBurrito/Flecs.NET) | 4.0.3, 23.11.2024. Сборка 4.0.4-build.548, 12.03.2025 ([NuGet](https://www.nuget.org/packages/Flecs.NET.Release)) | последний коммит 28.04.2025 | net8.0 (4.0.3), net9.0 (main) | MIT | «minimal GC interaction». Ядро на C (нативные бинарники под каждую ОС) | Без гарантий. Сортировка через `order_by` ([flecs](https://www.flecs.dev/flecs/Queries.html)) | Встроенный JSON flecs: `World.ToJson/FromJson` |
| [fennecs](https://github.com/outfox/fennecs) | 0.7.3, 05.08.2026 ([releases](https://github.com/outfox/fennecs/releases)) | коммиты до 08.10.2026, API ещё меняется (0.x) | только net10.0 | MIT | Явных обещаний нет | Не документирован, сортировку обсуждали в [#28](https://github.com/outfox/fennecs/issues/28) | Нет |
| [Entitas](https://github.com/sschmid/Entitas) | 1.14.1 (GitHub, 09.2022), 1.14.2 ([NuGet](https://www.nuget.org/packages/Entitas), 11.2022) | последний коммит 07.2023 | netstandard2.1 | MIT | «optimal in a GC environment» | HashSet: «deterministic, but not ordered» ([#768](https://github.com/sschmid/Entitas/issues/768)) | Нет. Требует кодогенерации |
| [Svelto.ECS](https://github.com/sebas77/Svelto.ECS) | 3.5.2, 14.07.2024 ([NuGet](https://www.nuget.org/packages/Svelto.ECS)) | 2 коммита с 2025, «feature complete» | netstandard2.0/2.1 | MIT | «all the Svelto operations are memory allocation free» (кроме ресайза) | Не документирован | Есть папка Serialization, в README не описана |
| [Morpeh](https://github.com/scellecs/morpeh) | 2024.1.1, 01.04.2025 ([NuGet](https://www.nuget.org/packages/Scellecs.Morpeh)). Ветка 2025.1 не выпущена | ветки stage-* до 09.2026 | netstandard2.1 | MIT | Только качественные заявления | Не документирован | Мировой сериализации нет. Jobs/Burst «Supported only in Unity» |

Бенчмарки: [Doraku/Ecs.CSharp.Benchmark](https://github.com/Doraku/Ecs.CSharp.Benchmark) (коммиты до 04.2026, но версии многих библиотек устаревшие), [blackbone/other-ecs-benchmarks](https://github.com/blackbone/other-ecs-benchmarks) (свежие версии, дата прогона не указана), [friflo common-use-cases](https://github.com/friflo/ECS.CSharp.Benchmark-common-use-cases) (08.2024, ведёт автор Friflo). Для нашего масштаба они почти ничего не говорят.

**Почему порядок в archetype-ECS хрупкий.** Это общее свойство архитектуры; для конкретных библиотек оно не подтверждено документацией. Сущности лежат в чанках по архетипам. Добавили или сняли компонент (например, `Hungry`, `InCombat`), и сущность переехала в другой архетип, а на её место встала последняя (swap-back). Порядок обхода зависит от истории операций и от порядка, в котором создавались архетипы и id типов компонентов (ср. [Arch#269](https://github.com/genaray/Arch/issues/269)). Внутри одного прогона это детерминировано. Но после загрузки сейва архетипы создаются в другом порядке, и обход расходится с непрерывным прогоном, поэтому тест «save на тике N, load, продолжение» сломается. Лечится сортировкой по id перед каждой системой или сериализацией внутреннего расположения, а это обнуляет удобство библиотеки.

### 2. Альтернатива: простые компоненты и системы

```csharp
public readonly record struct EntityId(int Index, int Generation); // value type, stable, serializable

public sealed class EntityRegistry {
    private int[] _generation = new int[1024];
    private bool[] _alive = new bool[1024];
    private int _count;                        // high-water mark
    private readonly MinIntHeap _free = new(); // lowest free index first -> reproducible after load
    public EntityId Create() {
        int i = _free.Count > 0 ? _free.Pop() : _count++;
        EnsureCapacity(i);
        _alive[i] = true;
        return new EntityId(i, _generation[i]);
    }
    public void Destroy(EntityId e) { // deferred to end of tick by CommandBuffer
        _alive[e.Index] = false; _generation[e.Index]++; _free.Push(e.Index);
    }
    public bool IsAlive(EntityId e) => _alive[e.Index] && _generation[e.Index] == e.Generation;
}

// Component storage: dense by entity index (sparse is fine at 500 entities)
public sealed class ComponentStore<T> where T : struct {
    private T[] _data = new T[1024];
    private bool[] _has = new bool[1024];
    public ref T Get(int index) => ref _data[index];
    // Systems iterate i = 0.._count ascending: order == entity index, always.
}
```

- **Плюсы для детерминизма.** Порядок обхода задан явно (возрастание индекса), нет хэш-кодов и `Dictionary`. Свободные индексы выдаются от меньшего к большему (min-heap): после загрузки сейва получаются те же id, что и в непрерывном прогоне, если сериализовать набор свободных индексов или пересобрать его из `_alive`. Создание и удаление сущностей откладывается в буфер команд и применяется в конце тика в фиксированном порядке, как структурные изменения в ECS-фреймворках.
- **Плюсы для сериализации.** Сейв просто записывает массивы по порядку (`generation`, `alive`, компоненты) через наш `StateWriter`. Формат принадлежит нам: версия, миграции (FR-SAVE), тот же байтовый поток идёт в хэш. Нет MessagePack, рефлексии и внешних форматов JSON.
- **Плюсы для тестов.** `new World(seed)`, несколько команд, `Step()` N раз, проверка полей. Ни глобального состояния, ни кодогенерации, ни нативных библиотек (как у Flecs.NET), ни нестабильного API версии 0.x (как у fennecs).
- **Минусы.** Запросы по нескольким компонентам приходится писать руками (проверка `_has` в цикле). Нет готовых связей, иерархий и инструментов отладки. Память растёт по максимальному индексу, а не по числу живых сущностей (на 500 сущностях это неважно). При десятках тысяч сущностей понадобятся плотные пулы или архетипы. Шаблонный код можно сократить source-генератором позже.
- **Вариант для колони-сима.** Можно вообще без «универсального» ECS: типизированные таблицы `Colonists`, `Creatures`, `Factions`, `Items`, у каждой массив структур и общий `EntityId`. Системы (`NeedsSystem`, `JobSystem`, `CombatSystem`, `PathfindingSystem`) — статические функции `(World, TickContext)`, порядок вызова фиксирован в `Simulation.Step`.

### 3. Что важно для детерминизма в слое сущностей

1. **Стабильный порядок итерации.** Везде, где результат зависит от порядка (кто первым берёт задачу, кто получает урон, кто съедает последнюю еду), обходим по возрастанию `EntityId.Index` или сортируем по `(ключ, Index)`. `Array.Sort` нестабилен, но с полным порядком это не мешает ([learn: Array.Sort](https://learn.microsoft.com/en-us/dotnet/api/system.array.sort)).
2. **Никаких хэш-кодов объектов и порядка `Dictionary`.** Порядок обхода `Dictionary` не определён ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2)), `string.GetHashCode` рандомизирован на процесс ([learn](https://learn.microsoft.com/en-us/dotnet/api/system.string.gethashcode)). Словарь `EntityId → X` допустим только для поиска. Ссылки между сущностями хранятся как `EntityId`, а не как ссылки на объекты (так Riot заменили указатели хэндлами, [Riot](https://technology.riotgames.com/node/84)).
3. **Воспроизводимое создание и удаление id.** Аллокатор детерминирован (min-heap или FIFO), его состояние входит в сейв и в хэш. Удаление отложено до конца тика. Поколение увеличивается при удалении, поэтому висячий `EntityId` не укажет на новую сущность.
4. **Порядок систем фиксирован в коде.** Никакой регистрации через рефлексию или сканирование сборки, где порядок типов не гарантирован.
5. **Проверка тестом.** Прогон с save/load посередине должен дать те же хэши, что и прогон без перерыва. Это ловит скрытое несохраняемое состояние (аналог heavy mode Factorio, [wiki](https://wiki.factorio.com/Desynchronization)).

## Рекомендация для проекта

**Своя простая модель сущностей, без внешней ECS-библиотеки на этапах M0–M2.**

- **Почему.** (1) Детерминизм порядка нужен по определению, а ни одна библиотека его не документирует. С ними пришлось бы сортировать вручную в каждой системе и всё равно рисковать расхождением после загрузки. (2) Сохранения с версиями и миграциями проще на своём бинарном формате, который заодно считает хэш. Готовая сериализация есть только у Friflo (JSON, только поля), Arch.Persistence (с открытым багом порядка) и DefaultEcs/Flecs.NET (проекты стоят). (3) 400–500 сущностей при бюджете 8 мс не дают поводов для оптимизации под кэш: узкие места будут в поиске пути и AI, а не в обходе компонентов. (4) Нет зависимости от темпа релизов: Arch без релиза с 05.2025, Flecs.NET и DefaultEcs стоят, fennecs ещё 0.x, у Friflo 4.0 превью без changelog.
- **Как.** `EntityId(Index, Generation)`, `EntityRegistry` с min-heap свободных индексов, `ComponentStore<T>` или типизированные таблицы, `CommandBuffer` для структурных изменений, фиксированный список систем в `Simulation.Step`. Сериализация и хэш идут через общий `StateWriter`. Анализаторы запрещают `foreach` по `Dictionary`/`HashSet` в системах.
- **Тесты M0.** (а) create/destroy/create возвращает тот же индекс с новым поколением; (б) save → load → `Create()` выдаёт тот же id, что без сейва; (в) хэш после 10 000 тиков совпадает в двух прогонах и на двух ОС.
- **Когда пересмотреть.** Если число сущностей вырастет до десятков тысяч (снаряды, предметы на земле, растения) или понадобятся связи и иерархии, взять Friflo.Engine.ECS 3.x (MIT, net10, активный, JSON-сериализация). Обязательно оставить свой `EntityId` в компонентах, сортировать результаты запросов по нему и сериализовать своим форматом. Arch оставить вторым вариантом. Перед выбором прогнать наш тест «save/load посередине».

## Источники

- Arch: [GitHub](https://github.com/genaray/Arch), [releases](https://github.com/genaray/Arch/releases), [2.0.0 notes](https://github.com/genaray/Arch/releases/tag/2.0.0), [NuGet Arch](https://www.nuget.org/packages/Arch), [NuGet Arch.Persistence](https://www.nuget.org/packages/Arch.Persistence), [#227](https://github.com/genaray/Arch/issues/227), [#276](https://github.com/genaray/Arch/issues/276), [#269](https://github.com/genaray/Arch/issues/269), [Arch.Extended](https://github.com/genaray/Arch.Extended)
- Friflo: [GitHub](https://github.com/friflo/Friflo.Engine.ECS), [releases](https://github.com/friflo/Friflo.Engine.ECS/releases), [NuGet](https://www.nuget.org/packages/Friflo.Engine.ECS), [JSON serialization](https://friflo.gitbook.io/friflo.engine.ecs/documentation/entity#json-serialization), [query optimization](https://friflo.gitbook.io/friflo.engine.ecs/documentation/query-optimization)
- DefaultEcs: [GitHub](https://github.com/Doraku/DefaultEcs), [NuGet](https://www.nuget.org/packages/DefaultEcs), [#193 maintenance](https://github.com/Doraku/DefaultEcs/issues/193)
- Flecs.NET: [GitHub](https://github.com/BeanCheeseBurrito/Flecs.NET), [NuGet](https://www.nuget.org/packages/Flecs.NET.Release), [flecs Queries (order_by)](https://www.flecs.dev/flecs/Queries.html)
- fennecs: [GitHub](https://github.com/outfox/fennecs), [releases](https://github.com/outfox/fennecs/releases), [NuGet](https://www.nuget.org/packages/fennecs), [#28](https://github.com/outfox/fennecs/issues/28)
- Entitas: [GitHub](https://github.com/sschmid/Entitas), [NuGet](https://www.nuget.org/packages/Entitas), [#768](https://github.com/sschmid/Entitas/issues/768)
- Svelto.ECS: [GitHub](https://github.com/sebas77/Svelto.ECS), [NuGet](https://www.nuget.org/packages/Svelto.ECS)
- Morpeh: [GitHub](https://github.com/scellecs/morpeh), [NuGet](https://www.nuget.org/packages/Scellecs.Morpeh)
- Бенчмарки: [Doraku](https://github.com/Doraku/Ecs.CSharp.Benchmark), [blackbone](https://github.com/blackbone/other-ecs-benchmarks), [friflo](https://github.com/friflo/ECS.CSharp.Benchmark-common-use-cases)
- .NET: [Dictionary](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2), [Array.Sort](https://learn.microsoft.com/en-us/dotnet/api/system.array.sort), [String.GetHashCode](https://learn.microsoft.com/en-us/dotnet/api/system.string.gethashcode)
- Практика: [Riot: Fixing Divergences](https://technology.riotgames.com/node/84), [Factorio Desynchronization](https://wiki.factorio.com/Desynchronization)
