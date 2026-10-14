# Игра (рабочее название)

Колониальный симулятор на Godot 4.7.2 (.NET) с чистым C#-ядром. Документы: `docs/GDD.md`, решения: `docs/adr/`, ход работы: `docs/PROGRESS.md`, правила для ассистента: `CLAUDE.md`.

## Установка с нуля

Пока поддерживается только macOS arm64. Всё ставится в папку проекта `.tools/`, глобально ничего не меняется.

```sh
tools/setup.sh
```

Скрипт идемпотентный. Он скачивает:
- Godot 4.7.2 mono (macOS universal) и шаблоны экспорта `.tpz` с github.com/godotengine/godot/releases, проверяя SHA512 по `SHA512-SUMS.txt`;
- .NET SDK через `dotnet-install.sh --channel 10.0`;
- `csharp-ls` через `dotnet tool install --tool-path .tools/bin`.

Также создаётся `Godot_mono.app/Contents/MacOS/._sc_`, чтобы редактор хранил данные рядом с приложением.

## Команды

| Команда | Что делает |
| --- | --- |
| `tools/build.sh` | сборка решения |
| `tools/test.sh` | тесты Core (`COVERAGE=1` включает покрытие) |
| `tools/run.sh` | синхронизация данных и запуск игры |
| `tools/smoke.sh` | headless smoke-тест с лимитом 60 с |
| `tools/export.sh` | экспорт под macOS и Windows в `out/` |
| `tools/sync-data.sh` | копирует `data/*.json` в `src/Game/data/` |

На Windows те же команды в `tools/*.ps1` (не проверены, пока нет Windows-машины). Требуется положить Godot в `.tools/godot` или указать `GODOT_BIN`.

Редактор Godot: `HOME=$PWD/.tools/godot-home .tools/godot/Godot_mono.app/Contents/MacOS/Godot --path src/Game -e`.
