; Inno Setup script for Sentinel
; Compile with Inno Setup 6 (free: https://jrsoftware.org/isdl.php).
; Output: installer\Output\Sentinel-Setup.exe

#define MyAppName        "Sentinel"
#define MyAppVersion     "1.0.0"
#define MyAppPublisher   "Sentinel Project"
#define MyAppExeName     "Sentinel.exe"

[Setup]
AppId={{9B7C2E5A-5E7D-4C8B-AD11-9F4A2C9D1B22}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Sentinel-Setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Point Source at the folder produced by `dotnet publish` (publish\).
; Run dotnet publish before compiling this script.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";       Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Sentinel"; Flags: nowait postinstall skipifsilent
