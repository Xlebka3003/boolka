# Установка boolka для Windows

Выпуски содержат одно и то же автономное Windows x64 приложение. Устанавливать .NET отдельно не требуется.

## Установщик

Запустите `boolka-<версия>-windows-x64-setup.exe`. Он устанавливает boolka для текущего пользователя в `%LocalAppData%\Programs\boolka`, добавляет пункт меню «Пуск» и по желанию ярлык на рабочем столе. Права администратора не требуются.

Запуск приложения в конце установки необязателен. Удалить установленную boolka можно через список приложений Windows. Удаление самой программы не удаляет настройки boolka, AddOns, SavedVariables и резервные копии игровых данных.

## ZIP без установки

Распакуйте `boolka-<версия>-windows-x64-standalone.zip` в отдельную папку и запустите `boolka.exe`. Сохраните семь файлов лицензий рядом с приложением. Настройки boolka хранятся отдельно от EXE в `%LocalAppData%\boolka`; перенос одной папки приложения не переносит настройки.

## Сборка пакетов

Нужны Windows, .NET 8 SDK и официальный компилятор [Inno Setup](https://jrsoftware.org/isdl.php). Компилятор используется только при подготовке выпуска и не нужен пользователю boolka. Штатный параметр `/PORTABLE=1` установщика Inno Setup позволяет распаковать сам компилятор в отдельную папку без регистрации его установки.

```powershell
dotnet publish src/Boolka.App/Boolka.App.csproj -p:PublishProfile=WindowsStandalone -p:NuGetAudit=false -o app-<версия>-standalone -m:1
./scripts/Package-Windows.ps1 -SourceDirectory ./app-<версия>-standalone -CompilerPath 'C:/Tools/Inno Setup/ISCC.exe'
```

Упаковщик читает версию из EXE и создаёт установщик, ZIP и `SHA256SUMS.txt` в `distributions`. Он допускает только восемь файлов автономной сборки, отклоняет ссылки, проверяет содержимое ZIP и не перезаписывает готовые файлы предыдущего выпуска. Файлы сначала собираются во временной папке внутри каталога вывода.

## Изолированная проверка установщика

При тестировании задавайте отдельный `TestAppId` и отдельную папку вывода компилятора. Использование рабочего AppId в тесте может изменить запись настоящей установленной boolka в списке приложений Windows.

```powershell
& 'C:/Tools/Inno Setup/ISCC.exe' '/DSourceDir=C:/fixture/app' '/DOutputDir=C:/fixture/output' '/DProductVersion=0.41.9' '/DOutputName=boolka-fixture' '/DTestAppId=boolka.fixture.UNIQUE_ID' ./installer/boolka.iss
./fixture/output/boolka-fixture.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS= /DIR="C:/fixture/installed"
```

Тихая установка никогда не запускает boolka. Проверять удаление следует только через uninstaller этой отдельной fixture-папки; игровые файлы и настоящие пользовательские настройки в проверке не участвуют.
