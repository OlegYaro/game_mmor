# Предлагаемые инструменты и зависимости

Фаза 1, 14.10.2026. Ставлю только то, что одобрено владельцем. Всё ставится только в папку проекта (`.tools/`), без прав администратора, без системных пакетов и без влияния на другие проекты. Подробности и источники: `docs/research/godot-dotnet.md`, `docs/research/claude-tooling.md`, `docs/research/testing.md`.

## 1. Окружение разработки

Решение владельца 14.10.2026: всё ставится только в папку проекта и не влияет на другие проекты.

| Что | Версия | Куда и как | Зачем |
| --- | --- | --- | --- |
| .NET SDK | 10.0.4xx (LTS) | Официальный `dotnet-install.sh` с `--install-dir .tools/dotnet`, `~/.zprofile` не меняется | Сборка и тесты Core и Game |
| Godot .NET | 4.7.2 stable, universal | Официальный zip с godotengine.org в `.tools/godot`, режим self-contained | Движок и редактор |
| Шаблоны экспорта Godot .NET | 4.7.2 | Официальный `.tpz` в `.tools/godot`, рядом с движком | Экспорт под macOS и Windows |
| csharp-ls | 0.28.x | `dotnet tool install csharp-ls --tool-path .tools/bin` | Навигация по C#-коду для меня через LSP |
| NuGet-кэш | — | `.tools/nuget` (через `NUGET_PACKAGES`) | Пакеты проекта, отдельно от `~/.nuget` |

Не нужны: Homebrew, gh CLI (репозиторий создаёшь ты), Docker.

## 2. Плагины и MCP для Claude Code

| Инструмент | Автор | Что даёт | Риски | Вердикт |
| --- | --- | --- | --- | --- |
| `context7` (уже установлен) | Upstash, официальный маркетплейс | Актуальная документация Godot, индекс обновлён 13.10.2026 | Нужна авторизация через `/mcp`; запросы уходят во внешний сервис | Авторизовать сейчас |
| `microsoft-docs` | Microsoft, официальный маркетплейс | Документация .NET API из Microsoft Learn, без ключа | Запросы уходят в Microsoft Learn | Ставить сейчас |
| `csharp-lsp` | Anthropic, официальный маркетплейс | Переход к определению, ошибки компиляции и ссылки по C#-коду | Требует `DOTNET_ROOT`, иначе молча не стартует | Ставить сейчас |
| Хук PostToolUse `dotnet build` | Свой, в `.claude/settings.json` проекта | После каждой правки `.cs` сразу видно ошибки сборки | Замедляет правки на время сборки | Ставить сейчас |
| `beckett-godot-mcp` | beckettlab, 44 звезды, обновлён 07.10.2026 | Сборка C#, логи игры, скриншоты из редактора | Молодой проект; плагин в редакторе | Позже, когда появятся сцены (M1–M2), отдельным решением |
| `Coding-Solo/godot-mcp` | Coding-Solo, 6 тыс. звёзд | Запуск проекта | Не поддерживает C#, 73 открытых issue | Не ставить |
| `IvanMurzak/Godot-MCP` | IvanMurzak | Аддон C# в редакторе | По умолчанию ходит в облако и добавляет NuGet в проект игры | Не ставить |

Плагины `context7`, `microsoft-docs`, `csharp-lsp` и хук PostToolUse включаются только в `.claude/settings.json` проекта, в пользовательские настройки Claude Code не попадают.

Логи и ошибки запущенной игры я получаю без MCP: headless-запуск Godot с записью лога в файл и чтение `user://logs/godot.log`.

## 3. NuGet-пакеты проекта

| Пакет | Где | Лицензия | Зачем |
| --- | --- | --- | --- |
| `System.IO.Hashing` | Core | MIT | `XxHash3` для хэша состояния и карты |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | Core (только при сборке) | MIT | Запрет недетерминированных API |
| `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Core.Tests | Apache-2.0, MIT | Тесты |
| `coverlet.collector` | Core.Tests | MIT | Покрытие кода |
| `JsonSchema.Net` | Core.Tests и tools, не в релизе | MIT + условия maintenance fee | Проверка данных по JSON Schema |
| `dotnet-reportgenerator-globaltool` | Локальный инструмент репозитория | Apache-2.0 | Отчёт и порог покрытия 70% |

Исходный файл без пакета: `FastNoiseLite.cs` (MIT) копируется в Core с сохранением лицензии.

## 4. CI

| Что | Автор | Зачем |
| --- | --- | --- |
| `actions/checkout`, `actions/setup-dotnet` | GitHub | Сборка и тесты на macOS и Windows |
| `chickensoft-games/setup-godot@v2` | Chickensoft | Godot .NET и шаблоны в CI для smoke-теста и экспорта |
| `danielpalme/ReportGenerator-GitHub-Action` | Daniel Palme | Отчёт покрытия в CI |

Файл CI готовлю в фазе 2. Запустится он, когда ты создашь репозиторий на GitHub и сделаешь push.
