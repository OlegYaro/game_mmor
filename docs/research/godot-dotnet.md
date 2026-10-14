# Godot 4.x + .NET: актуальные версии и ограничения C#

Дата проверки: 14 октября 2026. Все факты сверены с первоисточниками (ссылки в конце, номера в квадратных скобках).

## Краткие выводы

1. Последний стабильный Godot — **4.7.2** (18 августа 2026), ветка 4.7 вышла 18 июня 2026 («Lights, Camera, Action!» / «Director's Cut»). **4.8 ещё не стабильна**: последний снапшот 4.8 dev 7 (29 сентября 2026, feature freeze), стабильный релиз ожидается в Q4 2026. Утверждение GDD «есть 4.7/4.8» верно наполовину: 4.7 — да, 4.8 — нет. [1][2][3][4]
2. У Godot 4.x **нет LTS-ветки**. Политика: стабильная ветка поддерживается минимум до выхода следующей минорной и её первого патча; дальше best-effort. Сейчас «Full» — 4.7 и 4.6, «Partial» — 4.5. LTS-статус получает только предыдущая мажорная линия (3.6). [4]
3. Godot 4.7.x требует **.NET SDK 8 или новее** (минимальный TFM проекта — `net8.0`, для Android — `net9.0`). Godot **4.8 поднимает минимум до .NET 10 / `net10.0`** (PR #123738 слит 26 сентября 2026, milestone 4.8). [5][6][7][8]
4. Актуальный .NET LTS — **.NET 10** (вышел 11 ноября 2025, поддержка до 14 ноября 2028). Последний SDK — **10.0.402** (runtime 10.0.13, 13 октября 2026). **.NET 8 и .NET 9 заканчивают поддержку 10 ноября 2026**, то есть через 4 недели. [9][10]
5. `net10.0` в Godot 4.7.x работает: `GodotSharp` собран под `net8.0` и совместим с более новыми TFM; GodotTools только *поднимает* TFM до минимума, но не понижает. Официальной фразы «net10.0 поддержан» в документации 4.7 нет — это подтверждено по коду и NuGet, не по docs. [6][11]
6. Экспорт C# поддержан для **Windows, macOS, Linux**; Android и iOS — experimental; **web — не поддержан**. macOS-экспорт всегда даёт Universal 2 (arm64 + x86_64); .NET-экспорт — `dotnet publish --self-contained true` на каждый RID, то есть runtime .NET у игрока не нужен. [5][12][13][14]
7. Нужна отдельная сборка редактора **«.NET» (в именах файлов — `mono`)**: `Godot_v4.7.2-stable_mono_macos.universal.zip` + отдельные шаблоны `Godot_v4.7.2-stable_mono_export_templates.tpz`. Версии из Steam/App Store C# не поддерживают. NuGet `Godot.NET.Sdk` / `GodotSharp` 4.7.2 (18.08.2026). [15][16][17]
8. Установка на macOS без прав администратора: прямой zip с сайта (ничего не ставится, приложение нотаризовано) + .NET SDK через `dotnet-install.sh` в `~/.dotnet`. Homebrew cask `godot-mono` существует, но тянет cask `dotnet-sdk` (pkg-инсталлер) и сам Homebrew требует админа при первичной установке. [15][18][19]

## Подробности

### 1. Версии Godot

- Блог godotengine.org (первая страница на 14.10.2026): 4.8 dev 7 (29.09), 4.8 dev 6 (15.09), 4.8 dev 5 (10.09), 4.8 dev 4 (26.08), **4.7.2 maintenance (18.08)**, 4.7.1 maintenance (14.07), **4.7 stable (18.06.2026)**. [1][2][3]
- 4.7.2: 57 исправлений от 39 контрибьюторов, «no known incompatibilities with 4.7.1», известных проблем в посте нет. [2]
- 4.8 dev 7 объявлен как feature freeze, дата беты/стабильной не названа; таблица release policy даёт оценку Q4 2026. [3][4]
- Таблица поддержки (release_policy): 4.8 — development; 4.7 (июнь 2026) и 4.6 (январь 2026) — full; 4.5 (сентябрь 2025) — partial; 4.4 и ниже — EOL. Поддерживается только последний патч в минорной ветке. [4]
- Документация `docs.godotengine.org/en/stable` сейчас соответствует 4.7. [5]

### 2. .NET SDK и совместимость с .NET 10

- Docs 4.7 (c_sharp_basics): «Godot 4.5 requires .NET 8 or later, but exporting to Android requires .NET 9 or later»; рекомендуется последний стабильный 64-битный SDK. Текст не обновлён под 4.7, но требования те же. [5]
- Код ветки `4.7`: `GodotSharp.csproj` — `TargetFramework net8.0`, `LangVersion 12`; `ProjectGenerator.cs` — генерирует `net8.0` (`GodotMinimumRequiredTfm`), `net9.0` только при `GodotTargetPlatform == android`, `EnableDynamicLoading = true`. [6][7]
- `ProjectUtils.EnsureTargetFrameworkMatchesMinimumRequirement` повышает TFM до минимума, если он ниже; более высокий TFM (например `net10.0`) не трогает. Явной валидации net8/net9/net10 нет. [11]
- NuGet `GodotSharp 4.7.2`: фреймворк `net8.0`, «compatible with this framework or higher», без зависимостей. [17]
- Godot 4.8: PR #123738 «.NET: Upgrade packages and minimum TFM required to `net10.0`» слит 26.09.2026 (milestone 4.8, автор raulsntos). Мотив — EOL .NET 8 в ноябре 2026. Побочный эффект: VS 2022 не умеет .NET 10, нужен VS 2026 (Rider/VS Code не затронуты). Пост 4.8 dev 7 прямо пишет «requires .NET 10.0 or newer». [3][8]
- .NET: 10 — LTS, 11.11.2025 → 14.11.2028 (Active); 8 — LTS, EOL 10.11.2026; 9 — STS, EOL 10.11.2026; 11 — в стадии RC1 (go-live), GA ожидается в ноябре 2026. SDK 10.0.402 / runtime 10.0.13 от 13.10.2026, есть macOS Arm64 installer и tar.gz. [9][10]
- Версия языка C#: определяется TFM (net8.0 → C# 12, net9.0 → C# 13, net10.0 → C# 14). Использовать `LangVersion` выше, чем у TFM, Microsoft не поддерживает. Для проекта на `net10.0` доступен C# 14. [20]
- Форумные сообщения о сбоях «Failed to create C# project» с .NET 10 на Linux (Bazzite/Homebrew) и о «Godot по умолчанию .NET 9 на macOS» — единичные, без подтверждения разработчиков; на 4.7.x издержки сводятся к тому, что ProjectGenerator пишет `net8.0`, и TFM нужно поднять руками. [21][22]

### 3. Ограничения C# в Godot 4.7

- Платформы (docs, C# index): desktop (Windows, Linux, macOS) — полная поддержка с 4.2; Android — experimental; iOS — experimental, экспорт только с macOS, симулятор только x64; **web — не поддержан** («consider Godot 3 instead»). [12]
- Экспорт реализован в `GodotTools/Export/ExportPlugin.cs`: для каждой пары платформа/архитектура вызывается `dotnet publish <proj> -c ExportRelease -r <rid> --self-contained true -p:GodotTargetPlatform=<platform> -o <dir>`. RID: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`. Для macOS публикуются оба RID, lipo не применяется — нативные библиотеки .NET кладутся рядом по архитектурам. Опции пресета: `dotnet/include_scripts_content` (false), `dotnet/embed_build_outputs` (false, для macOS отключено), `dotnet/include_debug_symbols` (true). [13][14]
- macOS-экспорт: всегда Universal 2 (arm64 + x86_64). Для .NET-экспорта энтайтлменты JIT, Unsigned Executable Memory и DYLD Environment Variables включены принудительно и скрыты. Codesign-варианты: `Built-in (ad-hoc only)`, `Xcode codesign`, `rcodesign`; нотаризация — `Disabled` / `Xcode notarytool` / `rcodesign`. Без Developer ID: ad-hoc + нотаризация выключена; у пользователя Gatekeeper потребует «Open Anyway» или `xattr -dr com.apple.quarantine`. [12][23]
- NativeAOT / trimming: официально **не документированы и не рекомендованы**. Maintainer (raulsntos, 2024): trimming конфликтует с reflection в GodotSharp, обходной путь через `TrimmerRootAssembly` документировать отказались; цель — полная совместимость с NativeAOT «в будущем». Статья 2024 о платформах: desktop NativeAOT «работает» при `PublishAOT=true` + rooting GodotSharp, но это не поддерживаемый путь. В 4.7 ситуация не изменилась (в release notes 4.7 про C# ничего нет). [24][25][26]
- Новый биндинг «Godot .NET» (репо `godotengine/godot-dotnet`, .NET как GDExtension, флаг `EnableGodotDotNetPreview` в Sdk.targets) существует, но в 4.7 это preview; официального поста о его статусе не найдено — «не подтверждено». [27]
- Известные gotchas из docs: имя класса должно совпадать с именем файла; `Get/Set/Call/Connect` требуют snake_case-имён; hot-reload не сохраняет состояние кроме exported-полей; сборку нужно пересобирать, чтобы редактор увидел новые экспортируемые поля/сигналы. [5]
- NuGet: `Godot.NET.Sdk` 4.7.2 (18.08.2026), 4.7.1 (14.07), 4.7.0 (18.06), 4.6.3 (20.05). `GodotSharp` 4.7.2 — `net8.0`. [16][17]

### 4. Установка Godot .NET на macOS без admin

- Прямая загрузка: страница godotengine.org/download/macos → «.NET» → `Godot_v4.7.2-stable_mono_macos.universal.zip` (universal, arm64 + x86_64). Приложение подписано и нотаризовано (Prehensile Tales B.V.), «self-contained and does not require installation»; достаточно распаковать в `~/Applications`. Прямая ссылка GitHub Releases проверена (302 → asset). [15][28]
- Homebrew: `brew install --cask godot-mono` → `/Applications/Godot_mono.app`, версия 4.7.2, зависимость — cask `dotnet-sdk` 10.0.402 (pkg-инсталлер, требует пароль). Первичная установка самого Homebrew в `/opt/homebrew` требует админа; на этой машине `brew` не установлен. [18][19]
- .NET SDK без admin: `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0` (ставит в `~/.dotnet`), затем `export DOTNET_ROOT=$HOME/.dotnet; export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools` в `~/.zshrc`. Поддерживаемые macOS для .NET 10: 14, 15, 26. На Arm Mac x64-SDK ставится отдельно в `.../x64/dotnet/` — не смешивать. [19]
- Менеджеры версий: `gdvm` (adalinesimonian) — `curl -sSL https://gdvm.io/install.sh | bash`, ставит в `~/.gdvm`, поддерживает Apple Silicon, C#-сборка через префикс `csharp:` (`gdvm install csharp:4.7.2`, `gdvm pin csharp:latest`). Также `gdenv` (`gdenv install 4.7.2-stable_mono`), `gdman -f mono`. Необходимость sudo для gdvm в README не описана — «не подтверждено», но путь `~/.gdvm` указывает на пользовательскую установку. [29]
- Бинарник: `Godot_mono.app/Contents/MacOS/Godot`. Запуск из CLI: `Godot_mono.app/Contents/MacOS/Godot --headless --path <project> --import` (импорт ресурсов и выход), `--build-solutions` (сборка C#), `--export-release "<preset>" <out>` / `--export-debug` (путь вывода относителен `project.godot`, не cwd). `--headless` обязателен на CI и убирает окно при экспорте. [30]

### 5. Экспортные шаблоны и Windows-сборка с macOS

- Редакторские данные на macOS: `~/Library/Application Support/Godot/` (Windows `%APPDATA%\Godot\`, Linux `~/.local/share/godot/`). Папка шаблонов — константа `export_templates_folder = "export_templates"`, версия — `GODOT_VERSION_FULL_CONFIG` = `4.7.2.stable.mono` для .NET-сборки (подтверждено существованием `godotengine.org/mirrorlist/4.7.2.stable.mono.json`). Итого: `~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/`. [31][32][33][34]
- Скачивание для CLI: `.tpz` — это zip; скачать `Godot_v4.7.2-stable_mono_export_templates.tpz` с GitHub Releases или `downloads.godotengine.org/?version=4.7.2&flavor=stable&slug=mono_export_templates.tpz&platform=templates`, распаковать и переложить содержимое папки `templates/` в указанную директорию версии. Через GUI: Editor > Manage Export Templates > Install from file. [15][35]
- Windows-экспорт с macOS: поддержан; нужны шаблоны `windows_release_x86_64.exe` и т. п. из того же tpz. Архитектуры: x86_64 (по умолчанию), x86_32, arm64 (не запускается на x86_64-Windows). Подпись опциональна (включается в пресете; вне Windows нужен `osslsigncode`). Иконка из PNG/SVG конвертируется в ICO автоматически с 4.1; `rcedit` нужен только для метаданных/иконки в exe и требует Wine на macOS — для MVP не нужен. Для .NET-части Godot сам вызывает `dotnet publish -r win-x64 --self-contained true` — кросс-компиляция из macOS штатная. [13][36][37]

### 6. Известные проблемы Godot .NET на macOS arm64

- **#124317 (открыт, 09.10.2026, 4.7.2, macOS 26.6.2, M4 Pro)**: встроенный codesign пишет не-минимальный DER в энтайтлментах; при трёх и более энтайтлментах macOS 26 убивает приложение при запуске (SIGKILL/137). Исправление в PR #124386 «pending review». .NET-экспорт всегда включает три энтайтлмента (JIT, Unsigned Executable Memory, DYLD), поэтому с большой вероятностью затронут — это вывод по описанию, в issue .NET не упомянут. Обход: пересобрать подпись `codesign` из Xcode CLT с теми же энтайтлментами. [38]
- **#115970 (закрыт 03.03.2026)**: «Mac Arm: Can't build a project in C#» на 4.6 / macOS 26.2 / .NET 10.0.102 — «Unable to find package Godot.NET.Sdk». Причина — сеть/IPv6 к nuget.org; обход `DOTNET_SYSTEM_NET_DISABLEIPV6=1`. Не баг Godot. [39]
- **#111416 (закрыт)**: случайный SIGABRT при headless-экспорте на macOS 26 / M1 Max в 4.5 — GDScript-проект, Web-экспорт, к .NET не относится; исправлено к 4.6. [40]
- Открытые issue с метками `topic:dotnet` + `platform:macos` — 10 штук, все 2020–2023 годов (в основном 3.x/4.2-dev), ни одного свежего high-priority. [41]

## Рекомендация для проекта

- Движок: **Godot 4.7.2 .NET** (`Godot_v4.7.2-stable_mono_macos.universal.zip`), шаблоны той же версии. На 4.8 переходить после стабильного релиза и первого патча (4.8.1), не раньше.
- .NET: ставить **SDK 10.0.4xx** через `dotnet-install.sh` в `~/.dotnet`; в csproj игры и Core сразу `<TargetFramework>net10.0</TargetFramework>` (C# 14). Не привязываться к .NET 8/9 — EOL 10.11.2026, а 4.8 всё равно потребует net10.0. Библиотека Core — `net10.0` без ссылки на `GodotSharp`; тесты на xUnit/NUnit под тем же TFM.
- Установка без admin: zip в `~/Applications` + `~/.dotnet`; `DOTNET_ROOT` обязательно в профиле, иначе редактор не найдёт SDK. Homebrew и gdvm — опционально.
- CI/CLI: `Godot --headless --path . --import`, затем `--export-release "Windows Desktop" build/win/Game.exe` и `--export-release "macOS" build/mac/Game.zip`. Шаблоны класть в `~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/`.
- macOS-экспорт: ad-hoc подпись, нотаризация выключена для MVP; проверять запуск на macOS 26 из-за #124317 — при SIGKILL пересобрать подпись `codesign`. Windows-экспорт: без подписи и без rcedit.
- Не использовать NativeAOT/trimming; не планировать web-экспорт на C#.

## Источники

1. https://godotengine.org/blog/ — список постов, даты релизов 4.7 / 4.7.x / 4.8 dev
2. https://godotengine.org/article/maintenance-release-godot-4-7-2/ — 4.7.2, 18.08.2026
3. https://godotengine.org/article/dev-snapshot-godot-4-8-dev-7/ — 4.8 dev 7, feature freeze, «requires .NET 10.0 or newer»
4. https://docs.godotengine.org/en/stable/about/release_policy.html — таблица поддержки, отсутствие LTS в 4.x
5. https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html — требования .NET 8+, gotchas
6. https://github.com/godotengine/godot/blob/4.7/modules/mono/glue/GodotSharp/GodotSharp/GodotSharp.csproj — net8.0, LangVersion 12
7. https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectGenerator.cs — генерируемый TFM
8. https://github.com/godotengine/godot/pull/123738 — минимум net10.0 в 4.8
9. https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core — LTS/STS, даты EOL
10. https://dotnet.microsoft.com/en-us/download/dotnet/10.0 — SDK 10.0.402 / runtime 10.0.13
11. https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectUtils.cs — логика минимального TFM
12. https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html — C# platform support
13. https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/GodotTools/GodotTools/Export/ExportPlugin.cs — RID, опции экспорта
14. https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/GodotTools/GodotTools/Build/BuildSystem.cs — `dotnet publish --self-contained true`
15. https://godotengine.org/download/macos/ — .NET-сборка, universal, tpz, нотаризация
16. https://www.nuget.org/packages/Godot.NET.Sdk — версии SDK
17. https://www.nuget.org/packages/GodotSharp/4.7.2 — net8.0, compatible or higher
18. https://formulae.brew.sh/cask/godot-mono — cask 4.7.2, зависимость dotnet-sdk
19. https://learn.microsoft.com/en-us/dotnet/core/install/macos — dotnet-install.sh, ~/.dotnet, DOTNET_ROOT, поддерживаемые macOS
20. https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/configure-language-version — версия C# по TFM
21. https://forum.godotengine.org/t/failed-to-create-c-project-on-linux-bazzite-and-net-10/140980 — неподтверждённый сбой
22. https://forum.godotengine.org/t/net10-and-c-14/133412 — .NET 10 с 4.6 (форум)
23. https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_macos.html — Universal 2, codesign, энтайтлменты .NET
24. https://github.com/godotengine/godot-docs/issues/9239 — позиция maintainer по NativeAOT
25. https://godotengine.org/article/platform-state-in-csharp-for-godot-4-2/ — NativeAOT/Android/iOS (2024)
26. https://godotengine.org/releases/4.7/ — release notes 4.7 (C# не упомянут)
27. https://github.com/godotengine/godot/blob/4.7/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/Sdk.targets — EnableGodotDotNetPreview
28. https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_macos.universal.zip — проверено HEAD-запросом
29. https://github.com/adalinesimonian/gdvm — gdvm, csharp:
30. https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html — путь бинарника, флаги CLI
31. https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html — editor data paths
32. https://github.com/godotengine/godot/blob/4.7/editor/file_system/editor_paths.h — `export_templates_folder = "export_templates"`
33. https://github.com/godotengine/godot/blob/4.7/editor/export/export_template_manager.cpp — папка версии = GODOT_VERSION_FULL_CONFIG
34. https://godotengine.org/mirrorlist/4.7.2.stable.mono.json — строка версии mono-сборки
35. https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html — установка tpz, CLI-экспорт
36. https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_windows.html — osslsigncode, архитектуры
37. https://docs.godotengine.org/en/stable/tutorials/export/changing_application_icon_for_windows.html — rcedit, ICO из PNG
38. https://github.com/godotengine/godot/issues/124317 — codesign DER, macOS 26
39. https://github.com/godotengine/godot/issues/115970 — Godot.NET.Sdk not found (IPv6)
40. https://github.com/godotengine/godot/issues/111416 — SIGABRT headless export
41. https://github.com/godotengine/godot/issues?q=is%3Aissue%20state%3Aopen%20label%3Atopic%3Adotnet%20label%3Aplatform%3Amacos — открытые issue
