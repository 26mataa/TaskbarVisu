; Installeur Windows de TaskbarVisu (Inno Setup 6).
; Compilation : ISCC.exe /DAppVersion=0.8.3 installer\TaskbarVisu.iss
; Attend le build autonome dans build\portable\TaskbarVisu.exe (voir README).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B0E3F52-6D2A-4C1B-9E7A-2F1C9A7D5E10}
AppName=TaskbarVisu
AppVersion={#AppVersion}
AppPublisher=TaskbarVisu
AppPublisherURL=https://github.com
DefaultDirName={localappdata}\Programs\TaskbarVisu
DefaultGroupName=TaskbarVisu
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile=..\LICENSE
SetupIconFile=..\src\TaskbarVisu\Assets\TaskbarVisu.ico
UninstallDisplayIcon={app}\TaskbarVisu.exe
OutputDir=..\build
OutputBaseFilename=TaskbarVisu-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\build\portable\TaskbarVisu.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TaskbarVisu"; Filename: "{app}\TaskbarVisu.exe"
Name: "{autodesktop}\TaskbarVisu"; Filename: "{app}\TaskbarVisu.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TaskbarVisu.exe"; Description: "{cm:LaunchProgram,TaskbarVisu}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM TaskbarVisu.exe /F"; Flags: runhidden; RunOnceId: "StopTaskbarVisu"

[Registry]
; « Lancer avec Windows » : l'entrée créée par l'application est retirée à la désinstallation.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TaskbarVisu"; Flags: uninsdeletevalue dontcreatekey
