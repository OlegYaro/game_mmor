# CLAUDE.md

Игра на Godot 4.7.2 (.NET) + чистый C# Core. Решения: `docs/adr/`, требования: `docs/GDD.md`, `docs/requirements-index.md`. Журнал: `docs/PROGRESS.md`. План качества и критерии релиза: `docs/quality-plan.md`.

**Источники истины с 15.10.2026: `docs/MVP.md` и `docs/ROADMAP-v1-v2.md`.** При расхождении с GDD, gdd-issues, gdd-change-proposals, requirements-index (и ADR) прав новый файл. Пометки: [решено] и [GDD] обязательны; [предложение] можно закладывать, но записывать в `gdd-change-proposals.md` как открытое; [открыто] не решать самому, собирать в `docs/open-questions.md`. `docs/design-overview.md` — архив.

## Архитектурные правила
- `src/Core` не знает про Godot (проверяет архитектурный тест). Весь геймплей живёт в Core, `src/Game` только показывает снимки состояния и передаёт ввод.
- Детерминированный тик 10 Гц. Никакого `DateTime.Now`, `System.Random`, `float`/`double` в логике — только целые и fixed-point (см. `BannedSymbols.txt`, ADR 0005).
- Контент в `data/*.json` (схемы в `data/schema`). Числа баланса не пишем в код, только в данные.
- Сохранения версионируются, у каждой версии есть миграция (ADR 0007).
- Код и комментарии на английском. README, CLAUDE.md, PROGRESS и ADR на русском.

## Команды (всё через `tools/`)
`tools/build.sh`, `tools/test.sh` (`COVERAGE=1` для покрытия), `tools/run.sh`, `tools/smoke.sh`, `tools/export.sh`, `tools/sync-data.sh`, `tools/schemagen.sh` (схемы в `data/schema`), `tools/artgen.sh` (процедурный арт), `tools/bench-render.sh`, `tools/setup.sh` (первичная установка `.tools/`, только bash). Для остальных есть одноимённые `.ps1` под Windows.

## Definition of Done
Сборка без предупреждений, все тесты зелёные, новая логика покрыта тестами (Core не ниже 70% строк), `tools/smoke.sh` проходит, ADR и PROGRESS обновлены.

## Структура
`src/Core` логика, `src/Game` Godot (рядом с `project.godot`), `tests/Core.Tests`, `data/` контент и схемы, `locale/` строки, `docs/` GDD, ADR, исследования, `tools/` скрипты, `.tools/` локальный тулчейн (не в git).

## Локальный тулчейн
Всё лежит в `.tools/` (dotnet, NuGet-кэш, Godot, csharp-ls, логи). Ничего не ставить глобально, не трогать `~/.zprofile` и пользовательские настройки Claude Code. Скрипты сами подключают окружение через `tools/env.sh`. Godot запускается с `HOME=.tools/godot-home`, чтобы `user://` оставался внутри проекта.

## Подводные камни
Godot при открытии редактора переписывает `TargetFramework` в `src/Game/Game.csproj` на net8.0, если значение не литерал. Там должен быть литерал `net10.0`; `Directory.Build.targets` роняет сборку, если он расходится с `Directory.Build.props`.
Экспорт только через `tools/export.sh`: он сначала синхронизирует `data/` в `src/Game/data/` (в git не попадает).

## Процесс
Работаем по фазам. После каждой фазы остановиться и ждать «ок» от владельца. Коммиты по Conventional Commits с ID требований, например `feat(sim): генерация по seed (FR-WORLD-01)`. Репозиторий: GitHub `OlegYaro/game_mmor`, ветка `main`; `git push` только по просьбе владельца.

## Параллельная работа агентов
- Параллельные агенты работают в отдельных `git worktree` (`.claude/worktrees/`, в git не попадает). В новом worktree сначала `ln -s /Users/krip/Gemini/Games/1/.tools .tools`.
- Один файл правит один агент: общие места (`Simulation.cs`, `Save*`, `GameRoot`) — только в одной волне. Слияние веток и пересоздание эталонов (`UPDATE_GOLDEN=1`) делает основной поток; после слияния `contentHash` в `tests/Core.Tests/Golden/*.json` всегда пересоздаётся.
- Хук `PostToolUse` собирает проект после правки `.cs` (`tools/hook-build.sh`); пока идут тесты, он пропускается.
- Исследования и рутинные правки отдавать Sonnet (effort medium), сложный код с детерминизмом и сохранениями — Opus.

## Покрытие кода и долгие процессы
- Полный замер покрытия (`COVERAGE=1`) — только в конце этапа (milestone), не после каждой волны. Настройки coverlet лежат в `tests/Core.Tests/coverage.runsettings` (только сборка Core, SingleHit), полный замер занимает около 30 секунд.
- После волны достаточно замерить покрытие только тестов изменённых модулей (`--filter`).
- Не блокируйся на ожидании фоновых процессов: если что-то идёт дольше 5 минут, продолжай работу (сборка и тесты в Release, чтобы не трогать bin/Debug) и проверь результат потом.
- Перед запуском тестов убедись, что не висят старые процессы Core.Tests (`ps aux | grep -E "Core.Tests|vstest|datacollector"`).
- Не запускай два прогона тестов одновременно в одной конфигурации.
