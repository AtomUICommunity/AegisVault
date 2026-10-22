; AegisVault per-user installer (Inno Setup 6).
;
; Built by CI from the NativeAOT publish folder: this script only packages the
; files and creates shortcuts. Everything the user owns (vault files,
; %APPDATA%\AegisVault preferences) lives outside the install folder and is
; deliberately left behind on uninstall.

#define AppName "AegisVault"
#define AppPublisher "AegisVault contributors"
#define AppExe "AegisVault.App.exe"
#define AppId "{{8F2C1B4E-6F0B-4A1E-9C7D-5B7E2A3D9C41}"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; CI passes /DPublishDir=<absolute publish folder>.
#ifndef PublishDir
  #define PublishDir "..\src\AegisVault.App\bin\Release\net10.0\win-x64\publish"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; Per-user install: no elevation prompt, and the uninstaller needs no admin.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=AegisVault-win-x64-setup-v{#AppVersion}
; The .iss lives in installer/, so this lands at <repo>/artifacts (CI uploads it).
OutputDir=..\artifacts
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\AegisVault.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

[Languages]
; The wizard follows the OS language where Inno has a translation; English is
; the guaranteed fallback (a missing .isl would fail the build).
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.dbg"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] entries on purpose: uninstalling removes the program
; files only. Vaults and preferences must survive, or the user loses their data.
