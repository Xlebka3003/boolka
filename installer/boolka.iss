; Установка только для текущего пользователя. Настройки и игровые папки сюда не входят.
#ifndef SourceDir
  #error SourceDir must point to a verified standalone build
#endif
#ifndef OutputDir
  #error OutputDir must point to the release output directory
#endif
#ifndef ProductVersion
  #define ProductVersion GetFileVersion(AddBackslash(SourceDir) + "boolka.exe")
#endif
#ifndef OutputName
  #define OutputName "boolka-" + ProductVersion + "-windows-x64-setup"
#endif

[Setup]
#ifdef TestAppId
; Отдельный AppId не позволяет проверке затронуть установленную пользовательскую boolka.
AppId={#TestAppId}
AppName=boolka installer fixture
#else
AppId={{DD5CE4EC-EB35-4FC2-A8AB-A4C8CBEC805A}
AppName=boolka
#endif
AppVersion={#ProductVersion}
AppPublisher=Xlebka
AppPublisherURL=https://github.com/Xlebka3003/boolka
AppSupportURL=https://github.com/Xlebka3003/boolka/issues
AppUpdatesURL=https://github.com/Xlebka3003/boolka/releases
DefaultDirName={localappdata}\Programs\boolka
DefaultGroupName=boolka
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayIcon={app}\boolka.exe
SetupIconFile=..\src\Boolka.App\Assets\boolka.ico
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
Uninstallable=yes
CreateUninstallRegKey=yes
VersionInfoVersion={#ProductVersion}
VersionInfoProductName=boolka
VersionInfoProductVersion={#ProductVersion}
VersionInfoDescription=boolka installer
VersionInfoCopyright=Copyright (c) 2026 Xlebka

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Явный список исключает случайное включение журналов, настроек и временных файлов.
Source: "{#SourceDir}\boolka.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Assets\Flags\LICENSE.txt"; DestDir: "{app}\Assets\Flags"; Flags: ignoreversion
Source: "{#SourceDir}\Assets\Lucide\LICENSE.txt"; DestDir: "{app}\Assets\Lucide"; Flags: ignoreversion
Source: "{#SourceDir}\Licenses\dotnet\Desktop-LICENSE.txt"; DestDir: "{app}\Licenses\dotnet"; Flags: ignoreversion
Source: "{#SourceDir}\Licenses\dotnet\Runtime-LICENSE.txt"; DestDir: "{app}\Licenses\dotnet"; Flags: ignoreversion
Source: "{#SourceDir}\Licenses\dotnet\Runtime-THIRD-PARTY-NOTICES.txt"; DestDir: "{app}\Licenses\dotnet"; Flags: ignoreversion

[Icons]
Name: "{group}\boolka"; Filename: "{app}\boolka.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\boolka"; Filename: "{app}\boolka.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Даже интерактивный запуск требует отдельного выбора. Silent-проверки приложение не запускают.
Filename: "{app}\boolka.exe"; Description: "{cm:LaunchProgram,boolka}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked
