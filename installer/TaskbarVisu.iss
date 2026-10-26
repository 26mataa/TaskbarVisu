; Installeur Windows de TaskbarVisu (Inno Setup 6).
; Compilation : tools\build-release.ps1 (ou ISCC.exe /DAppVersion=x.y.z installer\TaskbarVisu.iss)
; Attend le build autonome dans build\portable\TaskbarVisu.exe (voir README).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B0E3F52-6D2A-4C1B-9E7A-2F1C9A7D5E10}
AppName=TaskbarVisu
AppVersion={#AppVersion}
AppPublisher=TaskbarVisu
AppPublisherURL=https://github.com/26mataa/TaskbarVisu
AppSupportURL=https://github.com/26mataa/TaskbarVisu/issues
AppUpdatesURL=https://github.com/26mataa/TaskbarVisu/releases
VersionInfoVersion={#AppVersion}
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
; Mise à jour depuis l'application (silencieuse, /RELAUNCH=1) : on la relance, sans ouvrir les réglages.
; Une installation silencieuse par winget ne passe pas ce paramètre et ne lance rien.
Filename: "{app}\TaskbarVisu.exe"; Parameters: "--tray"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM TaskbarVisu.exe /F"; Flags: runhidden; RunOnceId: "StopTaskbarVisu"

[Registry]
; « Lancer avec Windows » : l'entrée créée par l'application est retirée à la désinstallation.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TaskbarVisu"; Flags: uninsdeletevalue dontcreatekey

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;
