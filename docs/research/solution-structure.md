# Структура решения: Core (чистый C#) + Game (Godot 4) + Core.Tests

Дата проверки: 14.10.2026. Актуальный стабильный Godot — 4.7.2 (18.08.2026), `Godot.NET.Sdk` 4.7.2; 4.8 в dev. Всё, что не удалось подтвердить документацией или исходниками, помечено «не подтверждено».

## Краткие выводы

1. `Game.csproj` обязан лежать рядом с `project.godot` (комментарий в `GodotSharpDirs.cs`: «The csproj should be in the same folder as project.godot»). `.sln` можно вынести в корень репозитория через настройку проекта `dotnet/project/solution_directory` (поддержка `..` и поиск `*.sln`/`*.slnx` в указанной папке — PR #106031, влит 30.10.2025, milestone 4.6; код присутствует в ветке 4.7). В 4.3–4.5 экспорт ломался на такой раскладке (issue #98804).
2. Godot 4.7 генерирует `Game.csproj` с `TargetFramework=net8.0` (`net9.0` только для Android) и `EnableDynamicLoading=true`; `Nullable` не выставляется. В master (4.8) генератор уже пишет `net10.0`. Официальная документация: «Godot 4.5 requires .NET 8 or later». .NET 10 в 4.7 работает по отзывам, но официально не подтверждён.
3. .NET 8 (LTS) и .NET 9 (STS) теряют поддержку 10.11.2026; .NET 10 — LTS до 14.11.2028. xUnit v3 поддерживает .NET 8+. Совместимый выбор на сегодня: `net8.0` везде (официальный дефолт Godot 4.7), с TFM в одном месте (`Directory.Build.props`), чтобы перейти на `net10.0` одной правкой после выхода Godot 4.8 или после собственной проверки на 4.7.
4. `ProjectReference` из Game на Core работает «как есть»: экспорт Godot делает `dotnet publish <Game.csproj> -c ExportRelease -r <rid> --self-contained true -o <tmp>` и кладёт **все** файлы publish-папки (включая `Core.dll`) в `data_<App>_<platform>_<arch>` (на macOS внутри `Contents/Resources`). `Private=false` ставить нельзя — DLL не попадёт в выход.
5. Core собирается на `Microsoft.NET.Sdk`; `GodotSharp` подключается неявно только `Godot.NET.Sdk` (Sdk.targets), поэтому в Core его нет. Страховка — архитектурный тест (простая проверка `GetReferencedAssemblies()` без зависимостей либо NetArchTest.eNhancedEdition 1.4.5 / ArchUnitNET 0.13.4).
6. Анализаторы: `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel`, `EnforceCodeStyleInBuild` — стандартные свойства MSBuild, в Core и тестах включаем без оговорок. В Game `TreatWarningsAsErrors` допустим, но генератор Godot выдаёт свои GD0xxx-диагностики, а gdUnit4 требует `NoWarn NU1605`; включать осознанно.
7. Данные `data/*.json`: в экспорте `res://` живёт внутри `.pck`, обычный `System.IO` его не видит (docs: `System.IO.Path` «can only be used with native OS paths»). JSON попадает в pck через фильтр `include_filter="*.json"` в `export_presets.cfg`. Core должен получать содержимое файлов через абстракцию, а не открывать пути сам.

## Подробности

### 1. Раскладка и .sln

```
/                     ← Game.sln, Directory.Build.props, .editorconfig, global.json
  src/Core/Core.csproj           (Microsoft.NET.Sdk, class library, без Godot)
  src/Game/project.godot         + Game.csproj (Godot.NET.Sdk), data/*.json, export_presets.cfg
  tests/Core.Tests/Core.Tests.csproj (xunit v3)
```

Настройки в `project.godot` (раздел `[dotnet]`, видны в Project Settings → Advanced):

```ini
[dotnet]
project/assembly_name="Game"
project/solution_directory="../.."
```

Как это разрешается в 4.7 (`GodotSharpDirs.cs`): пустое значение → `res://`; иначе добавляется префикс `res://`, путь глобализуется и проходит `Path.GetFullPath` («to load solutions from parent directory when appropriate»), затем нерекурсивно ищутся `*.sln`/`*.slnx`, содержащие проект с именем сборки `assembly_name`; при нескольких совпадениях — ошибка, при нуле — запасной путь `<solutionDir>/<assemblyName>.sln`. Отсюда правило: имя `assembly_name` должно совпадать с именем проекта в `.sln`, и в корневой папке не должно быть двух .sln с этим проектом.

Генерация: редактор создаёт `.csproj` и `.sln` при первом C# скрипте, только если их нет (ProjectGenerator.cs). Порядок, который не оставляет редактору повода что-то сгенерировать: создать `Game.csproj` и корневой `.sln` руками (`dotnet new sln`, `dotnet sln add`), затем открыть проект в Godot. Проверка после первого открытия: в `src/Game` не появился лишний `Game.sln` (не подтверждено документацией, только логикой кода).

Что генерирует Godot 4.7 (ветка 4.7, `ProjectGenerator.cs`):

```xml
<Project Sdk="Godot.NET.Sdk/4.7.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <TargetFramework Condition=" '$(GodotTargetPlatform)' == 'android' ">net9.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
</Project>
```

`EnableDynamicLoading` (docs Microsoft): генерирует `runtimeconfig.json`, ставит `RollForward=LatestMinor`, копирует NuGet-зависимости локально. Нужно только Game; в Core не добавлять.

Что `Godot.NET.Sdk` навязывает (Sdk.props/Sdk.targets, master): `Configurations=Debug;ExportDebug;ExportRelease`, `BaseOutputPath`/`OutputPath` безусловно в `.godot/mono/temp/bin/`, `AppendTargetFrameworkToOutputPath=false`, `EnableDefaultNoneItems=false`, `DefineConstants` (`GODOT`, `TOOLS`, платформенные) — «set here so users can't override it». Следствия для `Directory.Build.props` в корне:
- не задавать там `OutputPath`, `BaseOutputPath`, `Configurations`, `DefineConstants` — для Game они либо перезапишутся, либо сломают редактор;
- условия вида `'$(Configuration)' == 'Release'` для Game никогда не сработают (конфигурации `ExportRelease`);
- всё остальное (`Nullable`, `LangVersion`, `TreatWarningsAsErrors`, `AnalysisLevel`) наследуется штатно: редактор вызывает обычный `dotnet build <csproj> -c Debug -p:GodotTargetPlatform=...` (BuildSystem.cs), MSBuild подхватывает props из родительских папок. Отдельных issue «Godot + Directory.Build.props» не найдено — не подтверждено, что проблем нет, но механизм стандартный.

Нюанс dev-машины: при `net8.0` Godot 4.7 хостит проект на рантайме .NET 8 (`RollForward=LatestMinor` не поднимает major), поэтому рядом с SDK 10 нужен установленный runtime 8. С `net10.0` отзывы положительные (обсуждение godot-proposals #13076, январь–март 2026), но официального подтверждения для 4.7 нет; генератор 4.8 (master) уже ставит `net10.0`.

### 2. ProjectReference Game → Core

```xml
<!-- src/Game/Game.csproj -->
<Project Sdk="Godot.NET.Sdk/4.7.2">
  <PropertyGroup>
    <TargetFramework>$(GameTargetFramework)</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Core/Core.csproj" />
  </ItemGroup>
</Project>
```

- TFM Core ≤ TFM Game (обычное правило MSBuild); проще держать одно значение в `Directory.Build.props` (`<GameTargetFramework>net8.0</GameTargetFramework>`).
- Экспорт (ExportPlugin.cs, BuildSystem.cs, 4.7): `dotnet publish Game.csproj -c ExportRelease|ExportDebug -r <os>-<arch> --self-contained true -o <temp> -p:GodotTargetPlatform=<platform>`; затем `RecursePublishContents` добавляет каждый файл publish-папки в `data_<App>_<platform>_<arch>` (macOS: `Contents/Resources/data_...`). Поэтому `Core.dll` и его NuGet-зависимости попадают в сборку автоматически; метаданные `Private` (Copy Local) трогать не нужно — `Private=false` исключит DLL из выхода.
- Экспорт из CLI требует, чтобы `dotnet` был в `PATH` (форум: иначе сборка без `data_`-папки).
- Как убедиться, что Core не тянет GodotSharp: (а) `Microsoft.NET.Sdk` + отсутствие `PackageReference Include="GodotSharp"`; (б) `dotnet list src/Core/Core.csproj package --include-transitive`; (в) тест ниже.

### 3. Анализаторы и архитектурная проверка

```xml
<!-- Directory.Build.props (корень) -->
<Project>
  <PropertyGroup>
    <GameTargetFramework>net8.0</GameTargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors Condition="'$(MSBuildProjectName)' != 'Game'">true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

Docs Microsoft: `AnalysisLevel` — «specify a set of code analyzers to run according to a .NET release»; `EnforceCodeStyleInBuild=true` включает проверку правил `.editorconfig` (IDExxxx) при сборке; `CodeAnalysisTreatWarningsAsErrors` — отдельный рубильник для CAxxxx. В Game предупреждения-как-ошибки включать после проверки: GD0xxx-диагностики генератора Godot (список в docs `c_sharp/diagnostics`) и NU1605 при gdUnit4.

Архитектурный тест без внешних библиотек (достаточно для одной сборки):

```csharp
[Fact]
public void Core_DoesNotReferenceGodot() =>
    Assert.DoesNotContain(typeof(Core.Marker).Assembly.GetReferencedAssemblies(),
        a => a.Name!.StartsWith("Godot", StringComparison.Ordinal));
```

Если нужны правила по неймспейсам: NetArchTest.eNhancedEdition 1.4.5 (04.06.2025, netstandard2.0; API `Types.InAssembly(...).ShouldNot().HaveDependencyOnAny("Godot")`) — оригинальный NetArchTest.Rules не обновлялся с 2021; ArchUnitNET 0.13.4 (20.08.2026) с пакетом `TngTech.ArchUnitNET.xUnitV3` для xUnit v3.

### 7. Данные `data/*.json`

- В редакторе `res://` — обычная папка, в экспорте — виртуальная ФС внутри `.pck`; `System.IO.Path`/`File` работают только с нативными путями (docs `c_sharp_differences`). `ProjectSettings.GlobalizePath("res://...")` в экспорте бесполезен.
- Включение не-ресурсов в экспорт: диалог экспорта → «Filters to export non-resource files/folders» (docs `exporting_projects`: «allows non-resource files such as .txt, .json and .csv to be exported»); в `export_presets.cfg` это ключ `include_filter` каждого `[preset.N]` (editor_export.cpp). Перечисление каталога внутри pck для не-ресурсов не работает (ask.godotengine #71057) — нужен явный список файлов или `index.json`.
- Данные должны лежать внутри проекта Godot (`src/Game/data/`), иначе экспорт их не увидит. Тесты подключают ту же папку ссылкой (`LinkBase` — документированная метадата .NET SDK):

```xml
<!-- tests/Core.Tests/Core.Tests.csproj -->
<ItemGroup>
  <Content Include="../../src/Game/data/**" LinkBase="data" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Второй вариант — копировать `data/` рядом с бинарником в CI и читать через `OS.GetExecutablePath()` (docs `data_paths`); экспорт этого не делает сам, на macOS нужно класть в `.app/Contents/Resources`. Для одиночной игры он сложнее и не нужен.

## Рекомендация для проекта

- Раскладка как в разделе 1; `.sln` в корне, `dotnet/project/solution_directory="../.."`, `assembly_name="Game"`. Минимальная версия Godot — 4.6 (из-за PR #106031), использовать 4.7.2.
- TFM: `net8.0` для Core/Game/Tests через одно свойство в `Directory.Build.props`; на dev-машине поставить runtime .NET 8 и SDK 10. Переход на `net10.0` — отдельная задача: либо после Godot 4.8, либо после собственной проверки (смена одной строки + `dotnet test` + экспорт).
- Core: `Microsoft.NET.Sdk`, `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`; тест на отсутствие ссылок на `Godot*`.
- Game: `ProjectReference` без `Private`, `Nullable=enable`, `TreatWarningsAsErrors` пока выключен.
- Данные: `src/Game/data/*.json`, `include_filter="*.json"` в каждом пресете, явный `index.json`; Game читает через `Godot.FileAccess.GetFileAsString("res://data/...")` и отдаёт строки в Core через интерфейс (`IDataReader`), тесты читают файлы напрямую через `System.IO` из скопированной папки.

## Источники

- C# basics (docs 4.7, требования .NET, генерация .csproj/.sln): https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html
- `GodotSharpDirs.cs` (ветка 4.7, solution_directory, csproj рядом с project.godot): https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/GodotTools/GodotTools/Internals/GodotSharpDirs.cs
- PR #106031 «Fix loading correct solution file for projects in subdirectories»: https://github.com/godotengine/godot/pull/106031 ; issue #98804: https://github.com/godotengine/godot/issues/98804
- `ProjectGenerator.cs` 4.7 (net8.0, EnableDynamicLoading): https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectGenerator.cs ; master (net10.0): https://github.com/godotengine/godot/blob/master/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectGenerator.cs
- Sdk.props / Sdk.targets Godot.NET.Sdk: https://github.com/godotengine/godot/tree/master/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk
- `ExportPlugin.cs`, `BuildManager.cs`, `BuildSystem.cs` (ветка 4.7, publish --self-contained, data_-папка): https://github.com/godotengine/godot/tree/4.7/modules/mono/editor/GodotTools/GodotTools
- Godot.NET.Sdk на NuGet (4.7.2, 4.8.0-dev.7): https://www.nuget.org/packages/Godot.NET.Sdk ; архив 4.7.2: https://godotengine.org/download/archive/4.7.2-stable/
- Обсуждение .NET 10 в Godot: https://github.com/godotengine/godot-proposals/discussions/13076
- Политика поддержки .NET: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- MSBuild-свойства .NET SDK (EnableDynamicLoading, AnalysisLevel, EnforceCodeStyleInBuild, LinkBase): https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props
- ProjectReference/Private: https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items
- Форум: csproj рядом с project.godot, .sln переносится: https://forum.godotengine.org/t/c-how-can-i-choose-different-place-for-main-csproj-sln-rather-then-root-directory/130818
- NetArchTest.eNhancedEdition: https://www.nuget.org/packages/NetArchTest.eNhancedEdition ; ArchUnitNET: https://github.com/TNG/ArchUnitNET , https://www.nuget.org/packages/TngTech.ArchUnitNET.xUnit
- Экспорт, фильтр не-ресурсов: https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html ; ключи export_presets.cfg: https://github.com/godotengine/godot/blob/4.7/editor/export/editor_export.cpp
- C# differences (System.IO.Path только для нативных путей): https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_differences.html ; data paths: https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html
- Листинг каталога в pck не работает: https://ask.godotengine.org/71057/cannot-iterate-non-resource-files-in-the-directory-the-build
