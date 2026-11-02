; Inno Setup script for EveryDownload. Build with:  ISCC.exe installer\EveryDownload.iss
; Expects the self-contained publish output in ..\publish (see build-installer.ps1).

#define AppName "EveryDownload"
#define AppVersion "1.0.0"
#define AppExe "EveryDownload.exe"

[Setup]
AppId={{B3E8D1A4-52C7-4F09-9A6B-1D4E7C20F835}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Arman Soltani
AppContact=imrealarman@gmail.com
AppCopyright=Copyright (C) 2026 Arman Soltani
VersionInfoCompany=Arman Soltani
VersionInfoDescription=EveryDownload Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the wizard offers "all users" if the user wants it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=EveryDownload-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "browser"; Description: "Catch downloads from Chrome, Edge and Brave (browser hand-off)"; GroupDescription: "Integration:"
Name: "magnet"; Description: "Open magnet links and .torrent files with EveryDownload"; GroupDescription: "Integration:"
Name: "startup"; Description: "Start EveryDownload with Windows (hidden in the tray)"; GroupDescription: "Integration:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EveryDownload"; ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Registers the browser host (and, if chosen, magnet / .torrent) for the installing user (HKCU, so no admin rights needed).
Filename: "{app}\{#AppExe}"; Parameters: "--install-integration --magnet"; Flags: runhidden waituntilterminated; Tasks: browser and magnet
Filename: "{app}\{#AppExe}"; Parameters: "--install-integration"; Flags: runhidden waituntilterminated; Tasks: browser and not magnet
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-integration"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveIntegration"

; Settings, the download list and the log (%AppData%\EveryDownload) are deliberately left in place on uninstall.
