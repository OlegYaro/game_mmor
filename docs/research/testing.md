# Тестирование: Core (xUnit), Godot-слой (gdUnit4 / GoDotTest), headless smoke, CI, покрытие

Дата проверки: 14.10.2026, Godot 4.7.2. Непроверенное помечено «не подтверждено».

## Краткие выводы

1. Core тестируется обычным xUnit v3 (`dotnet test`, без Godot). xUnit v3 требует .NET 8+, тестовый проект — `OutputType=Exe`. Для совместимости с coverlet нужен режим VSTest (`xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`), а не MTP: `coverlet.msbuild` «does not work with the Microsoft Testing Platform (MTP v2)».
2. Для Godot-слоя рабочий вариант 2026 года — gdUnit4Net: `gdUnit4.api` 5.0.0 (stable) / 5.1.0-rc5–rc6, `gdUnit4.test.adapter` 3.1.1 (22.06.2026, требует api ≥ 5.1.0-rc5), `gdUnit4.analyzers` 1.0.0; таблица совместимости в README — Godot 4.6…4.7.2; TFM net8.0/net9.0. С v5 тесты по умолчанию идут **без** рантайма Godot, Godot нужен только для `[RequireGodotRuntime]`. Запуск: `dotnet test --settings .runsettings`, переменная `GODOT_BIN` обязательна (путь к бинарнику, на macOS `.../Godot.app/Contents/MacOS/Godot`).
3. Ограничения gdUnit4Net: `.runsettings` не подхватывается автоматически — передавать `--settings` или `RunSettingsFilePath` в csproj (discussion #350, docs issue #456); параллельный запуск не поддерживается; собственный CI gdUnit4Net гоняет тесты под `xvfb-run` на ubuntu-22.04 с `retries: 3` «because Godot mono randomly crashes during C# tests». В `--headless` не работает доставка ввода через `Input.parse_input_event` (issue #73557, открыт) — UI-тесты с вводом требуют виртуального дисплея или `Viewport.PushInput`.
4. Альтернатива — Chickensoft GoDotTest 2.0.47 (09.09.2026, GodotSharp ≥ 4.7.2): тесты живут в самой игре, запуск `godot --run-tests --quit-on-finish`; в CI Chickensoft используют `xvfb-run` + Mesa/Vulkan на Ubuntu, не `--headless`. Удобно для сценных тестов, но смешивает тесты и игру.
5. Smoke-тест: `timeout 60 godot --headless --path src/Game --quit-after 300 --log-file ./smoke.log`. Необработанное C# исключение в колбэке Godot **не завершает процесс**: `ExceptionUtils.LogUnhandledException` печатает `GD.PushError("Unhandled exception\n" + e)` и возвращает управление, код выхода остаётся 0. Поэтому либо грепать лог на `Unhandled exception`/`ERROR:`, либо в игре перехватывать и вызывать `GetTree().Quit(1)` (`SceneTree.quit(exit_code)`, 0..125). События `GD.UnhandledException` в GD.cs ветки 4.7 нет.
6. CI: `chickensoft-games/setup-godot@v2` (ubuntu/macos/windows; `version`, `use-dotnet`, `include-templates`, `cache`; добавляет `GODOT`/`GODOT4` в окружение). Экспорт из CLI: `godot --headless --export-release <preset> <path>` с .NET-шаблонами `mono_export_templates.tpz`. `firebelley/godot-export@v8.0.0` — только ubuntu, свои URL на Linux-редактор и шаблоны; `abarichello/godot-ci` — Docker-образы, mono-теги для 4.7 не проверены.
7. Покрытие: `coverlet.collector` 10.1.0 (27.09.2026) **не умеет порог**; порог даёт `coverlet.msbuild` 10.1.0 (`/p:Threshold=70 /p:ThresholdType=line /p:ThresholdStat=total`) или ReportGenerator (`minimumCoverageThresholds:lineCoverage=70`, «will exit unsuccessfully»), GitHub Action `danielpalme/ReportGenerator-GitHub-Action@5.5.11` через `customSettings`.

## Подробности

### 4. Тесты Godot-слоя

gdUnit4Net — пример проекта из официальной документации (тесты лежат в проекте Godot, csproj на `Godot.NET.Sdk`):

```xml
<Project Sdk="Godot.NET.Sdk/4.7.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <NoWarn>NU1605</NoWarn>
    <RunSettingsFilePath>$(MSBuildProjectDirectory)/.runsettings</RunSettingsFilePath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
    <PackageReference Include="gdUnit4.api" Version="5.1.0-rc6" />
    <PackageReference Include="gdUnit4.test.adapter" Version="3.1.1" />
    <PackageReference Include="gdUnit4.analyzers" Version="1.0.0" />
  </ItemGroup>
</Project>
```

`.runsettings` (поля из README адаптера; `--headless` в `Parameters` — рекомендация мейнтейнера в discussion #350):

```xml
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>1</MaxCpuCount>
    <TestSessionTimeout>180000</TestSessionTimeout>
    <TreatNoTestsAsError>true</TreatNoTestsAsError>
    <EnvironmentVariables><GODOT_BIN>/Applications/Godot_mono.app/Contents/MacOS/Godot</GODOT_BIN></EnvironmentVariables>
  </RunConfiguration>
  <GdUnit4>
    <Parameters>--headless --verbose</Parameters>
    <DisplayName>FullyQualifiedName</DisplayName>
    <CaptureStdOut>true</CaptureStdOut>
    <CompileProcessTimeout>20000</CompileProcessTimeout>
    <GodotConnectTimeout>60000</GodotConnectTimeout>
  </GdUnit4>
</RunSettings>
```

Тест, которому нужен движок:

```csharp
[TestSuite]
public class HudTests
{
    [TestCase, RequireGodotRuntime, GodotExceptionMonitor]
    public void Hud_Instantiates() { /* GD.Load<PackedScene>("res://scenes/Hud.tscn").Instantiate() ... */ }
}
```

Где держать тесты Godot-слоя. Документация gdUnit4 показывает тесты внутри проекта игры; отдельный `tests/Game.Tests.csproj` с `ProjectReference` на Game в принципе собирается (`GodotSharp.TestAdapter` описывает такую схему), но скрипты из внешней сборки нельзя вешать на узлы в редакторе (PR #106031), и для gdUnit4 такая раскладка не подтверждена. Рабочий компромисс: папка `src/Game/tests/`, пакеты gdUnit4 и `Microsoft.NET.Test.Sdk` подключены условно (`Condition="'$(Configuration)' == 'Debug'"`), а файлы исключены из экспорта через `DefaultItemExcludes` для `ExportRelease`/`ExportDebug` (приём из README GoDotTest). Условные PackageReference с gdUnit4 — не подтверждено, проверить сборкой `-c ExportRelease`.

GoDotTest: `TestEnvironment.From(OS.GetCmdlineArgs())` в главной сцене, флаги `--run-tests[=Suite.Method] --quit-on-finish --stop-on-error --coverage`; покрытие через `coverlet.console --target $GODOT --targetargs "--run-tests --coverage --quit-on-finish"` (скрипт `coverage.sh` в шаблоне GodotGame). Готовый action `godot-gdunit-labs/gdUnit4-action@v1` (`godot-net: true`, `dotnet-version` net7/net8, Ubuntu) оборачивает тесты в xvfb — на раннерах без GPU медленно (#350).

### 5. Headless smoke-тест и CI

Флаги (docs command line): `--headless` = `--display-driver headless --audio-driver Dummy`; `--path <dir>` (папка с project.godot); `--quit` — после первой итерации; `--quit-after N` — после N итераций (0 = выкл.); `--log-file <path>` — «write output/error log to the specified path»; `--verbose`; `--quiet` (ошибки всё равно печатаются); `--build-solutions` (только редактор, «Implies --editor»); `--export-release <preset> <path>` (относительные пути — от папки project.godot). Неизвестные аргументы игнорируются молча.

Логи: по умолчанию `user://logs/godot.log` (macOS: `~/Library/Application Support/Godot/app_userdata/<name>/logs`), настройки `debug/file_logging/enable_file_logging`, `log_path`, `max_log_files` (5). stdout сбрасывается на каждый `print` только в debug-сборках; в release — `application/run/flush_stdout_on_print`; stderr (`push_error`) сбрасывается всегда.

Исключения C#: в 4.7 (`ExceptionUtils.cs`) `LogException` → `GD.PushError(e.ToString())` (или в отладчик), `LogUnhandledException` → `GD.PushError("Unhandled exception\n" + e)`; завершения процесса в этом коде нет, политика «unhandled exception policy» из Godot 3 в 4.x не найдена. Код выхода при `--quit-after` будет 0 — не подтверждено экспериментом, следует из кода. Поэтому smoke-скрипт:

```bash
timeout 60 "$GODOT" --headless --path src/Game --quit-after 300 --log-file "$PWD/smoke.log"; code=$?
[ "$code" -eq 0 ] || { echo "godot exit $code"; exit 1; }
! grep -E "Unhandled exception|ERROR:|SCRIPT ERROR" smoke.log
```

Плюс в `Main._Ready` для CI-режима (аргумент `--smoke` через `OS.GetCmdlineUserArgs()`): подписка на собственный логгер ошибок и `GetTree().Quit(1)`.

CI-workflow (GitHub Actions; `setup-godot@v2` поддерживает macos-latest, т.е. arm64 — конкретный бинарник на arm64 не подтверждён, Godot на macOS — universal):

```yaml
jobs:
  core:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet test tests/Core.Tests --collect:"XPlat Code Coverage" --results-directory TestResults
      - uses: danielpalme/ReportGenerator-GitHub-Action@5.5.11
        with:
          reports: TestResults/**/coverage.cobertura.xml
          targetdir: coveragereport
          reporttypes: MarkdownSummaryGithub;Cobertura
          customSettings: minimumCoverageThresholds:lineCoverage=70
  godot:
    strategy: { matrix: { os: [ubuntu-latest, macos-latest] } }
    runs-on: ${{ matrix.os }}
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: chickensoft-games/setup-godot@v2
        with: { version: 4.7.2, use-dotnet: true, include-templates: true }
      - run: godot --headless --path src/Game --build-solutions --quit-after 200 || true
      - run: dotnet build -c Debug
      - run: dotnet test src/Game --no-build --settings src/Game/.runsettings
        env: { GODOT_BIN: ${{ env.GODOT }} }   # имя переменной от setup-godot — проверить в логах раннера
      - run: ./scripts/smoke.sh
      - run: godot --headless --path src/Game --export-release "macOS" ../../build/Game.zip
```

Шаг `--build-solutions` в своём CI gdUnit4Net завершают `--quit-after 200`, Chickensoft — `--quit || exit 0`; оба допускают ненулевой код (не подтверждено, почему). Для экспорта на macos-runner `dotnet` должен быть в `PATH` (форум: иначе сборка без `data_`-папки). Retry-стратегия для Godot-тестов (`retries`) оправдана опытом gdUnit4Net.

### 6. Покрытие Core

Вариант A (collector + порог в ReportGenerator, работает с `dotnet test` любой версии): показан в workflow выше; локально `dotnet tool install -g dotnet-reportgenerator-globaltool` и `reportgenerator -reports:... -targetdir:coveragereport -reporttypes:Html "minimumCoverageThresholds:lineCoverage=70"`.

Вариант B (порог внутри теста, только VSTest-режим):

```bash
dotnet test tests/Core.Tests /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura \
  /p:Threshold=70 /p:ThresholdType=line /p:ThresholdStat=total
```

`coverlet.msbuild` 10.1.0 и `coverlet.collector` 10.1.0 — на NuGet от 27.09.2026; VSTest-документация coverlet прямо говорит, что «threshold validation and built-in merge are not provided by this integration mode». В csproj тестов не включать `TestingPlatformDotnetTestSupport` (MTP), пока coverlet его не поддерживает. Исключения: `[ExcludeFromCodeCoverage]`, `ExcludeByFile` для сгенерированного кода.

## Рекомендация для проекта

- Core.Tests: xUnit v3 в режиме VSTest, `coverlet.collector`, порог 70 % строк через ReportGenerator в CI (job `core` на ubuntu, без Godot) — быстрый и обязательный gate для PR.
- Godot-слой: gdUnit4Net внутри `src/Game/tests/` (api 5.1.0-rc6 + adapter 3.1.1; при выходе стабильного 5.1 зафиксировать), `.runsettings` с `--headless`, `GODOT_BIN` в окружении; сценные тесты без ввода. Пока тестов Godot-слоя мало — только smoke.
- Smoke: `scripts/smoke.sh` из раздела 5, обязательный в CI на ubuntu; macos-runner — раз в сутки/по тегу (дорогой).
- Экспорт: `godot --headless --export-release` на том же `setup-godot` с `include-templates: true`; firebelley/godot-export и Docker-образы не нужны.
- Отдельно проверить вручную на macOS arm64 (не подтверждено): код выхода при исключении C# под `--quit-after`; условные PackageReference gdUnit4 при `-c ExportRelease`; имя переменной (`GODOT`/`GODOT4`), которую выставляет setup-godot.

## Источники

- gdUnit4Net README (версии, совместимость, `[RequireGodotRuntime]`): https://github.com/MikeSchulze/gdUnit4Net ; Test adapter README (`.runsettings`, `GODOT_BIN`, лимиты): https://github.com/MikeSchulze/gdUnit4Net/blob/master/TestAdapter/README.md
- Документация gdUnit4 C#: https://godot-gdunit-labs.github.io/gdUnit4/latest/csharp_project_setup/csharp-setup/ , https://godot-gdunit-labs.github.io/gdUnit4/latest/csharp_project_setup/vstest-adapter/
- NuGet: https://www.nuget.org/packages/gdUnit4.api , https://www.nuget.org/packages/gdUnit4.test.adapter
- Discussion #350 «Running in Linux CI» (headless, `--settings`, `RunSettingsFilePath`): https://github.com/godot-gdunit-labs/gdUnit4Net/discussions/350
- CI gdUnit4Net (`xvfb-run`, `GODOT_BIN`, retries): https://github.com/godot-gdunit-labs/gdUnit4Net/blob/master/.github/workflows/unit-tests.yml ; gdUnit4-action: https://github.com/godot-gdunit-labs/gdUnit4-action
- Issue #73557 (ввод в headless): https://github.com/godotengine/godot/issues/73557
- GoDotTest: https://github.com/chickensoft-games/GoDotTest , https://www.nuget.org/packages/Chickensoft.GoDotTest ; шаблон GodotGame (`visual_tests.yaml`, `coverage.sh`): https://github.com/chickensoft-games/GodotGame
- setup-godot (action.yml, inputs): https://github.com/chickensoft-games/setup-godot
- Command line tutorial: https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html ; Logging: https://docs.godotengine.org/en/stable/tutorials/scripting/logging.html ; `SceneTree.quit`: https://docs.godotengine.org/en/stable/classes/class_scenetree.html
- `ExceptionUtils.cs` 4.7 (исключения только логируются): https://github.com/godotengine/godot/blob/4.7/modules/mono/glue/GodotSharp/GodotSharp/Core/NativeInterop/ExceptionUtils.cs ; `GD.cs` 4.7: https://github.com/godotengine/godot/blob/4.7/modules/mono/glue/GodotSharp/GodotSharp/Core/GD.cs
- Экспорт из CLI без .NET SDK в PATH: https://forum.godotengine.org/t/problem-exporting-c-project-from-command-line-cant-find-net-sdk/113124
- firebelley/godot-export: https://github.com/firebelley/godot-export ; abarichello/godot-ci: https://github.com/abarichello/godot-ci
- xUnit v3 getting started: https://xunit.net/docs/getting-started/v3/getting-started
- coverlet VSTest (нет порога): https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/VSTestIntegration.md ; MSBuild (Threshold): https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/MSBuildIntegration.md ; NuGet: https://www.nuget.org/packages/coverlet.msbuild , https://www.nuget.org/packages/coverlet.collector
- ReportGenerator (minimumCoverageThresholds): https://github.com/danielpalme/ReportGenerator/wiki/Settings ; GitHub Action: https://github.com/danielpalme/ReportGenerator-GitHub-Action
- `.runsettings` / `RunSettingsFilePath`: https://learn.microsoft.com/en-us/visualstudio/test/configure-unit-tests-by-using-a-dot-runsettings-file
