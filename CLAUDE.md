# CLAUDE.md

Игра на Godot 4.7.2 (.NET) + чистый C# Core. Решения: `docs/adr/`, требования: `docs/GDD.md`, `docs/requirements-index.md`. Журнал: `docs/PROGRESS.md`.

**Источники истины с 15.10.2026: `docs/MVP.md` и `docs/ROADMAP-v1-v2.md`.** При расхождении с GDD, gdd-issues, gdd-change-proposals, requirements-index (и ADR) прав новый файл. Пометки: [решено] и [GDD] обязательны; [предложение] можно закладывать, но записывать в `gdd-change-proposals.md` как открытое; [открыто] не решать самому, собирать в `docs/open-questions.md`. `docs/design-overview.md` — архив.

## Архитектурные правила
- `src/Core` не знает про Godot (проверяет архитектурный тест). Весь геймплей живёт в Core, `src/Game` только показывает снимки состояния и передаёт ввод.
- Детерминированный тик 10 Гц. Никакого `DateTime.Now`, `System.Random`, `float`/`double` в логике — только целые и fixed-point (см. `BannedSymbols.txt`, ADR 0005).
- Контент в `data/*.json` (схемы в `data/schema`). Числа баланса не пишем в код, только в данные.
- Сохранения версионируются, у каждой версии есть миграция (ADR 0007).
- Код и комментарии на английском. README, CLAUDE.md, PROGRESS и ADR на русском.

## Команды (всё через `tools/`)
`tools/build.sh`, `tools/test.sh` (`COVERAGE=1` для покрытия), `tools/run.sh`, `tools/smoke.sh`, `tools/export.sh`, `tools/sync-data.sh`, `tools/setup.sh` (первичная установка `.tools/`). Для Windows есть одноимённые `.ps1`.

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
