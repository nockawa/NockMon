; NockMon installer (Inno Setup 6). Built by tools/release/build.ps1, one installer per architecture:
;   ISCC /DAppVersion=1.0.1 /DArch=x64 /DSourceDir=<self-contained publish folder> /DOutputDir=<folder> NockMon.iss
; Per-machine install in Program Files: NockMon always runs elevated, so its files must not be writable by the user's
; non-elevated processes (a per-user folder would let any of them plant code that then runs as administrator).

#ifndef AppVersion
  #error Define AppVersion (/DAppVersion=1.2.3)
#endif
#ifndef FileVersion
  ; Numeric only (VersionInfoVersion): build.ps1 passes AppVersion without its prerelease suffix.
  #define FileVersion AppVersion
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Define SourceDir (/DSourceDir=<self-contained publish folder>)
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#if Arch == "x64"
  #define ArchAllowed "x64compatible"
#elif Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  #error Arch must be x64 or arm64
#endif

[Setup]
; Never change AppId: it identifies the installation for upgrades and uninstall (and winget's ProductCode).
AppId={{8438DA19-4C3D-4C08-AA4F-8D5D11F16551}
AppName=NockMon
AppVersion={#AppVersion}
AppVerName=NockMon {#AppVersion}
AppPublisher=Loïc Baumann
AppPublisherURL=https://github.com/nockawa/NockMon
AppSupportURL=https://github.com/nockawa/NockMon/issues
AppUpdatesURL=https://github.com/nockawa/NockMon/releases
AppCopyright=Copyright (c) 2026 Loïc Baumann
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\NockMon
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=NockMon-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\src\NockMon.App\Assets\NockMon.ico
UninstallDisplayIcon={app}\NockMon.exe
UninstallDisplayName=NockMon
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Restart Manager closes a running NockMon (files in use) on upgrade, silently under winget.
CloseApplications=yes
RestartApplications=no
; The PATH task: broadcast the change so new terminals see it.
ChangesEnvironment=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "addtopath"; Description: "Add NockMon to the &PATH (lets AI agents start ""nockmon mcp"")"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\NockMon"; Filename: "{app}\NockMon.exe"; Comment: "Configurable system monitor"
Name: "{autodesktop}\NockMon"; Filename: "{app}\NockMon.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NotOnPath(ExpandConstant('{app}'))

[Run]
; The installer is elevated: NockMon starts elevated without a second UAC prompt.
Filename: "{app}\NockMon.exe"; Description: "Start NockMon"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Files in use otherwise. Settings and layouts stay in each user's %LOCALAPPDATA%\NockMon.
Filename: "{sys}\taskkill.exe"; Parameters: "/im NockMon.exe /f"; Flags: runhidden; RunOnceId: "StopNockMon"

[Code]
const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function NotOnPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Path) then
    Result := True
  else
    Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

procedure RemoveFromPath(Dir: string);
var
  Path, Entry, Kept: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Path) then
    exit;
  Kept := '';
  Path := Path + ';';
  while Path <> '' do
  begin
    P := Pos(';', Path);
    Entry := Copy(Path, 1, P - 1);
    Delete(Path, 1, P);
    if (Entry <> '') and (CompareText(Entry, Dir) <> 0) then
    begin
      if Kept <> '' then
        Kept := Kept + ';';
      Kept := Kept + Entry;
    end;
  end;
  RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Kept);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
