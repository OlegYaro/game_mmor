# Контент в JSON: исследование (14.10.2026)

## Краткие выводы

1. **Валидация — JsonSchema.Net 9.4.1 (json-everything, MIT, draft 2020-12, net8/9/10).** С 8.0 переписан на `JsonElement` с разделением фаз build/evaluate, что подходит под «загрузить схемы один раз, валидировать много файлов». Corvus (Apache-2.0) быстрее по собственным замерам, но V5 требует .NET 9+, а его сила — кодогенерация типов из схемы, что нам не нужно. NJsonSchema тянет Newtonsoft.Json и не подтверждает 2020-12. [json-everything basics](https://docs.json-everything.net/schema/basics/), [Corvus README](https://github.com/corvus-dotnet/Corvus.JsonSchema/blob/main/README.md), [NJsonSchema nuget](https://www.nuget.org/packages/NJsonSchema/11.6.1)
2. **Десериализация — STJ + source generator в `record`-типы;** строгие опции: `UnmappedMemberHandling = Disallow` (подтверждено с .NET 8), `NumberHandling = Strict`, `PropertyNameCaseInsensitive = false`, `RespectRequiredConstructorParameters = true` (.NET 9+). В .NET 10 всё это упаковано в пресет `JsonSerializerOptions.Strict` плюс `AllowDuplicateProperties = false`. [JsonUnmappedMemberHandling](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonunmappedmemberhandling), [.NET 10 what's new](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries)
3. **Схемы генерируем из C#-типов:** `JsonSchemaExporter` есть с .NET 9 (`options.GetJsonSchemaAsNode(typeof(T))`, `TransformSchemaNode` для описаний/ограничений). Core остаётся на net8 (Godot 4.5 требует .NET 8+), генератор схем — отдельный dev-tool на net10. Рукописные схемы — только для «мета»-уровня (манифест мода, формат патча). [extract-schema](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema), [Godot C# basics](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html)
4. **Организация:** один файл на категорию (`data/creatures/*.json` — массив объектов или по файлу на сущность, оба допустимы), ID в `snake_case` (`orc_warrior`), ссылки по ID, проверка целостности ссылок — отдельный проход после загрузки всего каталога (не JSON Schema: она не умеет кросс-файловые ссылки).
5. **Горячая перезагрузка:** `FileSystemWatcher` + дебаунс 100–200 мс + пересборка неизменяемого `Catalog` целиком; симуляция держит `DefId` (интернированный int), а не ссылки на объекты определений, поэтому подмена каталога меняет параметры без перезапуска. Factorio замораживает прототипы после data-стадии (перезагрузка = рестарт), RimWorld грузит Defs один раз при старте — ориентир для нас не они, а «параметры да, структура нет». [FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher), [Factorio data lifecycle](https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html)
6. **Моды (v2):** слои каталогов `data/` → `mods/<id>/data/`, объект с тем же ID — JSON Merge Patch (RFC 7386) поверх базового; `null` удаляет поле, массивы заменяются целиком. Это тот же принцип, что `Load="Merge"` в Caves of Qud, но стандартизованный. RimWorld PatchOperations (XPath) — слишком тяжёлое для v1. [RFC 7386](https://www.rfc-editor.org/rfc/rfc7386), [Caves of Qud Objects](https://wiki.cavesofqud.com/wiki/Modding:Objects)
7. **Локализация:** Core хранит только ключи (`creature.orc_warrior.name`), переводы — CSV (`locale/strings.csv`) или PO для `TranslationServer`; Godot-слой вызывает `Tr(key)`. [Godot CSV](https://docs.godotengine.org/en/stable/tutorials/i18n/localization_using_spreadsheets.html), [Godot gettext](https://docs.godotengine.org/en/stable/tutorials/i18n/localization_using_gettext.html)

## Подробности

### 1. Валидаторы JSON Schema в .NET (версии nuget.org на 14.10.2026)

- **JsonSchema.Net 9.4.1** (11.10.2026). Драфты 6, 7, 2019-09, 2020-12 (+ готовящийся «2026/v1»); таргеты netstandard2.0, net8, net9, net10; зависимость только `JsonPointer.Net`. API: `JsonSchema.Build(JsonElement, BuildOptions)` → `schema.Evaluate(instance, EvaluationOptions { OutputFormat = Flag|List|Hierarchy })`. `$ref` между файлами — через `SchemaRegistry` (`BuildOptions`), бандлинг `SchemaRegistry.CreateBundle()` (9.1.5+). Принцип «build once, evaluate many»; Flag — самый быстрый вывод. Начиная с 9.0.0 автор ввёл «Open Source Maintenance Fee» (лицензия остаётся MIT; условия платежа для коммерческого использования — проверить перед релизом, не подтверждено). [release notes](https://docs.json-everything.net/rn-json-schema/), [LICENSE](https://github.com/json-everything/json-everything/blob/master/LICENSE)
- **Corvus.JsonSchema** (Apache-2.0). Два поколения: V4 — пакеты `Corvus.Json.*` (`Corvus.Json.Validator 5.7.7`, net8/9/10, поддержка net8 до ноября 2026), V5 — `Corvus.Text.Json.*` (net9/net10, net8 не поддерживается). Основное — генерация типизированного C# из схемы (Roslyn source generator `Corvus.Text.Json.SourceGenerator`), есть и динамический валидатор. Заявляет «10× быстрее других .NET-валидаторов» — самоотчёт, не проверено. Драфты 4, 6, 7, 2019-09, 2020-12.
- **NJsonSchema 11.6.1** (20.04.2026, MIT, net8/netstandard2.0/net462). Зависит от `Newtonsoft.Json ≥ 13.0.3`; описывает себя как «draft v4+», поддержка 2020-12 в документации не подтверждена. Сильная сторона — генерация C#/TypeScript из схем, не валидация.

### 2. Десериализация в record-типы

```csharp
public sealed record CreatureDef(
    string Id, string Body, int MaxHealth, int Speed,
    IReadOnlyList<string> Tags, IReadOnlyDictionary<string, int> Skills);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true)] // .NET 9+
[JsonSerializable(typeof(CreatureDef[]))]
[JsonSerializable(typeof(BuildingDef[]))]
internal partial class ContentJsonContext : JsonSerializerContext { }

var defs = JsonSerializer.Deserialize(utf8, ContentJsonContext.Default.CreatureDefArray)!;
```

- `UnmappedMemberHandling` доступен как опция сериализатора и как `[JsonUnmappedMemberHandling]` на типе — с .NET 8. Для `JsonSourceGenerationOptionsAttribute` в .NET 8+ большинство опций задаётся атрибутом. Параметры `record` без значения по умолчанию = обязательные при `RespectRequiredConstructorParameters` (.NET 9+); на net8 — `required` + `[JsonRequired]`.
- Детерминизм чисел: поля баланса, влияющие на симуляцию, — `int` (или фиксированная точка `int` в тысячных); `float` допустим только для визуальных параметров. `NumberHandling.Strict` запрещает числа в кавычках; `AllowDuplicateProperties=false` (.NET 10) ловит дубли ключей — на net8 это делает JSON Schema (`additionalProperties:false` не ловит дубли; дубли ловит только парсер) — ограничение, «не подтверждено» обходное решение на net8 кроме предварительного прохода `Utf8JsonReader`.
- Запрет рефлексии: `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>` в Core — любой пропущенный тип падает сразу, а не в AOT-сборке.

### 3. Организация файлов, ID, ссылки, схемы

```
data/
  creatures/   humanoids.json  animals.json      # массив CreatureDef
  buildings/   *.json
  recipes/     *.json
  items/       *.json
  events/      *.json
  balance.json                                   # единичный объект
  schema/      creature.schema.json ...          # сгенерированы из C#
```

- ID: `^[a-z][a-z0-9_]*$`, уникальность в пределах категории; ссылки — `"recipe": "smelt_iron"`, `"drops": ["iron_ore"]`. Для модов зарезервировать префикс `modid:` (`core` — неявный); регулярка в схеме допускает `^([a-z0-9_]+:)?[a-z][a-z0-9_]*$`.
- Проверка ссылок: после загрузки всех категорий — `ReferenceChecker` обходит DTO (атрибут `[DefRef(typeof(ItemDef))]` на свойстве или явный список полей) и собирает все ошибки сразу, не падая на первой: `creatures/humanoids.json#orc_warrior.drops[1] → unknown item 'iron_ore2'`.
- Схемы из C#: `JsonSchemaExporter` (.NET 9+) отдаёт `JsonNode`; `TreatNullObliviousAsNonNullable=true`; `TransformSchemaNode` переносит `[Description]`, `[Range]`, `pattern` для ID. Экспортер пишет `additionalProperties:false`, если в опциях `UnmappedMemberHandling=Disallow`. Генерация — в `tools/SchemaGen` (net10) с CI-проверкой «схемы в репозитории совпадают со сгенерированными». Схемы нужны для IDE-подсказок (`$schema` в каждом файле) и для внешних авторов модов; в рантайме валидировать схемой + STJ строгим режимом (двойная защита дешёвая: каталог грузится раз).

```csharp
var opts = new JsonSerializerOptions(ContentJsonContext.Default.Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
JsonNode schema = opts.GetJsonSchemaAsNode(typeof(CreatureDef[]),
    new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (ctx, node) => AddDescriptionsAndIdPattern(ctx, node) });
```

### 4. Горячая перезагрузка (dev-режим)

- `FileSystemWatcher(dataRoot) { IncludeSubdirectories = true, Filter = "*.json", NotifyFilter = LastWrite | FileName }`. Документация прямо предупреждает: одна операция даёт несколько событий, при переполнении буфера события теряются (`Error`) — поэтому дебаунс и перезагрузка **всего каталога**, а не одного файла.
- Поток: событие → таймер 150 мс → `CatalogLoader.Load(dataRoot)` в фоне → валидация (schema + ссылки) → если ошибок нет, `Interlocked.Exchange(ref _catalog, newCatalog)` на границе тика симуляции; если есть — лог, старый каталог остаётся.
- Что меняется на лету: числа, строки-ключи, списки тегов, рецепты (вход/выход/время). Что нельзя: удаление ID, на которые ссылаются живые сущности (проверяется — перезагрузка отклоняется с сообщением), изменение структуры типов (это перекомпиляция). Сущности симуляции хранят `DefId` (`readonly struct DefId(int)`), таблица `DefId → строковый ID` стабильна между перезагрузками, новые ID дописываются в конец.
- Аналоги: Factorio — `data.lua` → `data-updates.lua` → `data-final-fixes.lua`, потом прототипы заморожены, `data` недоступна в control-стадии; RimWorld — `DefDatabase<T>` заполняется при старте, переопределение по `defName`; Caves of Qud — XML-слияние по `Name` с `Load="Merge"`. Ни у одного из них hot-reload контента в вики не описан («не подтверждено»).

### 5. Моды (задел на v2)

- Слои: `[core data/] + [mods/a/data/] + [mods/b/data/]` в порядке `mods.json`. Один проход: для каждого файла каждого слоя объекты попадают в `Dictionary<id, JsonObject>` категории; совпадение ID — `MergePatch(existing, incoming)`; поле `"$replace": true` отключает слияние (полная замена); `"$delete": true` удаляет объект.
- RFC 7386 (алгоритм на ~10 строк): объект-в-объект рекурсивно, `null` — удаление ключа, любые не-объекты (включая массивы) — замена целиком. Ограничение «нельзя дописать элемент в массив» решается соглашением: списки тегов/дропов — объект `{ "iron_ore": 1 }` вместо массива там, где моды захотят дописывать.
- Патчи применяются к `JsonNode` **до** STJ-биндинга и валидации, поэтому строгая проверка покрывает и результат слияния. RimWorld PatchOperations (XPath Add/Replace/Remove/Sequence/FindMod) — можно добавить позже как `"$ops": [...]` без изменения формата файлов.

```csharp
static JsonNode? MergePatch(JsonNode? target, JsonNode? patch) {
    if (patch is not JsonObject po) return patch?.DeepClone();
    var to = target as JsonObject ?? new JsonObject();
    foreach (var (k, v) in po.ToList())
        if (v is null) to.Remove(k); else to[k] = MergePatch(to[k], v);
    return to;
}
```

### 6. Локализация

- В JSON контента только ключи; ключ выводится из ID по соглашению (`creature.orc_warrior.name`, `.desc`), явное поле `"name_key"` — лишь для исключений. Core не содержит ни одной показываемой строки: события отдают `(string key, object[] args)`.
- Переводы: `locale/strings.csv` — первая колонка ключи, заголовок — коды локалей (`en`, `ru`), UTF-8 без BOM, ключи регистрозависимы; Godot импортирует в `.translation` и регистрирует в `Project Settings → Localization`. PO/POT — когда появятся переводчики (контекст `msgctxt`, плюралы `tr_n`, файл на локаль). Проверка «каждый ключ из каталога есть в CSV» — тест в Core (CSV читается как данные, без Godot).

## Сравнение

| Критерий | JsonSchema.Net 9.4.1 | Corvus (V4 5.7.7 / V5) | NJsonSchema 11.6.1 |
|---|---|---|---|
| Дата | 11.10.2026 | 14.10.2026 | 20.04.2026 |
| Draft 2020-12 | да | да | не подтверждено |
| .NET | netstandard2.0, net8/9/10 | V4: net8/9/10; V5: net9/10 | net8, netstandard2.0, net462 |
| Основа | System.Text.Json (`JsonElement`) | STJ + генерация типов | Newtonsoft.Json |
| Source-gen | нет (не нужен) | да (типы из схемы) | нет |
| Производительность | «build once, evaluate many», overhaul в 8.0 | заявлено 10× (самоотчёт) | не измерялось |
| Лицензия | MIT (+ maintenance fee с 9.0, условия уточнить) | Apache-2.0 | MIT |

## Рекомендация для проекта

1. **Core (net8.0):** STJ source generator, record-DTO, строгие опции; каталог — неизменяемый `Catalog` с `DefId`; ссылки проверяются `ReferenceChecker` после загрузки всех категорий; все ошибки — списком.
2. **Валидация JSON Schema:** JsonSchema.Net 9.4.1 в `Core.Content` (загрузка при старте и hot-reload); схемы хранятся в `data/schema/` и генерируются `tools/SchemaGen` (net10, `JsonSchemaExporter`); CI сравнивает. Условия maintenance fee json-everything проверить до коммерческого релиза; запасной вариант — Corvus V4 (Apache-2.0).
3. **Файлы:** по категории, массивы объектов, `$schema` в каждом; ID `snake_case` с опциональным `modid:` префиксом.
4. **Hot-reload:** только в dev-сборке (`#if DEBUG` или флаг), полная пересборка каталога, подмена на границе тика, отказ при удалённых ID.
5. **Моды:** заложить сейчас слои + RFC 7386 на `JsonNode` (≈50 строк), остальное (ops, зависимости, версии модов) — v2.
6. **Локализация:** ключи из ID, CSV в `locale/`, тест полноты переводов в Core.

## Источники

- json-everything basics — https://docs.json-everything.net/schema/basics/ ; release notes — https://docs.json-everything.net/rn-json-schema/ ; nuget 9.4.1 — https://www.nuget.org/packages/JsonSchema.Net/9.4.1 ; LICENSE — https://github.com/json-everything/json-everything/blob/master/LICENSE
- Corvus.JsonSchema README — https://github.com/corvus-dotnet/Corvus.JsonSchema/blob/main/README.md ; nuget Corvus.Json.Validator 5.7.7 — https://www.nuget.org/packages/Corvus.Json.Validator/5.7.7
- NJsonSchema nuget 11.6.1 — https://www.nuget.org/packages/NJsonSchema/11.6.1
- STJ source generation — https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
- JsonUnmappedMemberHandling (.NET 8+) — https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonunmappedmemberhandling
- .NET 10: Strict, AllowDuplicateProperties — https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries
- JsonSchemaExporter (.NET 9+) — https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema ; API — https://learn.microsoft.com/en-us/dotnet/api/system.text.json.schema.jsonschemaexporter
- FileSystemWatcher — https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher
- Godot C# (.NET 8+ для 4.5) — https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html
- Godot CSV — https://docs.godotengine.org/en/stable/tutorials/i18n/localization_using_spreadsheets.html ; gettext — https://docs.godotengine.org/en/stable/tutorials/i18n/localization_using_gettext.html ; i18n — https://docs.godotengine.org/en/stable/tutorials/i18n/internationalizing_games.html
- RFC 7386 JSON Merge Patch — https://www.rfc-editor.org/rfc/rfc7386
- RimWorld PatchOperations — https://rimworldwiki.com/wiki/Modding_Tutorials/PatchOperations
- Factorio data lifecycle — https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html
- Caves of Qud Objects (Load="Merge") — https://wiki.cavesofqud.com/wiki/Modding:Objects
