; VideoJaNai setup installer (Inno Setup 6).
;
; Wraps the slim core tree the package builder (BuildVideoJaNai) produces into a per-user
; Windows installer. Built in CI by deploy.yml:
;   ISCC.exe installer\videojanai.iss /DAppVersion=<ver> /DSourceDir=<tree>
;
; PER-USER, NO ADMIN, by necessity: the app writes into its own folder at runtime (TensorRT
; engines build into animejanai\onnx, the updater + in-app component manager extract component
; packs and self-update in place). A Program Files install would break those writes for a
; standard user, so the default location is %LOCALAPPDATA%\Programs.
;
; The GPU-specific components (TensorRT runtime + the GPU's builder kernels, RIFE models) are NOT
; bundled - they download post-install via VideoJaNaiUpdater.exe, GPU-matched, keeping the
; installer small. A failed download is non-fatal: open VideoJaNai's App Settings later to finish.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error Define SourceDir (the built slim tree) with /DSourceDir=...
#endif

#define AppName "VideoJaNai"
#define Publisher "the-database"
#define AppExe "VideoJaNai.exe"
#define UpdaterExe "VideoJaNaiUpdater.exe"

[Setup]
AppId={{B7E4D2A1-3C56-4F89-9A2B-1D3E5F7A9C04}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/the-database/VideoJaNai
WizardStyle=modern
; Per-user install: no elevation, lands in %LOCALAPPDATA%\Programs\VideoJaNai,
; directory still changeable on the standard page.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
OutputBaseFilename=VideoJaNai-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; The updater needs its own entry (not just the wildcard) so ExtractTemporaryFile can run it for
; GPU detection during the wizard; exclude it from the wildcard to avoid listing it twice.
Source: "{#SourceDir}\{#UpdaterExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "{#UpdaterExe}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; Remove the whole per-user app folder on uninstall, including files created at runtime that the
; installer never tracked (built engines + timing caches in animejanai\onnx, downloaded component
; packs, components.json, logs, app state).
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  CompPage: TInputOptionWizardPage;
  GpuName: String;
  HasNvidia: Boolean;
  TrtPacks: String;   // comma-separated, detected at wizard start (best-effort)
  RifePack: String;

// Run VideoJaNaiUpdater.exe --recommend (from the given exe path), capturing its KEY=value lines
// into the globals. Returns False if it could not be run.
function RunRecommend(ExePath: String): Boolean;
var
  TmpFile, Cmd: String;
  Lines: TArrayOfString;
  i, eq: Integer;
  key, val: String;
  rc: Integer;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\vjn-recommend.txt');
  Cmd := '/C ""' + ExePath + '" --recommend > "' + TmpFile + '" 2>&1"';
  if not Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Exit;
  if not LoadStringsFromFile(TmpFile, Lines) then
    Exit;
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    eq := Pos('=', Lines[i]);
    if eq > 0 then
    begin
      key := Copy(Lines[i], 1, eq - 1);
      val := Copy(Lines[i], eq + 1, Length(Lines[i]) - eq);
      if key = 'NVIDIA' then HasNvidia := (val = '1')
      else if key = 'GPU' then GpuName := val
      else if key = 'TRT_PACKS' then TrtPacks := val
      else if key = 'RIFE' then RifePack := val;
    end;
  end;
  Result := True;
end;

procedure InitializeWizard;
var
  ExePath, trtLabel: String;
begin
  GpuName := '';
  HasNvidia := False;
  TrtPacks := '';
  RifePack := 'rife';

  ExtractTemporaryFile('{#UpdaterExe}');
  ExePath := ExpandConstant('{tmp}\{#UpdaterExe}');
  RunRecommend(ExePath);

  CompPage := CreateInputOptionPage(wpSelectTasks,
    'Components', 'Choose which AI components to install for your hardware.',
    'Selected components download after the core files are copied. You can change this any time ' +
    'from VideoJaNai''s App Settings.',
    False, False);

  if HasNvidia then
    trtLabel := 'Upscaling (TensorRT) - for ' + GpuName
  else
    trtLabel := 'Upscaling (TensorRT) - requires an NVIDIA GPU (offline DirectML not yet supported)';
  CompPage.Add(trtLabel);
  CompPage.Add('RIFE frame interpolation models');

  // Offline upscaling is TensorRT-only for now, so the TensorRT components require an NVIDIA GPU;
  // disable and uncheck them on non-NVIDIA machines.
  CompPage.Values[0] := HasNvidia;
  CompPage.CheckListBox.ItemEnabled[0] := HasNvidia;
  CompPage.Values[1] := True;
end;

// Install the comma-separated packs in CSV via the installed updater. Updates the visible status
// label per pack (downloads are large). Returns the count that failed.
function InstallPacks(Csv: String): Integer;
var
  ExePath, pack: String;
  comma, rc: Integer;
begin
  Result := 0;
  ExePath := ExpandConstant('{app}\{#UpdaterExe}');
  Csv := Trim(Csv);
  while Csv <> '' do
  begin
    comma := Pos(',', Csv);
    if comma > 0 then
    begin
      pack := Copy(Csv, 1, comma - 1);
      Csv := Copy(Csv, comma + 1, Length(Csv) - comma);
    end
    else
    begin
      pack := Csv;
      Csv := '';
    end;
    pack := Trim(pack);
    if pack = '' then
      Continue;
    WizardForm.StatusLabel.Caption := 'Downloading component: ' + pack + ' (this may take a few minutes)...';
    WizardForm.Refresh;
    if not Exec(ExePath, '--install ' + pack, ExpandConstant('{app}'),
                SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
      Result := Result + 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  failed: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // Re-detect from the now-installed updater: network may be available now even if it wasn't at
  // wizard start, giving authoritative GPU-matched pack names.
  RunRecommend(ExpandConstant('{app}\{#UpdaterExe}'));

  failed := 0;
  if CompPage.Values[0] and (TrtPacks <> '') then
    failed := failed + InstallPacks(TrtPacks);
  if CompPage.Values[1] and (RifePack <> '') then
    failed := failed + InstallPacks(RifePack);

  WizardForm.StatusLabel.Caption := '';
  if failed > 0 then
    MsgBox('Some components could not be downloaded (you may be offline). VideoJaNai will still ' +
           'open; use App Settings later to finish installing them.',
           mbInformation, MB_OK);
end;
