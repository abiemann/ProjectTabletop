; Compile through scripts/Build-Installer.ps1 after publishing both applications.
; Inno Setup 6.3 or newer is required for the architecture expressions below.
#if VER < EncodeVer(6,3,0)
  #error Inno Setup 6.3 or newer is required.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1.
#endif
#ifndef PayloadDirectory
  #error PayloadDirectory must be supplied by Build-Installer.ps1.
#endif
#ifndef InstallerOutputDirectory
  #error InstallerOutputDirectory must be supplied by Build-Installer.ps1.
#endif

[Setup]
AppId={{ED4C89E8-8558-4926-95D3-861702C3FFB4}
AppName=Project Tabletop
AppVersion={#AppVersion}
AppPublisher=Alexander Biemann
AppPublisherURL=https://github.com/abiemann/ProjectTabletop
AppSupportURL=https://github.com/abiemann/ProjectTabletop/issues
AppUpdatesURL=https://github.com/abiemann/ProjectTabletop/releases
DefaultDirName={localappdata}\Programs\ProjectTabletop
DefaultGroupName=Project Tabletop
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
LicenseFile={#PayloadDirectory}\LICENSE
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=ProjectTabletop-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ProjectTabletop.App.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Project Tabletop"; Filename: "{app}\ProjectTabletop.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Project Tabletop"; Filename: "{app}\ProjectTabletop.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ProjectTabletop.App.exe"; Description: "Launch Project Tabletop"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

; Uninstall removes the installed payload and shortcuts only. Application state
; in %LOCALAPPDATA%\ProjectTabletop and photographs in Pictures\Project Tabletop
; are deliberately outside {app}; no UninstallDelete entry targets user data.
