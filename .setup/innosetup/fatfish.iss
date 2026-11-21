; ============================================================================
;  FatFish — Inno Setup script
;
;  Build:
;    ISCC.exe fatfish.iss
;    ISCC.exe /DAppVersion=2.1.2 fatfish.iss
;
;  Unattended (used by our own tests):
;    Setup.exe /VERYSILENT /DIR=<dir> /APIKEY=sk-xxx /BASEURL=... /MODEL=...
;
;  Why Inno instead of the earlier IExpress self-extractor:
;    * Inno produces a real Windows installer: native Add/Remove entry, start
;      menu group, uninstaller, file version info, and we can set the setup's
;      own icon (FatFish.ico) — IExpress gives none of that.
;    * Uninstall is handled by Inno itself, so we no longer ship a
;      PowerShell uninstaller that rewrites HKCU on its own.
;
;  Two things this script deliberately does NOT do:
;    * It never overwrites or deletes an existing .env (user config).
;    * It does not bundle the .NET runtime (~150 MB). Instead PrepareToInstall
;      runs ensure-desktop-runtime.ps1, which downloads it from Microsoft's
;      CDN only when missing.
;
;  AppId must NEVER change: Inno uses it to recognise an existing install.
; ============================================================================

#define AppName "FatFish"
#define AppPublisher "FatFish"
#define AppExe "FatFish.exe"

#ifndef AppVersion
  #define AppVersion "2.1.1"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\..\dist\FatFish-" + AppVersion + "-Setup\payload"
#endif

#ifndef SetupIcon
  #define SetupIcon "..\..\src\FatFish.App\Assets\FatFish.ico"
#endif

