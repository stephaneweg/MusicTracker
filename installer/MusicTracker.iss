; Inno Setup script for MusicTracker.
; Compiled by installer\release.ps1 (which passes the version + paths). To build by hand:
;   ISCC.exe /DMyAppVersion=1.0.1.0 installer\MusicTracker.iss
;
; Per-user install (PrivilegesRequired=lowest) so the auto-update can run the installer /VERYSILENT
; WITHOUT a UAC prompt. User data lives in %AppData%/%LocalAppData% (see AppPaths), never next to the exe.

; MyAppName = internal identity (install path + output filename) — KEEP "MusicTracker" so the
; in-app auto-update keeps finding MusicTrackerSetup-*.exe and upgrading in place.
#define MyAppName "MusicTracker"
; MyAppDisplayName = the user-visible product name shown in the wizard, shortcuts and Start menu.
#define MyAppDisplayName "Aria Studio"
#define MyAppPublisher "Stéphan Wegener"
#define MyAppExeName "MusicTracker.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\MusicTracker\bin\Release"
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

[Setup]
; A STABLE AppId so successive versions upgrade in place (never change it).
AppId={{A3F1C9E2-7B4D-4E6A-9C21-5D8E0F3B2A17}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppDisplayName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#OutputDir}
OutputBaseFilename=MusicTrackerSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole Release output, minus debug symbols and the (runtime-downloaded) SoundFont.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,*.vshost.*,SoundFont\*,userdata\*,settings.json,recent.json,userdata.json"

[Icons]
Name: "{group}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppDisplayName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Warn (but don't block) if the .NET Framework 4.8 runtime is missing.
function InitializeSetup(): Boolean;
var
  release: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', release) then
  begin
    if release < 528040 then
      MsgBox('Aria Studio nécessite Microsoft .NET Framework 4.8.' + #13#10 +
             'Il n''a pas été détecté ; installez-le si l''application ne démarre pas.', mbInformation, MB_OK);
  end;
end;
