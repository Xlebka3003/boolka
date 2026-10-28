# Boolka.Core

Общая библиотека `net8.0`, не зависящая от WPF/Avalonia. Пространство имён сохранено `Boolka`.

Содержит каталог ESOUI и модели, чтение манифестов, подбор зависимостей, проверку ZIP, безопасную установку/удаление и откат, резервное копирование, наборы `.boolka`, настройки, локализацию и диагностику. `Assets/Translations.json` встроен в сборку как `Boolka.Translations.json`.

`boolka` (прежний WPF UI), `Boolka.Desktop` (Avalonia) и `Boolka.Verification` имеют friend-доступ к внутренним API. Публичную поверхность специально не расширяли ради UI.

Границы физических путей используют `FileSystemPaths`: Windows — OrdinalIgnoreCase, Unix — Ordinal, включая macOS. Такое строгое сравнение безопасно и для регистрозависимых томов. Регистронезависимые проверки имён модулей/полей манифестов и запрет конфликтующих ZIP-имён остаются отдельной политикой.

## Что ещё не является универсальным

- `EsoLaunchService`, `LaunchProgram` и `LaunchSequence` компилируются без Windows UI/API, но пока сохраняют прежнюю политику `.exe/.bat/.cmd/.ps1`, поиска TTC Client.exe и ESO.exe. Новый интерфейс должен применять свой платформенный адаптер; наличие этих методов не означает проверенный запуск ESO на macOS/Linux.
- `LaunchProcessRunner` с cmd.exe/PowerShell остался в WPF UI.
- `AddonDetailsService` и `PreviewService` с BitmapDecoder/BitmapSource остались WPF-компонентами: Avalonia использует собственную проверку/загрузку изображений.
- Старые настройки/журналы сохраняют прежний формат, миграция Aster ограничена `StorageMigration`.

## Проверка извлечения

Core и старый WPF UI собираются без предупреждений. В `work/core-extraction-check-install`, `...-removal`, `...-dependencies` прежние существенные fixture-проверки запущены как чистые net8.0 проекты, с прямой ссылкой на Core без WPF. Они проверяют обход путей, ссылки, блокировки, журналы, установку/откат, групповое удаление и PCDependsOn на архиве LoreBooks. Проверки выполнены на Windows; реальный запуск на Linux/macOS ещё требуется.
