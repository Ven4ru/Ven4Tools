; Ven4Tools_Setup_2.2.4.iss
; Установщик с правами администратора

[Setup]
AppId={{Ven4Tools-2.2.4}}
AppName=Ven4Tools
AppVersion=2.2.4
AppPublisher=Ven4
AppPublisherURL=https://github.com/Ven4ru/Ven4Tools
AppSupportURL=https://github.com/Ven4ru/Ven4Tools
AppUpdatesURL=https://github.com/Ven4ru/Ven4Tools/releases
DefaultDirName={pf}\Ven4Tools
DefaultGroupName=Ven4Tools
UninstallDisplayIcon={app}\Ven4Tools.exe
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=C:\Users\Venes4\Desktop
OutputBaseFilename=Ven4Tools_Setup_2.2.4
SetupIconFile=C:\Users\Venes4\Desktop\backup\Ven4Tools2\icon.ico

; Установщик требует права администратора
PrivilegesRequired=admin

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительные иконки:"

[Files]
Source: "C:\Users\Venes4\AppData\Local\Programs\Ven4Tools\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Ven4Tools"; Filename: "{app}\Ven4Tools.Launcher.exe"; IconFilename: "{app}\Ven4Tools.exe"; IconIndex: 0
Name: "{group}\Uninstall Ven4Tools"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Ven4Tools"; Filename: "{app}\Ven4Tools.Launcher.exe"; Tasks: desktopicon; IconFilename: "{app}\Ven4Tools.exe"; IconIndex: 0

[Run]
; Запускаем лаунчер (он сам запросит права)
Filename: "{app}\Ven4Tools.Launcher.exe"; Description: "Запустить Ven4Tools"; Flags: postinstall nowait skipifsilent

[Registry]
Root: HKLM; Subkey: "Software\Ven4\Ven4Tools"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Ven4\Ven4Tools"; ValueType: string; ValueName: "Version"; ValueData: "2.2.4"; Flags: uninsdeletekey