[Setup]
AppId={{8F3A2C71-5D64-4E2B-9C1A-7B0E6D3F9A55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; Per-user install: no admin prompt, no system-wide changes. Matches what the
; old PowerShell installer did (install.cmd -> %LOCALAPPDATA%\FatFish).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
DisableDirPage=auto
DisableProgramGroupPage=auto

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
CloseApplications=yes
SetupLogging=yes
AllowNoIcons=yes

; Chinese first so it is the default language; English kept as a fallback for
; anyone running the installer on a non-Chinese Windows.
;
; ChineseSimplified.isl is NOT bundled with Inno Setup (the 29 shipped message
; files contain no Chinese), so a copy lives next to this script. It comes from
; the Inno Setup repo: Files/Languages/ChineseSimplified.isl
; (maintainer Zhenghan Yang, "Inno Setup version 6.5.0+" messages).
; Keep it in sync when Inno adds new messages: ISCC fails loudly if a required
; message is missing, so a stale file shows up at build time, not at runtime.
[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#PayloadDir}\{#AppExe}";  DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\README.md";  DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PayloadDir}\LICENSE";    DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PayloadDir}\NOTICE";     DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Seed a config only when the user has none. uninsneveruninstall keeps their
; keys safe across upgrades and uninstalls.
Source: "{#PayloadDir}\.env.example"; DestDir: "{app}"; DestName: ".env"; Flags: onlyifdoesntexist uninsneveruninstall skipifsourcedoesntexist
; Environment probe + auto-repair, extracted on demand by PrepareToInstall
; (Flags: dontcopy means it is not installed, only unpacked to {tmp}).
Source: "ensure-prerequisites.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "现在启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  PrereqPage: TWizardPage;
  PrereqMemo: TNewMemo;
  PrereqFixBtn: TNewButton;
  PrereqReady: Boolean;
  ConfigPage: TInputQueryWizardPage;
  PurposePage: TInputQueryWizardPage;
  FreshConfig: Boolean;

const
  KEY_API      = 'FATFISH_API_KEY';
  KEY_BASE     = 'FATFISH_BASE_URL';
  KEY_MODEL    = 'FATFISH_MODEL';
  KEY_VERIFIER = 'VERIFIER_API_KEY';
  KEY_TAVILY   = 'TAVILY_API_KEY';
  KEY_LIQIU    = 'LIQIU_API_KEY';

// ---------------------------------------------------------------------------
// Environment probe.
//
// Runs as soon as the user leaves the folder page, shows the checklist inside
// the wizard, and offers to repair (the only repairable item is the .NET 10
// desktop runtime). The Next button is blocked while anything is still FAIL.
//
// Why here and not in PrepareToInstall: that runs at the very end, right before
// files are copied. The user would fill in two pages of API keys first, then
// get bounced. Checking right after the folder is chosen fails fast.
//
// PrepareToInstall still probes, but only in silent mode -- unattended installs
// never show pages, so Nothing else would gate them.
//
// The script writes an ASCII report ("STATE|NAME|DETAIL") because Inno's
// LoadStringsFromFile reads ANSI and a UTF-8 report would turn to mojibake.
// ---------------------------------------------------------------------------
function FormatRecord(Line: String): String;
var
  P1, P2: Integer;
  State, Name, Detail, Tag: String;
begin
  Result := Line;
  P1 := Pos('|', Line);
  if P1 = 0 then Exit;
  State := Copy(Line, 1, P1 - 1);
  P2 := Pos('|', Copy(Line, P1 + 1, Length(Line)));
  if P2 = 0 then Exit;
  P2 := P1 + P2;
  Name := Copy(Line, P1 + 1, P2 - P1 - 1);
  Detail := Copy(Line, P2 + 1, Length(Line));

  if State = 'OK' then
    Tag := '[正常]  '
  else if State = 'FIXED' then
    Tag := '[已修复]'
  else if State = 'SKIP' then
    Tag := '[跳过]  '
  else
    Tag := '[缺失]  ';

  Result := Tag + ' ' + Name + '：' + Detail;
end;

procedure RunProbe(Repair: Boolean);
var
  ResultCode, I: Integer;
  ScriptPath, Args, ReportPath, Line, Text: String;
  Lines: TArrayOfString;
  Failed: Boolean;
begin
  ReportPath := ExpandConstant('{tmp}\prereq-report.txt');
  DeleteFile(ReportPath);
  PrereqReady := False;
  PrereqMemo.Lines.Text := '正在检查运行环境，请稍候……';
  PrereqFixBtn.Enabled := False;

  ExtractTemporaryFile('ensure-prerequisites.ps1');
  ScriptPath := ExpandConstant('{tmp}\ensure-prerequisites.ps1');

  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
          ' -TargetDir "' + WizardDirValue + '"' +
          ' -ReportFile "' + ReportPath + '"';
  if Repair then
    Args := Args + ' -Repair';

  // SW_HIDE: results are rendered in the wizard, a console on top is just noise.
  if not Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    PrereqMemo.Lines.Text := '无法启动 PowerShell，因此无法检查运行环境。';
    PrereqFixBtn.Enabled := True;
    Exit;
  end;

  if not (FileExists(ReportPath) and LoadStringsFromFile(ReportPath, Lines)) then
  begin
    PrereqMemo.Lines.Text := '没有拿到检查结果（报告文件缺失）。可以点「重新检查」再试。';
    PrereqFixBtn.Enabled := True;
    Exit;
  end;

  Text := '';
  Failed := False;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    if Pos('SUMMARY|', Line) = 1 then
    begin
      // summary line is not a check; skip it
    end
    else
    begin
      if Pos('FAIL|', Line) = 1 then
        Failed := True;
      Text := Text + FormatRecord(Line) + #13#10;
    end;
  end;

  if Failed then
  begin
    Text := Text + #13#10 +
            '有必需组件未满足。点下面的「修复缺失项」自动下载并安装' + #13#10 +
            '（需要联网，约 75 MB，不需要管理员权限）；也可以按上面的提示手动安装。';
  end
  else
  begin
    Text := Text + #13#10 + '环境检查通过，可以继续安装。';
  end;

  PrereqMemo.Lines.Text := Text;
  PrereqReady := not Failed;
  PrereqFixBtn.Enabled := Failed;
end;

procedure PrereqFixClicked(Sender: TObject);
begin
  RunProbe(True);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = PrereqPage.ID then
    RunProbe(False);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PrereqPage.ID then
  begin
    if not PrereqReady then
    begin
      MsgBox('运行环境还不满足，安装无法继续。' + #13#10 + #13#10 +
             '点「修复缺失项」再检查一次；若仍不通过，请按上面的提示手动安装。',
             mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ScriptPath, Args: String;
begin
  Result := '';

  // Interactive installs were already gated by the wizard page above.
  // Silent installs have no pages, so this is their only gate.
  if not WizardSilent then Exit;

  ExtractTemporaryFile('ensure-prerequisites.ps1');
  ScriptPath := ExpandConstant('{tmp}\ensure-prerequisites.ps1');
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
          ' -TargetDir "' + ExpandConstant('{app}') + '"' +
          ' -ReportFile "' + ExpandConstant('{tmp}\prereq-report-silent.txt') + '"' +
          ' -Repair';

  if not Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := '无法启动 PowerShell，运行环境未能检查。'
  else if ResultCode <> 0 then
    Result := '运行环境不满足，安装未开始。请先安装 .NET 10 桌面运行时：' +
              'https://dotnet.microsoft.com/download/dotnet/10.0';
end;

{ --------------------------------------------------------------------------
  Two config pages.

  Why pages at all: the old installer only copied .env.example and told the
  user to edit a config file by hand. That is exactly where non-technical users
  stop.

  Why a SECOND page: cost attribution. FatFish talks to several paid services
  and each one bills against its own key. One key for everything means the
  provider's dashboard shows a single opaque number; a key per purpose lets you
  see which purpose burned the tokens, set a limit per purpose, and isolate a
  bad key when something answers 401.

  What can actually be separated -- checked against the code, not assumed:
    * VERIFIER_API_KEY - truly separate. VerifyOptions.cs falls back to the main
      key only when this one is empty or does not look like a key. The verifier
      runs once per round, so its usage is the easiest to lose inside the main
      key's total.
    * TAVILY_API_KEY   - a different vendor (web search / fact-checking).
    * LIQIU_API_KEY    - a different vendor (image generation).
    * DEEPSEEK_API_KEY - deliberately NOT offered here. EnvSchema.cs treats it
      as a legacy alias of FATFISH_API_KEY: when both are set to different
      values FATFISH_API_KEY wins while the balance page may show the other.
      Selling it as "a separate key for plugins" would be a lie.

  Why the pages can be skipped, and why they never fight the template:
    * upgrade (an .env already exists)  -> pages skipped, file untouched
    * unattended install                -> pages never show; /APIKEY= etc. feed them
    * nothing typed                     -> .env template stays as-is
  -------------------------------------------------------------------------- }
procedure InitializeWizard;
begin
  // ---------------------------------------------------------------------
  // Environment check page: appears right after the user picks a folder.
  // Showing it here (instead of failing at the very end) means the user finds
  // out before filling in API keys, and the repair button is one click away.
  // ---------------------------------------------------------------------
  PrereqPage := CreateCustomPage(wpSelectDir,
    '检查运行环境',
    '确认这台电脑能运行 FatFish；缺少的组件可以在这里直接补上');

  PrereqMemo := TNewMemo.Create(PrereqPage);
  PrereqMemo.Parent := PrereqPage.Surface;
  PrereqMemo.Left := 0;
  PrereqMemo.Top := 0;
  PrereqMemo.Width := PrereqPage.SurfaceWidth;
  PrereqMemo.Height := PrereqPage.SurfaceHeight - 42;
  PrereqMemo.ScrollBars := ssVertical;
  PrereqMemo.ReadOnly := True;
  PrereqMemo.WordWrap := True;

  PrereqFixBtn := TNewButton.Create(PrereqPage);
  PrereqFixBtn.Parent := PrereqPage.Surface;
  PrereqFixBtn.Caption := '修复缺失项（重新检查）';
  PrereqFixBtn.Width := 200;
  PrereqFixBtn.Left := 0;
  PrereqFixBtn.Top := PrereqPage.SurfaceHeight - 32;
  PrereqFixBtn.OnClick := @PrereqFixClicked;
  PrereqFixBtn.Enabled := False;

  ConfigPage := CreateInputQueryPage(PrereqPage.ID,
    '主模型',
    'FatFish 靠它对话与生成',
    '填入主模型服务的 API 密钥即可开始使用，其余留空则用默认值。' + #13#10 + #13#10 +
    '密钥以明文写进安装目录下的 .env，不会上传到任何地方；以后想改，直接编辑那个文件。');

  ConfigPage.Add('API 密钥（必填）:', False);
  ConfigPage.Add('接口地址:', False);
  ConfigPage.Add('模型名:', False);
  ConfigPage.Values[1] := 'https://api.deepseek.com';
  ConfigPage.Values[2] := 'deepseek-flash';

  PurposePage := CreateInputQueryPage(ConfigPage.ID,
    '按用途分开记账（可选）',
    '想让平台分别统计每项用量，就给它们各自的密钥',
    'FatFish 会用到好几个付费服务，每一家都有它自己的密钥。' + #13#10 +
    '全部留空也能正常使用，只是这些消耗都记在上面那把主密钥名下，账单只有一个总数。' + #13#10 + #13#10 +
    '填了独立密钥，就能在对应平台的用量页里单独看到这一项花了多少，' +
    '也方便给单项设限额、出问题时单独排查。' + #13#10 + #13#10 +
    '审查员每轮对话都会调用一次，它的消耗最容易混在主密钥里看不见。' +
    '若审查员用的是别家服务，装好后还需在 .env 里补 VERIFIER_BASE_URL 与 VERIFIER_MODEL。');

  PurposePage.Add('审查员 API 密钥（可选）:', False);
  PurposePage.Add('联网搜索密钥 Tavily（可选）:', False);
  PurposePage.Add('出图密钥（可选）:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := False;
  if (ConfigPage = nil) or (PurposePage = nil) then Exit;
  if (PageID <> ConfigPage.ID) and (PageID <> PurposePage.ID) then Exit;

  // GOTCHA (hit for real, 2026-11-21): ShouldSkipPage runs while the wizard is
  // still being built, BEFORE the Select Destination Location page has set the
  // app directory. Expanding the app constant there raises
  // "An attempt was made to expand the 'app' constant before it was
  // initialized". WizardDirValue is the safe way to ask the same question.
  // (Note: use // comments here, not braces -- the preprocessor expands
  //  brace-delimited things even inside comments.)
  Dir := WizardDirValue;
  if Dir = '' then Exit;

  Result := FileExists(AddBackslash(Dir) + '.env');
end;

{ Command line beats the pages: /APIKEY=... keeps unattended installs working
  and gives the test suite something deterministic to assert on. }
function ParamOrValue(Page: TInputQueryWizardPage; ParamName: String; Index: Integer): String;
begin
  Result := Trim(ExpandConstant('{param:' + ParamName + '}'));
  if (Result = '') and (Page <> nil) then
    Result := Trim(Page.Values[Index]);
end;

function SetEnvValue(var Lines: TArrayOfString; Key, Value: String): Boolean;
var
  I, Count: Integer;
begin
  Result := False;
  Count := GetArrayLength(Lines);
  for I := 0 to Count - 1 do
  begin
    { Match both "KEY=" and the commented-out "# KEY=" form. }
    if (Pos(Key + '=', Lines[I]) = 1) or (Pos('# ' + Key + '=', Lines[I]) = 1) then
    begin
      Lines[I] := Key + '=' + Value;
      Result := True;
      Exit;
    end;
  end;

  SetArrayLength(Lines, Count + 1);
  Lines[Count] := Key + '=' + Value;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  EnvPath, ApiKey, BaseUrl, Model, VerifierKey, Tavily, Liqiu: String;
  Lines: TArrayOfString;
begin
  if CurStep = ssInstall then
  begin
    { Remember before any file is copied: is this a fresh config? }
    FreshConfig := not FileExists(ExpandConstant('{app}\.env'));
    Exit;
  end;

  if CurStep <> ssPostInstall then Exit;
  if not FreshConfig then Exit;                      { upgrade: never touch it }
  EnvPath := ExpandConstant('{app}\.env');
  if not FileExists(EnvPath) then Exit;

  ApiKey      := ParamOrValue(ConfigPage,  'APIKEY',      0);
  BaseUrl     := ParamOrValue(ConfigPage,  'BASEURL',     1);
  Model       := ParamOrValue(ConfigPage,  'MODEL',       2);
  VerifierKey := ParamOrValue(PurposePage, 'VERIFIERKEY', 0);
  Tavily      := ParamOrValue(PurposePage, 'TAVILY',      1);
  Liqiu       := ParamOrValue(PurposePage, 'LIQIUKEY',    2);

  { Nothing typed anywhere: keep the template exactly as shipped. }
  if (ApiKey = '') and (VerifierKey = '') and (Tavily = '') and (Liqiu = '') then Exit;

  if not LoadStringsFromFile(EnvPath, Lines) then Exit;

  if ApiKey <> '' then
    SetEnvValue(Lines, KEY_API, ApiKey);
  if BaseUrl <> '' then
    SetEnvValue(Lines, KEY_BASE, BaseUrl);
  if Model <> '' then
    SetEnvValue(Lines, KEY_MODEL, Model);
  if VerifierKey <> '' then
    SetEnvValue(Lines, KEY_VERIFIER, VerifierKey);
  if Tavily <> '' then
    SetEnvValue(Lines, KEY_TAVILY, Tavily);
  if Liqiu <> '' then
    SetEnvValue(Lines, KEY_LIQIU, Liqiu);

  SaveStringsToFile(EnvPath, Lines, False);
end;

// Deliberately no CurUninstallStepChanged hook.
// Inno already deletes everything it installed and tries to remove {app};
// the folder survives only because .env / logs / workspace are user data we
// mark uninsneveruninstall. An extra RemoveDir() here did nothing useful and
// was the closest suspect for a leftover unins000.exe after silent uninstall.
