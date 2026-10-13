; Inno Setup script for SchemaScope.
;
; Builds a per-user installer: no admin rights, no UAC prompt, and a desktop
; shortcut. Run after build.ps1 has produced dist\SchemaScope.exe.
;
;   iscc installer\SchemaScope.iss
;   iscc /DAppVersion=1.2.0 installer\SchemaScope.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "SchemaScope"
#define AppPublisher "Shadowmark"
#define AppUrl "https://schemascope.shadowmark.in"

[Setup]
AppId={{8E4C1D2A-6F3B-4A17-9C5E-2B7D0A9F4E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\SchemaScope.exe
OutputDir=Output
; Constant across releases so the download link on the site never has to change.
OutputBaseFilename=SchemaScope-Setup
SetupIconFile=..\src\SchemaScope.Shell\schemascope.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-user install. SchemaScope needs no admin rights, so it does not ask for
; them - every elevation prompt is people who abandon the install.
PrivilegesRequired=lowest
WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\dist\SchemaScope.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\SchemaScope.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SchemaScope.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SchemaScope.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; No [UninstallRun] for the Share tab's firewall rule. Deleting a rule needs
; admin rights and this per-user uninstaller has none, so it could never work.
; The Share tab has "Remove firewall rule" for that (it asks for UAC itself);
; SECURITY.md says to use it before uninstalling.

[UninstallDelete]
; The WebView2 cache we created beside our own data, the shared-link snapshots
; and the definition cache. Both of those hold full object text, and both are
; disposable. Saved connections in schemascope.db are deliberately left alone -
; reinstalling should not lose them.
Type: filesandordirs; Name: "{localappdata}\SchemaScope\WebView2"
Type: filesandordirs; Name: "{localappdata}\SchemaScope\shares"
Type: filesandordirs; Name: "{localappdata}\SchemaScope\cache"

[Messages]
; Shown on the "ready to install" page.
ReadyLabel2b=SchemaScope installs for your account only and needs no administrator rights.%n%nIt reads database schemas and never writes to them.
