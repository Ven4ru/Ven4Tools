; Ven4Tools Inno Setup Script
; Version: 2.2.3

#define MyAppName "Ven4Tools"
#define MyAppVersion "2.2.3"
#define MyAppPublisher "Ven4"
#define MyAppExeName "Ven4Tools.exe"
#define MyAppIcon "icon.ico"

[Setup]
; УНИКАЛЬНЫЙ ID - НЕ МЕНЯТЬ!
AppId={{ECDD1685-B885-4F2A-839D-E7DBBF1F9C22}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Ven4ru/Ven4Tools
AppSupportURL=https://github.com/Ven4ru/Ven4Tools/issues
AppUpdatesURL=https://github.com/Ven4ru/Ven4Tools/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra
SolidCompression=yes
OutputDir=..\Output
OutputBaseFilename=Ven4Tools_Setup_{#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
SetupIconFile=C:\Users\Ven4\Ven4Tools\Installer\icon.ico
PrivilegesRequired=lowest
CreateUninstallRegKey=not IsTaskSelected('portable')
Uninstallable=not IsTaskSelected('portable')

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "portable"; Description: "📦 Портативная установка (все настройки в папке с программой)"; \
    GroupDescription: "Режим установки:"; Flags: unchecked
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; \
    GroupDescription: "Дополнительные задачи:"; Flags: checkedonce

[Files]
[Files]
Source: "..\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\Release\net8.0-windows\win-x64\publish\Ven4Tools.Updater.*"; DestDir: "{app}"; Flags: ignoreversion
; Если файла нет - пропускаем без ошибки
Source: "C:\Users\Ven4\Ven4Tools\apps.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Ven4Tools"; Flags: postinstall nowait skipifsilent

[Code]
var
  PortableMode: Boolean;

function IsPortableMode: Boolean;
begin
  Result := PortableMode;
end;

procedure CreatePortableMarker();
var
  MarkerFile: string;
begin
  MarkerFile := ExpandConstant('{app}\portable.dat');
  SaveStringToFile(MarkerFile, 'Portable mode marker' + #13#10, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    PortableMode := WizardIsTaskSelected('portable');
  end;
  
  if (CurStep = ssPostInstall) and PortableMode then
  begin
    CreatePortableMarker();
  end;
end;