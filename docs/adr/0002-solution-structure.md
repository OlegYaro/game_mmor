# 0002. Структура решения

Статус: принято
Дата: 2026-10-14
Требования: NFR-04, NFR-06, NFR-09, FR-MOD-01; §17 GDD (Core без Godot)

## Контекст

Вся логика должна жить в чистой библиотеке `Core` без ссылок на Godot. Её тесты запускаются через `dotnet test` без редактора. Godot-проект `Game` только рисует и шлёт команды. Контент лежит в `data/` с JSON Schema. Подробности и источники: `docs/research/solution-structure.md`.

Ключевые факты:
- `Game.csproj` обязан лежать рядом с `project.godot`.
- Файл решения можно вынести в корень репозитория настройкой `dotnet/project/solution_directory`. Это работает с Godot 4.6, в 4.3–4.5 экспорт ломался.
- При экспорте Godot выполняет `dotnet publish` для `Game.csproj` и забирает все DLL, включая `Core.dll`. Обычный `ProjectReference` работает без настроек.
- В экспортированной игре `res://` упакован в `.pck`, и `System.IO` его не видит. JSON попадает в `.pck` через фильтр экспорта `*.json`.

## Варианты

| Вариант | Плюсы | Минусы |
| --- | --- | --- |
| Файл решения в корне, `project.godot` и `Game.csproj` в `src/Game`, Core и тесты рядом | Чистое разделение; `dotnet test` из корня; совпадает со структурой из задания | Нужна настройка `solution_directory`; работает только с Godot 4.6+ |
| Проект Godot в корне репозитория, Core и тесты в подпапках | Godot «из коробки», без настройки | Godot импортирует всё содержимое репозитория, включая тесты и docs; смешение слоёв |
| Core подключается как готовая DLL или NuGet-пакет | Жёсткая граница | Лишний шаг сборки на каждую правку; хуже отладка |
| Core подключается обычным `ProjectReference` | Работает при экспорте без настроек; отладка насквозь | Граница держится на архитектурном тесте, а не на сборке |
| Данные внутри проекта Godot (`src/Game/data/`) | Нет шага синхронизации | Источник правды внутри движкового слоя; тесты Core зависят от папки Godot |
| Данные в корневом `data/`, копия в Godot перед экспортом | Один источник правды рядом с Core; горячая перезагрузка в разработке из исходников | Экспорт обязан идти через скрипт |

## Решение

```
/
├── Game.sln                     # solution at repo root
├── Directory.Build.props        # TargetFramework, Nullable, LangVersion in one place
├── Directory.Packages.props     # central package versions
├── .editorconfig
├── BannedSymbols.txt            # APIs forbidden in Core (see ADR 0005)
├── src/
│   ├── Core/                    # Microsoft.NET.Sdk, pure C#, warnings as errors
│   └── Game/                    # Godot project: project.godot + Game.csproj (Godot.NET.Sdk)
├── tests/
│   └── Core.Tests/              # xUnit v3; includes architecture test "Core has no Godot refs"
├── data/                        # source of truth for content JSON
│   └── schema/                  # JSON Schemas
├── locale/                      # translations (CSV) for Godot TranslationServer
├── tools/                       # scripts: build, test, run, smoke, export, sync-data, art-gen
└── docs/
```

- **Целевой фреймворк:** `net10.0` для всех проектов, задаётся одним свойством в `Directory.Build.props`. Если Godot 4.7 не примет `net10.0` в `Game`, всё решение откатывается на `net8.0` одной правкой.
- **Core:** `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`. Запрещённые API — через `BannedApiAnalyzers`.
- **Game:** ссылается на Core обычным `ProjectReference`. Nullable включён. Предупреждения как ошибки в Game пока выключены: генератор Godot выдаёт свои диагностики, включим после проверки.
- **Защита границы:** архитектурный тест проверяет, что сборка Core не ссылается ни на одну сборку `Godot*`. Тест без внешних библиотек, через `GetReferencedAssemblies()`.
- **Данные:** источник правды — корневой `data/`. Core не открывает файлы сам, а получает содержимое через интерфейс источника данных.
  - В режиме разработки Game читает прямо из корневого `data/`, поэтому горячая перезагрузка работает на исходных файлах.
  - Перед экспортом скрипт копирует `data/` в `src/Game/data/`. Эта папка в `.gitignore`. Экспорт упаковывает её в `.pck` фильтром `*.json`.
  - Тесты читают `data/` напрямую.

## Последствия

- Минимальная версия Godot 4.6, используем 4.7.2 (ADR 0001).
- Копия `data/` в проекте Godot генерируется и не коммитится. Значит, правка контента всегда идёт в одном месте.
- Скрипты из `tools/` становятся обязательной точкой входа для экспорта. Экспорт напрямую из редактора без синхронизации даст устаревшие данные. Это правило будет записано в `CLAUDE.md` и корневой README в фазе 2, а скрипт экспорта будет сам вызывать синхронизацию.

## Источники

См. раздел «Источники» в `docs/research/solution-structure.md`.
