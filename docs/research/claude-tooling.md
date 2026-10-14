# Инструменты для агента: MCP-серверы и плагины Claude Code под Godot 4 + C#

Дата проверки: 14 октября 2026. Звёзды, даты последнего push и число открытых issue/PR взяты из GitHub API в этот день; остальное — из README и официальных docs (ссылки в конце). Локальная среда: Claude Code 2.1.299, установлены плагины `context7`, `playwright`, `feature-dev`, `pr-review-toolkit`, `swift-lsp`; `godot`, `dotnet`, `brew`, `gh` в PATH отсутствуют; `project.godot` ещё не создан.

## Краткие выводы

1. **Документация решается двумя официальными плагинами**: `context7@claude-plugins-official` (уже стоит, требует авторизации) — индекс `/godotengine/godot-docs` на 5,26 млн токенов, обновлён 13.10.2026; `microsoft-docs@claude-plugins-official` (Microsoft Learn MCP, GA с 07.11.2025, бесплатно, без ключа) — единственный нормальный источник по .NET API: в Context7 официальных .NET docs нет. У Godot `llms.txt` нет (404; запрос godot-docs #10549 закрыт как «not planned»). [1][2][3][4][5]
2. **Для C# code intelligence есть официальный плагин `csharp-lsp`** (csharp-ls, Roslyn). Реально работает с csharp-ls ≥ 0.24 (сейчас 0.28.0 от 13.09.2026, требует .NET 10 SDK; README плагина устарел — пишет .NET 6+). Нужен `DOTNET_ROOT` в окружении, иначе сервер молча не стартует. Альтернативы (roslyn-language-server, OmniSharp) — только если csharp-ls не поднимется. [6][7][8][9]
3. **Godot MCP-серверов много, зрелых — ни одного.** Самый популярный `Coding-Solo/godot-mcp` (6 072★) — тонкая обёртка над CLI без поддержки C# (issues #101, #114 открыты), последний push 16.04.2026, 73 открытых issue/PR. Единственный, кто явно поддерживает C# и Godot 4.7 с проверкой на CI, — `beckettlab/beckett-godot-mcp` (44★, push 07.10.2026, `build_csharp`, `game_logs`, `screenshot`), но проект молодой. `IvanMurzak/Godot-MCP` (285★, push 13.10.2026) — C#-аддон, но по умолчанию ходит в облако ai-game.dev и тянет NuGet-пакеты в csproj игры. [10][11][12]
4. **На старте MCP для Godot не нужен**: сборка (`dotnet build`), запуск (`Godot --headless --path . --log-file`), чтение логов (`user://logs/godot.log`) делаются из Bash. Хук PostToolUse с `dotnet build` даёт обратную связь по ошибкам компиляции после каждой правки. MCP для редактора имеет смысл добавить, когда появятся сцены и нужна их правка/скриншоты. [13][14][15]
5. Гейм-дев-плагинов для Godot в официальном маркетплейсе нет (есть `unity` от Unity и `unreal-engine-skills-for-claude-code`; из 315 плагинов ни одного с «godot» в имени/описании). [16]

## Кандидаты

| Инструмент | Что даёт | Автор | Поддержка (push / ★ / open issues+PR) | Риски | Вердикт |
|---|---|---|---|---|---|
| `context7` plugin (official) | Актуальные docs Godot (`/godotengine/godot-docs`, `/websites/godotengine_en_4_4`, `/godotengine/godot-cpp`), в т. ч. C# API | Upstash | 14.10.2026 / 63 024 / 35 | Удалённый сервер; нужен API-ключ (OAuth) | **ставить сейчас** (уже стоит, авторизовать) |
| `microsoft-docs` plugin (official) | `microsoft_docs_search`, `microsoft_docs_fetch`, `microsoft_code_sample_search` по learn.microsoft.com (.NET API, C#) | Microsoft | 10.09.2026 / 1 900 / — (репо MicrosoftDocs/mcp) | Удалённый, без auth; содержимое docs = внешний текст | **ставить сейчас** |
| `csharp-lsp` plugin (official) + `csharp-ls` | Диагностика после каждой правки `.cs`, go-to-definition, references | Anthropic / razzmatazz | csharp-ls 13.09.2026 / 1 005 / 74 | Требует .NET 10 SDK и `DOTNET_ROOT`; индексирует solution (память) | **ставить сейчас** (после .NET SDK) |
| PostToolUse hook `dotnet build` | Ошибки компиляции сразу в контекст агента | свой скрипт | — | Сборка 5–20 с на каждую правку; нужно фильтровать по `.cs` | **ставить сейчас** |
| `beckettlab/beckett-godot-mcp` (Lite, MIT) | MCP внутри редактора (GDScript-плагин, HTTP 127.0.0.1:8770): `play_scene`, `game_logs` со stack trace, `screenshot`, `build_csharp`, правка сцен через undo/redo, `validate_script` для `.cs` | beckettlab | 07.10.2026 / 44 / 0 | 43 коммита, один вендор, Full-версия платная ($15); код агента выполняется с правами пользователя | **потом** — когда будут сцены; первый кандидат |
| `IvanMurzak/Godot-MCP` (Apache-2.0) | C#-аддон: 42 инструмента, `runtime-errors-get`, скриншоты, `.cs`/`.gd` скрипты, рефлексия C#-методов | IvanMurzak | 13.10.2026 (v0.26.1) / 285 / 6 | Cloud-режим по умолчанию (ai-game.dev), токены в plaintext, NuGet `McpPlugin`+`ReflectorNet` в csproj игры; Godot 4.3–4.5 заявлены, 4.7 не подтверждён | **потом / с осторожностью** (только Custom-режим) |
| `Coding-Solo/godot-mcp` (MIT) | `run_project` + `get_debug_output`, `launch_editor`, создание сцен/нод через `godot_operations.gd`, UID-инструменты (4.4+); без плагина в редакторе | Coding-Solo | 16.04.2026 / 6 072 / 73 | C# не поддержан (#31, #101, #114); 2 автоотчёта о нарушении MCP-спеки (#135, #136); запускает Godot-бинарь с произвольным проектом | **нет** — всё то же делает Bash |
| `mkdevkit/godot-mcp` (MIT) | 173 инструмента, `get_output_log`, `get_editor_errors`, скриншоты игры/редактора; WebSocket 6505 | mkdevkit | 09.06.2026 / 15 / 3 | `execute_editor_script`/`execute_game_script` = произвольный код; C# не упомянут | **нет** |
| `AkiraZ1/godot-mcp` (MIT) | `godot_build` (dotnet build), `godot_check_boot` (headless), GUT-тесты; тестирован на 4.7 | AkiraZ1 | 21.09.2026 / 0 / 0 | 0 звёзд, нет захвата вывода игры, правки мимо undo/redo | **нет** (следить) |
| `signalcompose/godot-mcp` | Плагин Claude Code (`/plugin install godot-mcp@signalcompose`), 90+ инструментов, Godot 4.6+ | signalcompose | 17.06.2026 / 0 / 9 | Source-available лицензия, не OSS; C# нет | **нет** |
| `ee0pdt/Godot-MCP`, `bradypp/godot-mcp` | Правка сцен/скриптов (ee0pdt — с аддоном), `get_debug_output` (bradypp) | — | 19.03.2025 / 619 / 25; 31.05.2025 / 90 / 6 | Не обновлялись ≥ 16 месяцев | **нет** |
| `nuskey8/godot-docs-mcp` | Поиск по docs.godotengine.org | nuskey8 | 02.10.2025 (4 коммита) / 25 / — | Дублирует Context7, заброшен | **нет** |
| `MarcelRoozekrans/roslyn-codelens-mcp` (MIT) | 67 Roslyn-инструментов: call graph, диагностика с анализаторами, code fixes, DI-граф | MarcelRoozekrans | 13.10.2026 / 61 / 6 | Анализаторы грузятся in-process; .NET 10 | **потом** — если LSP + hook не хватит |
| `burhancetinkaya/csharp-roslyn-lsp`, `boostvolt/claude-code-lsps` (omnisharp) | Альтернативные LSP (roslyn-language-server / OmniSharp) | — | 28.03.2026 / 2 / 0; 06.10.2026 / 185 / 0 | Roslyn LS: семантические запросы «частично работают, могут зависать» (Claude Code #16360) | **нет** — запасной вариант |

Страницы, которые открыть не удалось: GitHub API для `Raunaksplanet/godot-mcp-server` (лимит запросов), `claude.com/marketplace/plugins/unity` и каталог `plugins/unity` в репо (404 — плагин лежит в `external_plugins`).

## Подробности по рекомендуемым

### Context7 (официальный плагин, уже установлен)
- Статус локально: `claude mcp list` → `plugin:context7:context7 ... ! Needs authentication`. Авторизовать: `/mcp` → context7 → login (OAuth, бесплатный ключ даёт больший лимит). Альтернатива без плагина: `claude mcp add --transport http context7 https://mcp.context7.com/mcp --header "Authorization: Bearer <KEY>"`. [1][17]
- Инструменты: `resolve-library-id`, `query-docs`. Библиотеки для проекта: `/godotengine/godot-docs` (stable, 44 765 сниппетов, 13.10.2026), `/websites/godotengine_en_4_4`, `/godotengine/godot` (исходники). В CLAUDE.md стоит зафиксировать: «для API Godot использовать `/godotengine/godot-docs`, для .NET — microsoft-docs». [2]

### Microsoft Learn MCP (официальный плагин Microsoft в маркетплейсе Anthropic)
- `claude plugin install microsoft-docs@claude-plugins-official` (26 765 установок; плагин добавляет MCP-сервер и 3 скилла). Без плагина: `claude mcp add --transport http microsoft-docs https://learn.microsoft.com/api/mcp`. [3][5][18]
- Streamable HTTP, auth не нужен, GA с 07.11.2025, индекс обновляется ежедневно. Microsoft просит не считать endpoint стабильным API: список инструментов читать через `tools/list`. [3][4]

### csharp-lsp (официальный плагин) + csharp-ls
1. .NET 10 SDK в `~/.dotnet` (см. `godot-dotnet.md`), затем `dotnet tool install --global csharp-ls` (0.28.0; с 0.21 требует .NET 10). `brew install csharp-ls` на этой машине недоступен (нет brew). [6][7]
2. В `~/.zshrc`: `export DOTNET_ROOT=$HOME/.dotnet; export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools` — без `DOTNET_ROOT` csharp-ls падает молча (issue melodic-software #3536). [8]
3. `claude plugin install csharp-lsp@claude-plugins-official` (в сессии: `/plugin install csharp-lsp@claude-plugins-official`). Отдельная переменная `ENABLE_LSP_TOOL` в текущих docs не упоминается — LSP-плагины стартуют при первой правке `.cs`. [9]
4. Проверка: попросить Claude внести ошибку типа в `.cs` — должна появиться строка `Found N new diagnostic issues`; иначе `/plugin` → Errors. Запускать `claude` из корня с `.sln`/`.csproj`. [9]
5. Не подтверждено: как csharp-ls переживает source-генераторы `Godot.NET.Sdk` (нужен `dotnet restore` до старта). Проверить на первом проекте.

### Хук PostToolUse: `dotnet build` после правки `.cs`
`.claude/settings.json` (timeout по умолчанию 600 с; формат из официального reference) [13]:
```json
{ "hooks": { "PostToolUse": [ { "matcher": "Edit|Write",
  "hooks": [ { "type": "command", "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/dotnet-build.sh", "args": [], "timeout": 120 } ] } ] } }
```
`.claude/hooks/dotnet-build.sh` (`chmod +x`, нужен `jq`):
```bash
#!/bin/bash
input=$(cat); file=$(jq -r '.tool_input.file_path // empty' <<<"$input")
[[ "$file" == *.cs ]] || exit 0
cd "$CLAUDE_PROJECT_DIR" || exit 0
if ! out=$(dotnet build --no-restore -nologo -v q 2>&1 | grep -E 'error|warning CS' | head -40); then exit 0; fi
if grep -q ' error ' <<<"$out"; then jq -n --arg r "dotnet build failed after editing $file:
$out" '{decision:"block",reason:$r}'; fi
exit 0
```
`decision: "block"` не отменяет правку, но возвращает ошибки агенту как feedback. Если сборка на каждую правку слишком долгая — перенести тот же скрипт в событие `Stop` или положиться на диагностику LSP (она без компиляции) и собирать вручную. [13]

### beckett-godot-mcp (когда появятся сцены)
- Установка: скопировать `addons/beckett/` в проект (или AssetLib → «Beckett»), включить в Project → Plugins; плагин сам поднимает сервер на `http://127.0.0.1:8770/mcp` и пишет `.mcp.json`; далее `claude` → `/mcp` → `beckett` connected. Проверено автором на 4.4.1/4.6.3/4.7.2 (CI). [10]
- Безопасность (SECURITY.md): loopback-only, проверка Host/Origin, токен в `res://.beckett/token`, запись только в `res://`/`user://`, `BECKETT_READONLY=1`, `BECKETT_ALLOWLIST`, `BECKETT_CONFIRM_DESTRUCTIVE=1`. Но код, который агент записывает в скрипты, и `build_csharp` (`dotnet build`) выполняются с правами пользователя — песочницы нет. Проект-токен и `.beckett/` не коммитить. [19]
- Перед установкой перечитать репозиторий: 44★ и 43 коммита — это уровень «ранний бета-инструмент», не стандарт.

## Логи и ошибки из игры без MCP

- Бинарь: `~/Applications/Godot_mono.app/Contents/MacOS/Godot`. Сборка C# — просто `dotnet build` в папке проекта (редактор делает то же самое); `--build-solutions` подразумевает `--editor`, в CI не нужен. Импорт ресурсов: `Godot --headless --path . --import`. [14][15]
- Запуск с логом: `Godot --headless --path . --log-file /tmp/run.log --quit-after 600 res://scenes/Smoke.tscn` (`--log-file` отключает ротацию; `-d` включает stdout-отладчик; `--verbose` добавляет `print_verbose`). Для MVP без headless окно открывается, лог всё равно пишется. `--headless` ломает только рендер/скриншоты, логика и `GD.Print` работают. [14][15]
- Без `--log-file` лог лежит в `user://logs/godot.log` = `~/Library/Application Support/Godot/app_userdata/<Project Name>/logs/godot.log` (хранится 5 файлов, `debug/file_logging/max_log_files`). `GD.PushError`/`push_error` идут в stderr, который сбрасывается на каждый вызов; stdout в release-экспорте сбрасывается только при выходе (`application/run/flush_stdout_on_print=true` чинит). Крэш-логи пишутся в тот же файл. [15][20]
- Практика для агента: `Godot ... 2>&1 | tee /tmp/run.log`, затем `grep -nE 'ERROR|SCRIPT ERROR|Unhandled|at .*\.cs:' /tmp/run.log`. Это покрывает 100 % того, что `get_debug_output` в Coding-Solo и `game_logs` в Beckett возвращают текстом; скриншоты и remote tree без MCP недоступны.

## Рекомендуемый минимальный набор

1. `context7@claude-plugins-official` — авторизовать (уже установлен).
2. `claude plugin install microsoft-docs@claude-plugins-official`.
3. .NET 10 SDK → `dotnet tool install --global csharp-ls` → `claude plugin install csharp-lsp@claude-plugins-official`.
4. Хук PostToolUse `dotnet build` (скрипт выше) в `.claude/settings.json` проекта.
5. В `CLAUDE.md`: команды запуска/логов из раздела выше и правило «API Godot — context7, .NET — microsoft-docs».
6. Отложено до появления сцен: `beckett-godot-mcp` Lite; альтернатива — `IvanMurzak/Godot-MCP` в Custom-режиме.

## Что не ставить и почему

- `Coding-Solo/godot-mcp` — популярность не равна пользе: для C#-проекта он не умеет привязывать скрипты, `run_project` эквивалентен одной Bash-команде, а `npx @coding-solo/godot-mcp` каждый раз тянет пакет из npm; 73 открытых issue/PR при 60 коммитах. [11]
- `mkdevkit`, `signalcompose`, `AkiraZ1`, `slangwald`, `LeeSinLiang` — 0–15 звёзд, без C#, в двух из них есть выполнение произвольного GDScript из MCP.
- `ee0pdt`, `bradypp`, `nuskey8/godot-docs-mcp` — не обновлялись с 2025 года.
- Сторонние LSP-обёртки (roslyn-language-server, OmniSharp, NuGet `CSharpLspAdapter`/`ClaudeCodeRoslynLspProxy`) — держать в запасе; основная проблема (Claude Code не реализует часть LSP-запросов, #16360) у них та же. [8][21]
- Roslyn-MCP (`roslyn-codelens-mcp` и др.) — дублируют LSP + `dotnet build`; анализаторы выполняются in-process. Вернуться, если понадобится call graph по большому solution.
- `playwright` (уже стоит) к Godot C# неприменим: web-экспорт для C# не поддержан.

## Источники

1. https://github.com/upstash/context7 — README, remote endpoint, инструменты; GitHub API: 63 024★, push 14.10.2026
2. https://context7.com/api/v1/search?query=godot — список индексов Godot (godot-docs: 5 263 698 токенов, 13.10.2026); запрос `dotnet docs` — официальных .NET docs нет
3. https://learn.microsoft.com/en-us/training/support/mcp — обзор Learn MCP: endpoint, без auth, бесплатно
4. https://learn.microsoft.com/en-us/training/support/mcp-release-notes — GA 07.11.2025, плагин для клиентов 23.03.2026
5. https://github.com/MicrosoftDocs/mcp — установка `microsoft-docs@claude-plugins-official`, 1,9k★, последний коммит 10.09.2026
6. https://github.com/razzmatazz/csharp-language-server/releases — 0.28.0 (13.09.2026), требование .NET 10
7. https://github.com/anthropics/claude-plugins-official/blob/main/plugins/csharp-lsp/README.md — `dotnet tool install --global csharp-ls`, «.NET SDK 6.0+»
8. https://github.com/melodic-software/claude-code-plugins/issues/3536 — csharp-ls ≥ 0.24 работает, `DOTNET_ROOT`, сравнение с Roslyn LS
9. https://code.claude.com/docs/en/plugins/code-intelligence — таблица LSP-плагинов, проверка диагностики
10. https://github.com/beckettlab/beckett-godot-mcp — README: инструменты, C#, CI на 4.4.1/4.6.3/4.7.2; API: 44★, push 07.10.2026
11. https://github.com/Coding-Solo/godot-mcp и https://github.com/Coding-Solo/godot-mcp/issues?q=C%23 — API: 6 072★, push 16.04.2026, 73 open; issues #31/#101/#114/#135/#136
12. https://github.com/IvanMurzak/Godot-MCP и /releases — v0.26.1 (13.10.2026), cloud-режим, NuGet в csproj; API: 285★
13. https://code.claude.com/docs/en/hooks — формат PostToolUse, stdin JSON, exit codes, `decision/reason`
14. https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html — `--headless`, `--log-file`, `--quit-after`, `--import`, `--build-solutions`, `-d`
15. https://docs.godotengine.org/en/stable/tutorials/scripting/logging.html — `user://logs/godot.log`, ротация, flush stdout/stderr, крэш-логи
16. https://raw.githubusercontent.com/anthropics/claude-plugins-official/main/.claude-plugin/marketplace.json — 315 плагинов; `unity`, `unreal-engine-skills-for-claude-code`, `csharp-lsp`, `context7`, `microsoft-docs`; «godot» — 0
17. https://code.claude.com/docs/en/mcp — `claude mcp add`, `--transport http`, `--scope`, `.mcp.json`, `MAX_MCP_OUTPUT_TOKENS`
18. https://claude.com/marketplace/plugins/microsoft-docs — 26 765 установок; https://claude.com/marketplace/plugins/csharp-lsp — 43 741
19. https://github.com/beckettlab/beckett-godot-mcp/blob/main/SECURITY.md — что защищено, гейты
20. https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html — `user://` на macOS
21. https://github.com/burhancetinkaya/csharp-roslyn-lsp/blob/main/README.md — ограничения Roslyn LS в Claude Code (#16360); https://github.com/boostvolt/claude-code-lsps — omnisharp (185★, push 06.10.2026)
22. Прочие кандидаты (GitHub API 14.10.2026): mkdevkit/godot-mcp 15★ 09.06.2026; AkiraZ1/godot-mcp 0★ 21.09.2026; signalcompose/godot-mcp 0★ 17.06.2026; ee0pdt/Godot-MCP 619★ 19.03.2025; bradypp/godot-mcp 90★ 31.05.2025; MarcelRoozekrans/roslyn-codelens-mcp 61★ 13.10.2026; nuskey8/godot-docs-mcp 25★, коммиты 02.10.2025
