; Compile only through scripts/package-installer.ps1, after payload verification.
#ifndef PayloadDirectory
  #error PayloadDirectory is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef NumericVersion
  #error NumericVersion is required
#endif
#ifdef IsolationTestId
  #ifdef PublishableBuild
    #error A publishable installer cannot enable isolation test mode
  #endif
  #ifndef IsolationRoot
    #error IsolationRoot is required for an isolated test
  #endif
  #define AppMutexName "Local\BatteryManager.App.Test." + IsolationTestId
  #ifndef HandoffWaitMilliseconds
    #define HandoffWaitMilliseconds 30000
  #endif
#else
  #ifndef PublishableBuild
    #error Use the verified production packaging entry point
  #endif
  #define AppMutexName "Local\BatteryManager.NewApp.2026"
  #ifdef HandoffWaitMilliseconds
    #error A publishable installer cannot override the handoff timeout
  #endif
  #define HandoffWaitMilliseconds 30000
#endif

[Setup]
#ifdef IsolationTestId
AppId=BatteryManager.Isolation.{#IsolationTestId}
AppName=Battery Manager installer test
CreateUninstallRegKey=no
DefaultDirName={#IsolationRoot}\install target with spaces
SetupMutex=Local\BatteryManager.Setup.Test.{#IsolationTestId}
OutputBaseFilename=BatteryManager-{#AppVersion}-isolation-test-setup
#else
AppId={{99051A29-4531-489B-8808-FB079EA80C2C}
AppName=Battery Manager
DefaultDirName={localappdata}\Programs\BatteryManager
SetupMutex=Local\BatteryManager.Setup.2026
OutputBaseFilename=BatteryManager-{#AppVersion}-win-x64-setup
#endif
AppVersion={#AppVersion}
AppPublisher=Battery Manager contributors
AppPublisherURL=https://github.com/Aice-Chen/BatteryManager
AppSupportURL=https://github.com/Aice-Chen/BatteryManager/issues
AppUpdatesURL=https://github.com/Aice-Chen/BatteryManager/releases
DefaultGroupName=Battery Manager
DisableProgramGroupPage=yes
DisableDirPage=no
UsePreviousAppDir=yes
UsePreviousTasks=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.22000
SetupArchitecture=x64
OutputDir={#OutputDirectory}
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
SetupIconFile=..\..\src\BatteryManager.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\BatteryManager.App.exe
AppMutex={#AppMutexName}
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

#ifndef IsolationTestId
[Tasks]
Name: "startup"; Description: "{code:StartupTaskDescription}"; Flags: unchecked
#endif

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.json"; DestDir: "{app}"; Flags: ignoreversion

#ifndef IsolationTestId
[Icons]
Name: "{autoprograms}\Battery Manager"; Filename: "{app}\BatteryManager.App.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\BatteryManager.App.exe"; Description: "{cm:LaunchProgram,Battery Manager}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
#endif

[CustomMessages]
english.StartupTask=Register a sign-in startup entry
chinesesimplified.StartupTask=登记登录启动项
english.StartupBlockedSuffix= (disabled by Windows)
chinesesimplified.StartupBlockedSuffix=（Windows 已禁用）
english.StartupUnknownSuffix= (Windows permission unknown)
chinesesimplified.StartupUnknownSuffix=（Windows 许可状态未知）
english.StartupBlocked=Windows has disabled this startup entry. This installer keeps that setting. Enable Battery Manager in Windows Startup Apps if you want it to start when you sign in.
chinesesimplified.StartupBlocked=Windows 已禁用此启动项。安装程序会保留该设置；如需登录自启，请在 Windows 的“启动应用”中启用 Battery Manager。
english.StartupUnknown=The startup entry was registered, but its Windows permission could not be confirmed. Check Battery Manager in Windows Startup Apps.
chinesesimplified.StartupUnknown=已登记启动项，但无法确认 Windows 是否允许启动。请在 Windows 的“启动应用”中检查 Battery Manager。
english.CloseApp=Please exit Battery Manager from its tray menu and retry. Installation will not force it to close.
chinesesimplified.CloseApp=请先从托盘菜单退出 Battery Manager，再重试。安装程序不会强行关闭程序。
english.StartupFailed=The startup setting could not be saved. Change it later in Battery Manager settings.
chinesesimplified.StartupFailed=开机启动设置未能保存，请稍后在 Battery Manager 设置中修改。
english.HandoffInvalid=Update handoff parameters are invalid. Close Battery Manager normally and run the installer again.
chinesesimplified.HandoffInvalid=更新交接参数无效。请正常退出 Battery Manager 后重新运行安装程序。
english.HandoffIdentity=The update process does not match Battery Manager in the selected installation directory. Installation was stopped.
chinesesimplified.HandoffIdentity=待退出的进程与目标安装目录中的 Battery Manager 不一致，已停止安装。
english.HandoffQuery=The running application's identity could not be verified. Close Battery Manager normally and run the installer again.
chinesesimplified.HandoffQuery=无法核实运行程序的身份。请正常退出 Battery Manager 后重新运行安装程序。
english.HandoffTimeout=Battery Manager did not exit within 30 seconds. No installation files were changed and no process was forced to close. Try again after it exits.
chinesesimplified.HandoffTimeout=Battery Manager 未在 30 秒内退出。安装文件尚未更改，也未强行关闭进程；请等待退出后重试。
english.HandoffDirectory=An update must use the running application's existing installation directory.
chinesesimplified.HandoffDirectory=此次更新必须使用原程序所在的安装目录。

[Code]
var
  HandoffDirectory: String;

function HandoffOpenProcess(DesiredAccess: LongWord; InheritHandle: Integer; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall setuponly';
function HandoffQueryImage(Process: THandle; Flags: LongWord; ImageName: String; var Size: LongWord): Integer;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall setuponly';
function HandoffWait(Process: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall setuponly';
function HandoffClose(Process: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall setuponly';

function RejectHandoff(MessageKey: String): Boolean;
begin
  Result := False;
  Log('Update handoff rejected: ' + MessageKey);
  SuppressibleMsgBox(CustomMessage(MessageKey), mbError, MB_OK, IDOK);
end;

function InitializeSetup: Boolean;
var
  PidText, Directory, ImageName: String;
  PidValue: Int64;
  I: Integer;
  Process: THandle;
  ImageSize, WaitResult, Error: LongWord;
begin
  Result := True;
  PidText := ExpandConstant('{param:WAITPID|not-specified}');
  if PidText = 'not-specified' then Exit;
  Result := False;
  for I := 1 to Length(PidText) do
    if (PidText[I] < '0') or (PidText[I] > '9') then begin RejectHandoff('HandoffInvalid'); Exit; end;
  PidValue := StrToInt64Def(PidText, 0);
  if (PidValue <= 0) or (PidValue > 4294967295) then begin RejectHandoff('HandoffInvalid'); Exit; end;
  // {app} is not initialized yet. Require an explicit drive-absolute /DIR and recheck it before writing files.
  Directory := ExpandConstant('{param:DIR|}');
  if Length(Directory) < 3 then begin RejectHandoff('HandoffInvalid'); Exit; end;
  if (Directory[2] <> ':') or (Directory[3] <> '\') or
    (Pos('{', Directory) <> 0) or (Pos('}', Directory) <> 0) then begin RejectHandoff('HandoffInvalid'); Exit; end;
  HandoffDirectory := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
#ifdef IsolationTestId
  if CompareText(HandoffDirectory, ExpandFileName('{#IsolationRoot}\install target with spaces')) <> 0 then begin
    RejectHandoff('HandoffDirectory'); Exit;
  end;
#endif
  // QUERY_LIMITED_INFORMATION | SYNCHRONIZE; this grants no termination or memory-write capability.
  Process := HandoffOpenProcess($00101000, 0, PidValue);
  if Process = 0 then begin
    Error := DLLGetLastError;
    Log(Format('Update handoff: OpenProcess failed with error %d.', [Error]));
    if Error = 87 then begin
      Log('Update handoff: process already exited before OpenProcess.');
      Result := True;
    end else RejectHandoff('HandoffQuery');
    Exit;
  end;
  try
    ImageSize := 32768;
    SetLength(ImageName, ImageSize);
    if HandoffQueryImage(Process, 0, ImageName, ImageSize) = 0 then begin
      Error := DLLGetLastError;
      WaitResult := HandoffWait(Process, 0);
      Log(Format('Update handoff: image query failed with error %d; process wait result %d.', [Error, WaitResult]));
      if WaitResult = 0 then begin
        // Windows may discard the image name after exit. The held handle proves this exact process is finished.
        Log('Update handoff: process already terminated; no live process identity is being accepted.');
        Result := True;
      end else RejectHandoff('HandoffQuery');
      Exit;
    end;
    SetLength(ImageName, ImageSize);
    if CompareText(ImageName, AddBackslash(HandoffDirectory) + 'BatteryManager.App.exe') <> 0 then begin
      RejectHandoff('HandoffIdentity'); Exit;
    end;
    Log('Update handoff: verified exact process image, waiting for process exit.');
    // Keep this exact process handle throughout the wait so PID reuse cannot retarget the operation.
    WaitResult := HandoffWait(Process, {#HandoffWaitMilliseconds});
    if WaitResult = 0 then begin
      Log('Update handoff: verified process exited.');
      Result := True;
    end else if WaitResult = 258 then RejectHandoff('HandoffTimeout')
    else RejectHandoff('HandoffQuery');
  finally
    HandoffClose(Process);
  end;
end;

#ifndef IsolationTestId
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'BatteryManager';
  ApprovalKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

// Match the app's conservative interpretation. StartupApproved is never written or removed.
// 0 = no record, 1 = known allowed, 2 = known blocked, 3 = unknown format/access.
function StartupApproval: Integer;
var
  Data: AnsiString;
begin
  Result := 3;
  if not RegValueExists(HKCU, ApprovalKey, RunValueName) then begin
    Result := 0;
    Exit;
  end;
  if not RegQueryBinaryValue(HKCU, ApprovalKey, RunValueName, Data) then Exit;
  if Length(Data) <> 12 then Exit;
  if (Ord(Data[2]) <> 0) or (Ord(Data[3]) <> 0) or (Ord(Data[4]) <> 0) then Exit;
  if Ord(Data[1]) = 2 then Result := 1
  else if Ord(Data[1]) = 3 then Result := 2;
end;

function StartupTaskDescription(Param: String): String;
var
  Approval: Integer;
begin
  Result := CustomMessage('StartupTask');
  Approval := StartupApproval;
  if Approval = 2 then Result := Result + CustomMessage('StartupBlockedSuffix')
  else if Approval = 3 then Result := Result + CustomMessage('StartupUnknownSuffix');
end;

function HasExplicitTaskSelection: Boolean;
var
  I: Integer;
  Param: String;
begin
  Result := False;
  for I := 1 to ParamCount do begin
    Param := Uppercase(ParamStr(I));
    if (Pos('/TASKS=', Param) = 1) or (Pos('/MERGETASKS=', Param) = 1) then begin
      Result := True;
      Exit;
    end;
  end;
end;

function CommandExecutable(Command: String): String;
var
  EndIndex: Integer;
begin
  Command := Trim(Command);
  Result := '';
  if Command = '' then Exit;
  if Command[1] = '"' then begin
    Delete(Command, 1, 1);
    EndIndex := Pos('"', Command);
    if EndIndex > 0 then Result := Copy(Command, 1, EndIndex - 1);
  end else begin
    EndIndex := Pos('.EXE', Uppercase(Command));
    if EndIndex > 0 then Result := Copy(Command, 1, EndIndex + 3);
  end;
end;

function IsBatteryManagerCommand(Command: String): Boolean;
begin
  Result := CompareText(ExtractFileName(CommandExecutable(Command)), 'BatteryManager.App.exe') = 0;
end;

procedure InitializeWizard;
var
  Command: String;
begin
  // Use the effective current Run setting, including changes made in the app after an install.
  // Checked means registered, not allowed by Windows. Preserve the registration on unattended upgrades.
  if not HasExplicitTaskSelection and RegQueryStringValue(HKCU, RunKey, RunValueName, Command) and IsBatteryManagerCommand(Command) then
    WizardSelectTasks('startup');
end;

#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes('{#AppMutexName}') then Result := CustomMessage('CloseApp');
  if (HandoffDirectory <> '') and (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))), HandoffDirectory) <> 0) then
    Result := CustomMessage('HandoffDirectory');
#ifdef IsolationTestId
  if CompareText(ExpandFileName(ExpandConstant('{app}')), ExpandFileName('{#IsolationRoot}\install target with spaces')) <> 0 then
    Result := 'Isolated test installation is restricted to its compiled test directory.';
#endif
end;

#ifndef IsolationTestId
procedure CurStepChanged(CurStep: TSetupStep);
var
  Command: String;
  Saved: Boolean;
  Notice: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Saved := True;
  if WizardIsTaskSelected('startup') then
    Saved := RegWriteStringValue(HKCU, RunKey, RunValueName, '"' + ExpandConstant('{app}\BatteryManager.App.exe') + '"')
  else if RegQueryStringValue(HKCU, RunKey, RunValueName, Command) and IsBatteryManagerCommand(Command) then
    Saved := RegDeleteValue(HKCU, RunKey, RunValueName);
  if not Saved then begin
    Log(CustomMessage('StartupFailed'));
    if not WizardSilent then MsgBox(CustomMessage('StartupFailed'), mbError, MB_OK);
  end else if WizardIsTaskSelected('startup') then begin
    Notice := '';
    if StartupApproval = 2 then Notice := CustomMessage('StartupBlocked')
    else if StartupApproval = 3 then Notice := CustomMessage('StartupUnknown');
    if Notice <> '' then begin
      Log(Notice);
      if not WizardSilent then MsgBox(Notice, mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  // Uninstall never removes another copy's startup entry, and never deletes LocalAppData user data.
  if (CurUninstallStep = usPostUninstall) and RegQueryStringValue(HKCU, RunKey, RunValueName, Command) then
    if CompareText(CommandExecutable(Command), ExpandConstant('{app}\BatteryManager.App.exe')) = 0 then
      RegDeleteValue(HKCU, RunKey, RunValueName);
end;
#endif
