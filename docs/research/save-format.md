# Формат сохранений: исследование (14.10.2026)

## Краткие выводы

1. **Формат v1 — System.Text.Json (STJ) с source generator, в UTF-8, без отступов, обёрнутый в Brotli.** Это единственный вариант из списка, где миграции можно писать на уровне JSON-дерева (`JsonNode`) до десериализации, а дифф сохранений читается `git diff`/`jq`. Цель 1024² + 50 жителей ≤ 2 с / ≤ 20 МБ достигается с большим запасом (оценка ниже: 0,2–1,5 МБ после сжатия). [STJ source-gen](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
2. **Бинарные форматы оставляем как запасной план.** MessagePack-CSharp 3.1.12 — ближайший кандидат (MIT, int-ключи, source generator). MemoryPack 1.21.4 — самый быстрый, но не self-describing и завязан на раскладку памяти; миграции по дереву невозможны. protobuf-net 3.4.30 — Apache-2.0, номера полей, AOT через BuildTools. Собственный `BinaryWriter` — только если нужен полный контроль над байтами, цена — свой фреймворк версионирования.
3. **Версионирование:** целочисленное `formatVersion` в заголовке + линейная цепочка миграций `v1→v2→…→vN`, применяемых к `JsonNode` до биндинга в DTO; каждая миграция покрыта фикстурой старого файла. Это схема Minecraft DataFixerUpper (DataVersion в каждом файле/чанке) и Factorio (миграции по имени, отметка «применено» в сейве). [Minecraft Data version](https://minecraft.wiki/w/Data_version), [Factorio migrations](https://lua-api.factorio.com/latest/auxiliary/migrations.html)
4. **Разница тайлов:** чанки 32×32, на чанк — битовая маска изменённых тайлов (128 байт) + плотный массив изменённых тайлов. При 10 % изменений ~0,55 МБ до сжатия. Сжатие — встроенный `BrotliStream` (.NET 9+ даёт `BrotliCompressionOptions.Quality`); zstd — через `ZstdSharp.Port 0.8.8` (MIT, чистый C#), встроенного zstd в .NET 8–10 нет (`ZstandardStream` — только preview .NET 11). [BrotliCompressionOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotlicompressionoptions), [ZstandardStream proposal](https://github.com/dotnet/runtime/issues/59591)
5. **Тест идентичности:** ломают его `Dictionary` (порядок вставки), `HashSet`, `DateTime.Now`, `Guid.NewGuid()`, `float` через культуру, `NaN`. Решение — канонический сериализатор: отсортированные ключи, явный `JsonPropertyOrder`, числовые ID вместо GUID, запрет NaN/Infinity. Плюс тест-страж через рефлексию: каждое поле состояния либо сериализуется, либо помечено `[JsonIgnore]` с комментарием «почему».
6. **Запись:** `temp → Flush(flushToDisk) → File.Move(tmp, dst, overwrite: true)` в той же директории; `File.Replace` — если нужен бэкап предыдущего файла. Путь директории Core получает параметром; Godot отдаёт `user://` = `~/Library/Application Support/Godot/app_userdata/<project>` (macOS) и `%APPDATA%\Godot\app_userdata\<project>` (Windows). [Godot data paths](https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html), [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace)

## Подробности

### 1. Кандидаты на формат (версии проверены на nuget.org 14.10.2026)

- **System.Text.Json.** Входит в .NET 8/9/10 (пакет 10.0.13; 11.0 — rc.2). Source generator (`JsonSerializerContext`) даёт AOT/trimming и отсутствие рефлексии; `JsonSerializerIsReflectionEnabledByDefault=false` ловит случайную рефлексию. Версионирование схемы: добавление поля — бесплатно (default), удаление — старое поле игнорируется (`UnmappedMemberHandling.Skip`) или ловится (`Disallow`, .NET 8+). Детерминизм: свойства пишутся в порядке объявления (управляется `[JsonPropertyOrder]`), числа инвариантной культурой, `double` — кратчайшая round-trip форма начиная с .NET Core 3.0. Лицензия MIT (часть .NET). [source-gen](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation), [JsonUnmappedMemberHandling](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonunmappedmemberhandling), [float formatting .NET Core 3.0](https://devblogs.microsoft.com/dotnet/floating-point-parsing-and-formatting-improvements-in-net-core-3-0/)
- **MessagePack-CSharp 3.1.12** (обновлён 12.10.2026, MIT, netstandard2.0 + оптимизации для net8+). Source generator в пакете `MessagePackAnalyzer`, резолвер — `[GeneratedMessagePackResolver]`. Версионирование: `[Key(int)]` → массив (компактно, пропуски индексов = `nil`), `[Key("name")]` → map; отсутствующие ключи = default; удалённый индекс нельзя переиспользовать. Встроенный LZ4 (`Lz4BlockArray`). Порядок `Dictionary` не нормализуется. Внимание: версии 3.1.0–3.1.6 и 2.5.x<301 имеют CVE (июнь 2026: LZ4 OOB-чтение CVE-2026-48109, stack overflow в timestamp-ext CVE-2026-48502 и др.) — брать ≥ 3.1.7. [README](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/master/README.md), [nuget](https://www.nuget.org/packages/MessagePack/3.1.12), [CVE-2026-48109](https://www.tenable.com/plugins/container-security/443098)
- **MemoryPack 1.21.4** (nuget-дата 12.02.2025, MIT, netstandard2.1/net7/net8; .NET 10 — «не подтверждено» отдельным таргетом, но netstandard2.1 работает). Только source generator, без IL.Emit. Формат не self-describing: поля в порядке объявления, имена не хранятся, unmanaged-структуры пишутся как raw memory с паддингом, только little-endian. Версионирование: по умолчанию можно только добавлять поля в конец; `GenerateType.VersionTolerant` + `[MemoryPackOrder]` разрешает удаление (медленнее, больше байт). Встроенный Brotli. [README](https://github.com/Cysharp/MemoryPack/blob/main/README.md)
- **protobuf-net 3.4.30** (16.09.2026, Apache-2.0, net8.0/netstandard2.0/net462). С 3.3 — build-time генерация сериализаторов (AOT/trimming) через BuildTools в основном пакете. Версионирование — номера полей `[ProtoMember(n)]`, имена не хранятся, менять номер = менять данные. Порядок map не гарантируется (нужна сортировка перед записью). [README](https://github.com/protobuf-net/protobuf-net/blob/main/README.md), [nuget](https://www.nuget.org/packages/protobuf-net/3.4.30)
- **Собственный BinaryWriter.** Полный контроль над байтами, нулевые зависимости, детерминизм «по построению». Минус: версионирование, пропуск неизвестных полей, отладка — всё своё; дифф сейвов невозможен без своего дампера.

### 2. Версионирование и миграции

Заголовок файла (несжатая первая строка JSON) содержит `formatVersion`, `gameVersion`, `seed`, `codec`. Миграции — список `IMigration { int From; JsonNode Apply(JsonNode root); }`, применяемых последовательно, пока `From < Current`. Миграция на уровне `JsonNode`, а не DTO: DTO всегда соответствуют только текущей версии, старые классы не плодятся. Тест: папка `fixtures/saves/v1/*.save.json` → загрузить → сравнить хеш состояния с эталоном.

```csharp
public sealed record SaveHeader(int FormatVersion, string GameVersion, long Seed, string Codec);

public interface ISaveMigration { int From { get; } JsonNode Migrate(JsonNode root); }

// v1 -> v2: field "hunger" renamed to "needs.food"
sealed class M1To2 : ISaveMigration {
    public int From => 1;
    public JsonNode Migrate(JsonNode root) {
        foreach (var c in root["colonists"]!.AsArray()) {
            var o = c!.AsObject();
            if (o.Remove("hunger", out var v)) o["needs"] = new JsonObject { ["food"] = v };
        }
        return root;
    }
}
```

Аналоги: Minecraft пишет `DataVersion` в `level.dat` и в каждый чанк, DataFixerUpper строит цепочку `DataFix` между `Schema` и оптимизирует её в один конвертер. Factorio: `migrations/*.json` (переименование прототипов) и `*.lua` (правка состояния), сейв запоминает по имени, какие миграции уже применены. RimWorld: XML `.rws`, `<meta><gameVersion>` + список модов; `Scribe_Values.Look(ref x, "label", defaultValue)` даёт default для отсутствующих полей; класс `Verse.BackCompatibility` (`BackCompatibleDefName`, `GetBackCompatibleType`) подменяет старые имена дефов/типов при загрузке — подтверждено по stack-trace, документации нет. [DataFixerUpper](https://github.com/Mojang/DataFixerUpper/blob/master/README.md), [RimWorld Save file](https://rimworldwiki.com/wiki/Save_file), [BackCompatibility trace](https://steamcommunity.com/workshop/filedetails/discussion/1321849735/3110267314139419788)

### 3. Разница тайлов

Мир 1024² = 1 048 576 тайлов, чанк 32×32 = 1024 чанка. На чанк: `ulong[16]` битмаска (128 Б) + массив изменённых тайлов в порядке возрастания индекса (сортированный порядок = детерминизм). Тайл-диф = 4 Б (`ushort terrain`, `ushort flags`); если нужен `byte height` — 5–6 Б.

| Доля изменений | Тайлов | Маски | Данные (4 Б) | Итого raw | После Brotli q5 (оценка ×0,4–0,6) |
|---|---|---|---|---|---|
| 5 % | 52 429 | 128 КБ | 205 КБ | ~0,33 МБ | 0,13–0,2 МБ |
| 10 % | 104 858 | 128 КБ | 410 КБ | ~0,54 МБ | 0,2–0,33 МБ |
| 100 % (полный снапшот, запасной режим) | 1 048 576 | — | 4,0 МБ | 4,0 МБ | ~0,5–1,5 МБ |

Чанки без изменений в файл не попадают. В JSON массивы тайлов пишутся как Base64-строки (`byte[]` STJ кодирует в Base64 автоматически), так что дифф тайлов остаётся бинарным блоком внутри текстового файла. Сжатие: `BrotliStream` встроен с .NET Core 2.1, качество через `BrotliCompressionOptions.Quality` (.NET 9+; в .NET 8 только `CompressionLevel`). zstd: `ZstdSharp.Port 0.8.8` (29.04.2026, MIT, порт zstd 1.5.7, скорость ±13 % от нативного). Встроенный `System.IO.Compression.ZstandardStream` есть только в preview .NET 11 — для проекта на net8 «не подтверждено». [ZstdSharp README](https://github.com/oleg-st/ZstdSharp/blob/master/README.md), [Zstandard API review](https://api.apireview.net/catalog/d2fff18c3b8062c7216a325d112d4024)

### 4. Тест «save → load → save побайтово идентичен»

Что ломает идентичность и как чинить:

- `Dictionary<K,V>` / `HashSet<T>` — порядок вставки, после загрузки может отличаться. → `SortedDictionary`, либо DTO хранит `List<KeyValuePair>` и сериализатор сортирует по ключу.
- `DateTime.Now`, `Stopwatch`, uptime — не хранить; «время» = тик симуляции (`long tick`).
- `Guid.NewGuid()` — заменить на `int`/`long` ID из счётчика, счётчик сохраняется.
- `float`/`double` — STJ пишет инвариантно и round-trip, но `NaN`/`Infinity` бросают исключение (нужен `JsonNumberHandling.AllowNamedFloatingPointLiterals`); лучше запретить NaN в состоянии тестом.
- Порядок свойств — задать `[JsonPropertyOrder]` или полагаться на порядок объявления (документированное поведение STJ). [Customize properties](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/customize-properties)
- Сжатие: Brotli/zstd детерминированы при одинаковых входе, уровне и версии библиотеки; сравнивать всё равно лучше несжатый payload плюс отдельно сжатый.

```csharp
[Fact] public void SaveLoadSave_IsByteIdentical() {
    var world = WorldGen.Generate(seed: 42); Sim.Run(world, ticks: 10_000);
    byte[] a = SaveCodec.Write(world);
    var loaded = SaveCodec.Read(a);
    byte[] b = SaveCodec.Write(loaded);
    Assert.Equal(a, b);
}
```

Полнота сериализации: тест через рефлексию обходит все типы в `Sim.State.*`, для каждого поля/свойства требует либо участия в JSON-контракте (`JsonTypeInfo.Properties`), либо `[JsonIgnore]` + `[SaveExcluded("reason")]`. Второй страж — `StateHash`: детерминированный хеш (xxHash/SHA-256 поверх канонического JSON) до записи и после загрузки; расхождение = пропущенное поле или недетерминизм.

### 5. Атомарная запись, слоты, ротация, пути

- Запись: `slot1.save.tmp` в той же директории → `FileStream.Flush(true)` → `File.Move(tmp, "slot1.save", overwrite: true)`. Для бэкапа предыдущего: `File.Replace(tmp, dst, dst + ".bak")` (бросит, если тома разные). [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace)
- Слоты: `saves/<slot-name>/` с `manifest.json` (превью: дата, день, число жителей, версия) и `state.save`; автосейвы `saves/autosave/day-00123.save`, ротация — хранить N последних (N=5), удалять старшие после успешной записи нового.
- Пути: Core получает `string saveRoot` в конструкторе; Godot-слой передаёт `ProjectSettings.GlobalizePath("user://saves")`. Значения `user://`: macOS `~/Library/Application Support/Godot/app_userdata/<project>`, Windows `%APPDATA%\Godot\app_userdata\<project>`, Linux `~/.local/share/godot/app_userdata/<project>`; с `application/config/use_custom_user_dir=true` — без префикса `Godot/app_userdata`. [Godot data paths](https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html)

## Сравнение

| Критерий | STJ (source-gen) | MessagePack-CSharp 3.1.12 | MemoryPack 1.21.4 | protobuf-net 3.4.30 | BinaryWriter |
|---|---|---|---|---|---|
| net8/net10 | в составе .NET | netstandard2.0, opt. net8+ | netstandard2.1/net7/net8 | net8/netstandard2.0 | да |
| AOT / source-gen | да | да (MessagePackAnalyzer) | только source-gen | да (BuildTools, с 3.3) | н/д |
| Добавить поле | бесплатно | бесплатно (новый Key) | только в конец (или VersionTolerant) | новый номер | вручную |
| Удалить поле | Skip/Disallow | не переиспользовать Key | только VersionTolerant | не переиспользовать номер | вручную |
| Миграция по дереву | `JsonNode` | нет (только DTO) | нет | нет | нет |
| Детерминизм байт | порядок объявления, инвариантные числа; Dictionary сортировать | массив по Key детерминирован; map — сортировать | детерминирован, но зависит от layout/endian | map — сортировать | полный контроль |
| Размер (отн.) | 1,0 (сжатый ~0,3) | ~0,4–0,6 | ~0,3–0,5 | ~0,4–0,6 | ~0,3 |
| Читаемость/дифф | да | нет (есть JSON-дамп) | нет | нет | нет |
| Лицензия | MIT | MIT | MIT | Apache-2.0 | — |

Цифры размеров — оценочные (не бенчмарк этого проекта); при цели ≤ 20 МБ любой вариант укладывается.

## Рекомендация для проекта

1. **v1: STJ + source generator, контейнер `header\n` (несжатый JSON, 1 строка) + Brotli-payload.** Канонические опции: `WriteIndented=false`, `DefaultIgnoreCondition=Never` (всегда писать все поля — стабильность диффа важнее байтов), `UnmappedMemberHandling=Disallow` в тестах и `Skip` в релизе, `NumberHandling=Strict`, сортированные словари. Dev-флаг `--save-pretty` пишет с отступами для чтения.
2. **Слой DTO отдельно от состояния симуляции** (`SaveDto` ↔ `World`), чтобы менять внутреннее представление без формата. DTO только для текущей версии; старые версии проходят через `JsonNode`-миграции.
3. **Тайлы:** `seed` + список чанков `{index, mask: base64, tiles: base64}`; полный снапшот — отдельный `codec` в заголовке на случай смены генератора.
4. **Тесты:** round-trip побайтово; фикстуры всех прошлых версий; рефлексивный страж полноты; хеш состояния. Бенчмарк 1024²/50 жителей в CI с порогом 2 с и 20 МБ.
5. **Если позже упрёмся в скорость:** переключить payload на MessagePack-CSharp (≥ 3.1.7) с `[Key(int)]`, сохранив тот же DTO-слой; миграции тогда — через промежуточный JSON-дамп (MessagePack умеет `ConvertToJson`).
6. **Сжатие:** Brotli quality 5–6 (баланс); zstd через ZstdSharp только при измеренной необходимости. .NET 11 `ZstandardStream` — следить, не закладываться.

## Источники

- STJ source generation — https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
- JsonUnmappedMemberHandling (.NET 8+) — https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonunmappedmemberhandling
- What's new .NET 10 libraries (Strict, AllowDuplicateProperties) — https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries
- Порядок свойств STJ — https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/customize-properties
- Float formatting .NET Core 3.0 — https://devblogs.microsoft.com/dotnet/floating-point-parsing-and-formatting-improvements-in-net-core-3-0/
- MessagePack-CSharp README / nuget 3.1.12 — https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/master/README.md , https://www.nuget.org/packages/MessagePack/3.1.12
- MessagePack CVE-2026-48109 (fixed 3.1.7) — https://www.tenable.com/plugins/container-security/443098 ; CVE-2026-48502 — https://www.sentinelone.com/vulnerability-database/cve-2026-48502/
- MemoryPack README / nuget 1.21.4 — https://github.com/Cysharp/MemoryPack/blob/main/README.md , https://www.nuget.org/packages/MemoryPack/1.21.4
- protobuf-net README / nuget 3.4.30 — https://github.com/protobuf-net/protobuf-net/blob/main/README.md , https://www.nuget.org/packages/protobuf-net/3.4.30
- ZstdSharp README / nuget 0.8.8 — https://github.com/oleg-st/ZstdSharp/blob/master/README.md , https://www.nuget.org/packages/ZstdSharp.Port/0.8.8
- Zstandard в .NET (issue, preview .NET 11) — https://github.com/dotnet/runtime/issues/59591
- BrotliCompressionOptions (.NET 9+) — https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotlicompressionoptions
- File.Replace — https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace
- Godot data paths — https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html
- Factorio migrations — https://lua-api.factorio.com/latest/auxiliary/migrations.html
- DataFixerUpper — https://github.com/Mojang/DataFixerUpper/blob/master/README.md ; Minecraft Data version — https://minecraft.wiki/w/Data_version
- RimWorld Save file — https://rimworldwiki.com/wiki/Save_file ; BackCompatibility (stack trace) — https://steamcommunity.com/workshop/filedetails/discussion/1321849735/3110267314139419788
