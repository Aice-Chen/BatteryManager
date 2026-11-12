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
#ifdef StartupTaskTest
  #ifndef IsolationTestId
    #error Startup task tests require an isolated installer
  #endif
  #define StartupTaskEnabled
#endif
#ifdef CleanupPauseTest
  #ifndef IsolationTestId
    #error Cleanup pause tests require an isolated installer
  #endif
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
  #define StartupTaskEnabled
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

#ifdef StartupTaskEnabled
[Tasks]
; Create the default in the task list itself. Inno applies /TASKS and /MERGETASKS afterwards.
; InitializeWizard runs before this list exists, so WizardSelectTasks there cannot set the default.
Name: "startup"; Description: "{code:StartupTaskDescription}"; Check: StartupRegistered
Name: "startup"; Description: "{code:StartupTaskDescription}"; Check: not StartupRegistered; Flags: unchecked
#endif

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\SHA256SUMS.txt"; DestName: "candidate-payload.sha256"; Flags: dontcopy

[Icons]
#ifdef IsolationTestId
Name: "{#IsolationRoot}\test-start-menu\Battery Manager"; Filename: "{app}\BatteryManager.App.exe"; WorkingDir: "{app}"
#else
Name: "{autoprograms}\Battery Manager"; Filename: "{app}\BatteryManager.App.exe"; WorkingDir: "{app}"
#endif

#ifndef IsolationTestId
[Run]
Filename: "{app}\BatteryManager.App.exe"; Description: "{cm:LaunchProgram,Battery Manager}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: LaunchAfterInteractiveInstall
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
const
#ifdef IsolationTestId
  RegistrationKey = 'Software\BatteryManager\InstallerTaskTests\{#IsolationTestId}\Uninstall';
#else
  RegistrationKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{99051A29-4531-489B-8808-FB079EA80C2C}_is1';
#endif
var
  HandoffDirectory: String;
  PreviousRegisteredDirectory: String;
  HandoffToken: String;
  ExistingTemporaryJournalHash: String;
  TemporaryJournalMalformed: Boolean;
  HandoffConfirmedExited: Boolean;
  InstallationStarted: Boolean;
  AutoUpdate: Boolean;
  InstallCompleted: Boolean;
  LaunchFailed: Boolean;
  CleanupFailed: Boolean;
  PreviousFiles, PreviousHashes, IncomingFiles, IncomingHashes: TArrayOfString;

function LaunchAfterInteractiveInstall: Boolean;
begin
  Result := InstallCompleted and not AutoUpdate;
end;

function HandoffOpenProcess(DesiredAccess: LongWord; InheritHandle: Integer; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall setuponly';
function HandoffQueryImage(Process: THandle; Flags: LongWord; ImageName: String; var Size: LongWord): Integer;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall setuponly';
function HandoffWait(Process: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall setuponly';
function HandoffClose(Process: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall setuponly';
function HandoffOpenEvent(DesiredAccess: LongWord; InheritHandle: Integer; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall setuponly';
function HandoffSignal(Event: THandle): Integer;
  external 'SetEvent@kernel32.dll stdcall setuponly';

function RejectHandoff(MessageKey: String): Boolean;
begin
  Result := False;
  Log('Update handoff rejected: ' + MessageKey);
  SuppressibleMsgBox(CustomMessage(MessageKey), mbError, MB_OK, IDOK);
end;

function WaitForApplicationReady(Process: THandle): Boolean;
var
  Accepted, Ready, Cancelled: THandle;
  Prefix: String;
  Status: LongWord;
begin
  Result := False;
  Prefix := 'Local\BatteryManager.AutoUpdate.' + HandoffToken;
  Accepted := HandoffOpenEvent(2, 0, Prefix + '.Accepted');
  Ready := HandoffOpenEvent($00100000, 0, Prefix + '.Ready');
  Cancelled := HandoffOpenEvent($00100000, 0, Prefix + '.Cancel');
  try
    if (Accepted = 0) or (Ready = 0) or (Cancelled = 0) then begin RejectHandoff('HandoffInvalid'); Exit; end;
    if HandoffWait(Cancelled, 0) <> 258 then begin RejectHandoff('HandoffInvalid'); Exit; end;
    if HandoffSignal(Accepted) = 0 then begin RejectHandoff('HandoffQuery'); Exit; end;
    Log('Update handoff: accepted; awaiting application preparation.');
    while True do begin
      if HandoffWait(Cancelled, 0) <> 258 then begin RejectHandoff('HandoffInvalid'); Exit; end;
      Status := HandoffWait(Ready, 0);
      if Status = 0 then begin
        Log('Update handoff: preparation ready; starting process-exit timeout.');
        Result := True; Exit;
      end;
      if Status <> 258 then begin RejectHandoff('HandoffQuery'); Exit; end;
      if HandoffWait(Process, 0) <> 258 then begin RejectHandoff('HandoffQuery'); Exit; end;
      Sleep(100);
    end;
  finally
    if Accepted <> 0 then HandoffClose(Accepted);
    if Ready <> 0 then HandoffClose(Ready);
    if Cancelled <> 0 then HandoffClose(Cancelled);
  end;
end;

function InitializeSetup: Boolean;
var
  PidText, Directory, ImageName, AutoUpdateText: String;
  PidValue: Int64;
  I: Integer;
  Process: THandle;
  ImageSize, WaitResult, Error: LongWord;
begin
  Result := True;
  if RegQueryStringValue(HKCU64, RegistrationKey, 'InstallLocation', Directory) then
    if (Length(Directory) >= 3) and (Directory[2] = ':') and (Directory[3] = '\') then
      PreviousRegisteredDirectory := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
  PidText := ExpandConstant('{param:WAITPID|not-specified}');
  AutoUpdateText := ExpandConstant('{param:AUTOUPDATE|not-specified}');
  AutoUpdate := AutoUpdateText = '1';
  HandoffToken := ExpandConstant('{param:WAITTOKEN|}');
  if ((AutoUpdateText <> 'not-specified') and not AutoUpdate) or
    (AutoUpdate and (PidText = 'not-specified')) then begin Result := RejectHandoff('HandoffInvalid'); Exit; end;
  if AutoUpdate then begin
    if Length(HandoffToken) <> 32 then begin Result := RejectHandoff('HandoffInvalid'); Exit; end;
    for I := 1 to Length(HandoffToken) do
      if Pos(HandoffToken[I], '0123456789abcdef') = 0 then begin Result := RejectHandoff('HandoffInvalid'); Exit; end;
  end else if HandoffToken <> '' then begin Result := RejectHandoff('HandoffInvalid'); Exit; end;
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
    if (Error = 87) and not AutoUpdate then begin
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
      if (WaitResult = 0) and not AutoUpdate then begin
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
    if AutoUpdate then
      if not WaitForApplicationReady(Process) then Exit;
    // Keep this exact process handle throughout the wait so PID reuse cannot retarget the operation.
    WaitResult := HandoffWait(Process, {#HandoffWaitMilliseconds});
    if WaitResult = 0 then begin
      Log('Update handoff: verified process exited.');
      HandoffConfirmedExited := AutoUpdate;
      Result := True;
    end else if WaitResult = 258 then RejectHandoff('HandoffTimeout')
    else RejectHandoff('HandoffQuery');
  finally
    HandoffClose(Process);
  end;
end;

#ifdef StartupTaskEnabled
const
#ifdef StartupTaskTest
  RunKey = 'Software\BatteryManager\InstallerTaskTests\{#IsolationTestId}\Run';
  ApprovalKey = 'Software\BatteryManager\InstallerTaskTests\{#IsolationTestId}\StartupApproved';
#else
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovalKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
#endif
  RunValueName = 'BatteryManager';
var
  StartupDefaultRead: Boolean;
  StartupDefaultSelected: Boolean;

// During installation StartupApproved is read-only. Exact-own uninstall removes its value.
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
  if (Ord(Data[1]) = 0) or (Ord(Data[1]) = 2) then Result := 1
  else if (Ord(Data[1]) = 1) or (Ord(Data[1]) = 3) then Result := 2;
end;

function StartupTaskDescription(Param: String): String;
var
  Approval: Integer;
begin
  Result := CustomMessage('StartupTask');
  Approval := StartupApproval;
  Log('Startup permission classification: ' + IntToStr(Approval));
  if Approval = 2 then Result := Result + CustomMessage('StartupBlockedSuffix')
  else if Approval = 3 then Result := Result + CustomMessage('StartupUnknownSuffix');
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
  Result := CompareText(CommandExecutable(Command), ExpandConstant('{app}\BatteryManager.App.exe')) = 0;
  if not Result and (PreviousRegisteredDirectory <> '') then
    Result := CompareText(CommandExecutable(Command), AddBackslash(PreviousRegisteredDirectory) + 'BatteryManager.App.exe') = 0;
end;

function StartupRegistered: Boolean;
var
  Command: String;
begin
  // Use the effective current Run setting, including changes made in the app after an install.
  // Checked means registered, not allowed by Windows. Preserve the registration on unattended upgrades.
  if not StartupDefaultRead then begin
    StartupDefaultRead := True;
    StartupDefaultSelected := RegQueryStringValue(HKCU, RunKey, RunValueName, Command) and IsBatteryManagerCommand(Command);
  end;
  Result := StartupDefaultSelected;
end;

#endif

function FileAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function ReplaceFileName(ExistingName, NewName: String; Flags: LongWord): Integer;
  external 'MoveFileExW@kernel32.dll stdcall setuponly';
function OpenPayloadForWrite(Name: String; DesiredAccess, ShareMode: LongWord; Security: NativeInt; Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall setuponly';

function CleanupJournal: String;
begin
  Result := ExpandConstant('{app}\.battery-manager-cleanup.sha256');
end;

function DataDirectory: String;
begin
#ifdef IsolationTestId
  Result := ExpandFileName('{#IsolationRoot}\user-data');
#else
  Result := ExpandConstant('{localappdata}\BatteryManager');
#endif
end;

function PathWithoutReparse(Path: String): Boolean;
var
  Attributes, Error: LongWord;
  Parent: String;
begin
  Result := False;
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  repeat
    Attributes := FileAttributes(Path);
    if Attributes = $FFFFFFFF then begin
      Error := DLLGetLastError;
      if (Error <> 2) and (Error <> 3) then Exit;
    end else if (Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then Exit;
    Parent := RemoveBackslashUnlessRoot(ExtractFileDir(Path));
    if CompareText(Path, Parent) = 0 then Break;
    Path := Parent;
  until Path = '';
  Result := True;
end;

function TreeWithoutReparse(Path: String): Boolean;
var
  Found: TFindRec;
  Child: String;
begin
  Result := False;
  if not PathWithoutReparse(Path) then Exit;
  if not DirExists(Path) then begin Result := not FileExists(Path); Exit; end;
  Result := True;
  if FindFirst(AddBackslash(Path) + '*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          if (Found.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then begin Result := False; Exit; end;
          Child := AddBackslash(Path) + Found.Name;
          if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            if not TreeWithoutReparse(Child) then begin Result := False; Exit; end;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function ValidPayloadPath(Path: String): Boolean;
var
  I, Start: Integer;
  Segment, Base: String;
begin
  Result := False;
  if (Path = '') or (Length(Path) > 240) then Exit;
  Start := 1;
  for I := 1 to Length(Path) + 1 do begin
    if I <= Length(Path) then
      if (Ord(Path[I]) < 32) or (Pos(Path[I], ':*?"<>|') <> 0) then Exit;
    if (I = Length(Path) + 1) or (Path[I] = '\') then begin
      Segment := Copy(Path, Start, I - Start);
      if (Segment = '') or (Segment = '.') or (Segment = '..') then Exit;
      if (Segment[Length(Segment)] = '.') or (Segment <> Trim(Segment)) then Exit;
      Base := Uppercase(ChangeFileExt(Segment, ''));
      if (Base = 'CON') or (Base = 'PRN') or (Base = 'AUX') or (Base = 'NUL') or
        ((Length(Base) = 4) and ((Copy(Base, 1, 3) = 'COM') or (Copy(Base, 1, 3) = 'LPT')) and (Base[4] >= '0') and (Base[4] <= '9')) then Exit;
      Start := I + 1;
    end;
  end;
  if (CompareText(Path, 'SHA256SUMS.txt') = 0) or (CompareText(Path, 'installed.json') = 0) or
    (CompareText(Copy(Path, 1, 25), '.battery-manager-cleanup.') = 0) or
    (CompareText(Copy(Path, 1, 5), 'unins') = 0) then Exit;
  Result := True;
end;

function ContainsPath(Paths: TArrayOfString; Path: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Paths) - 1 do
    if CompareText(Paths[I], Path) = 0 then begin Result := True; Exit; end;
end;

function CanReplacePayloadFile(Path: String): Boolean;
var Handle: THandle;
begin
  Result := False;
  if DirExists(Path) or not PathWithoutReparse(Path) then Exit;
  if not FileExists(Path) then begin Result := True; Exit; end;
  // Request the write/delete rights that replacement needs without changing the file.
  // Existing readers which deny write/delete cause refusal before the first extraction.
  Handle := OpenPayloadForWrite(Path, $40010000, 7, 0, 3, $80, 0);
  if Handle = -1 then Exit;
  HandoffClose(Handle);
  Result := True;
end;

function ReadPayloadManifest(FileName, Root: String; var Paths, Hashes: TArrayOfString): Boolean;
var
  Lines: TArrayOfString;
  I, J, Count: Integer;
  Hash, Path: String;
begin
  Result := False;
  SetArrayLength(Paths, 0); SetArrayLength(Hashes, 0);
  if not PathWithoutReparse(FileName) or not LoadStringsFromFile(FileName, Lines) then Exit;
  if (GetArrayLength(Lines) = 0) or (GetArrayLength(Lines) > 20000) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do begin
    if (Lines[I] = '') and (I = GetArrayLength(Lines) - 1) then Continue;
    if (Length(Lines[I]) < 67) or (Copy(Lines[I], 65, 2) <> '  ') then Exit;
    Hash := Lowercase(Copy(Lines[I], 1, 64));
    for J := 1 to 64 do if Pos(Hash[J], '0123456789abcdef') = 0 then Exit;
    Path := Copy(Lines[I], 67, MaxInt);
    StringChangeEx(Path, '/', '\', True);
    if not ValidPayloadPath(Path) or ContainsPath(Paths, Path) then Exit;
    if not PathWithoutReparse(AddBackslash(Root) + Path) then Exit;
    Count := GetArrayLength(Paths);
    SetArrayLength(Paths, Count + 1); SetArrayLength(Hashes, Count + 1);
    Paths[Count] := Path; Hashes[Count] := Hash;
  end;
  Result := ContainsPath(Paths, 'BatteryManager.App.exe');
end;

function PreparePayloadCleanup: String;
var
  Root, Manifest, Path: String;
  I, J, Count, JournalIndex: Integer;
  PendingFiles, PendingHashes, TemporaryFiles, TemporaryHashes: TArrayOfString;
  Covered: Boolean;
  Found: TFindRec;
begin
  Result := 'Payload cleanup validation failed. No installation files were changed.';
  Root := ExpandConstant('{app}');
  if not TreeWithoutReparse(Root) then Exit;
  ExtractTemporaryFile('candidate-payload.sha256');
  if not ReadPayloadManifest(ExpandConstant('{tmp}\candidate-payload.sha256'), Root, IncomingFiles, IncomingHashes) then Exit;
  Manifest := AddBackslash(Root) + 'SHA256SUMS.txt';
  SetArrayLength(PreviousFiles, 0); SetArrayLength(PreviousHashes, 0);
  ExistingTemporaryJournalHash := '';
  TemporaryJournalMalformed := False;
  if FileExists(Manifest) then begin
    if not ReadPayloadManifest(Manifest, Root, PreviousFiles, PreviousHashes) then Exit;
    for I := 0 to GetArrayLength(PreviousFiles) - 1 do
      if not ContainsPath(IncomingFiles, PreviousFiles[I]) then begin
        Path := AddBackslash(Root) + PreviousFiles[I];
        if DirExists(Path) then Exit;
        if FileExists(Path) then
          if CompareText(GetSHA256OfFile(Path), PreviousHashes[I]) <> 0 then Exit;
      end;
  end else if FileExists(AddBackslash(Root) + 'BatteryManager.App.exe') then Exit;
  for JournalIndex := 0 to 1 do begin
    Path := CleanupJournal;
    if JournalIndex = 1 then Path := Path + '.tmp';
    if DirExists(Path) then Exit;
    if FileExists(Path) then begin
      if not ReadPayloadManifest(Path, Root, PendingFiles, PendingHashes) then begin
        // A temporary write precedes extraction. The validated current/main manifests remain the
        // authority after an interrupted write; preserve corrupt temporary bytes without parsing paths.
        if (JournalIndex = 0) or (GetArrayLength(PreviousFiles) = 0) then Exit;
        TemporaryJournalMalformed := True;
        ExistingTemporaryJournalHash := GetSHA256OfFile(Path);
      end else begin
        if JournalIndex = 1 then begin
          ExistingTemporaryJournalHash := GetSHA256OfFile(Path);
          TemporaryFiles := PendingFiles; TemporaryHashes := PendingHashes;
        end;
        // A prior install may have completed extraction but failed cleanup. Keep its old-only ownership.
        for I := 0 to GetArrayLength(PendingFiles) - 1 do
          if not ContainsPath(PreviousFiles, PendingFiles[I]) then begin
            Count := GetArrayLength(PreviousFiles);
            SetArrayLength(PreviousFiles, Count + 1); SetArrayLength(PreviousHashes, Count + 1);
            PreviousFiles[Count] := PendingFiles[I]; PreviousHashes[Count] := PendingHashes[I];
          end;
      end;
    end;
  end;
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do
    if not ContainsPath(IncomingFiles, PreviousFiles[I]) then begin
      Path := AddBackslash(Root) + PreviousFiles[I];
      if DirExists(Path) then Exit;
      if FileExists(Path) then
        if CompareText(GetSHA256OfFile(Path), PreviousHashes[I]) <> 0 then Exit;
      if (ExistingTemporaryJournalHash <> '') and not TemporaryJournalMalformed then begin
        // A valid interrupted write covers every old-only entry before it can replace the main journal.
        Covered := False;
        for J := 0 to GetArrayLength(TemporaryFiles) - 1 do
          if (CompareText(TemporaryFiles[J], PreviousFiles[I]) = 0) and
            (CompareText(TemporaryHashes[J], PreviousHashes[I]) = 0) then Covered := True;
        if not Covered then Exit;
      end;
      if not CanReplacePayloadFile(Path) then Exit;
    end;
  for I := 0 to GetArrayLength(IncomingFiles) - 1 do
    if not CanReplacePayloadFile(AddBackslash(Root) + IncomingFiles[I]) then Exit;
  if not CanReplacePayloadFile(Manifest) or not CanReplacePayloadFile(AddBackslash(Root) + 'installed.json') then Exit;
  if FindFirst(AddBackslash(Root) + 'unins*', Found) then begin
    try
      repeat
        if not CanReplacePayloadFile(AddBackslash(Root) + Found.Name) then Exit;
      until not FindNext(Found);
    finally FindClose(Found); end;
  end;
  Result := '';
end;

procedure PreserveInterruptedJournal;
var
  Directory, Destination: String;
  I: Integer;
begin
  Directory := AddBackslash(DataDirectory) + 'installer-recovery';
  if not TreeWithoutReparse(DataDirectory) or not PathWithoutReparse(Directory) or not ForceDirectories(Directory) then
    RaiseException('Could not create a safe cleanup recovery directory.');
  I := 0;
  repeat
    Destination := AddBackslash(Directory) + 'interrupted-' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '-' + IntToStr(I) + '.sha256';
    I := I + 1;
  until (not FileExists(Destination) and not DirExists(Destination)) or (I = 1000);
  if not PathWithoutReparse(Destination) or not FileCopy(CleanupJournal + '.tmp', Destination, True) then
    RaiseException('Could not preserve the interrupted cleanup journal.');
  // Copy+verify works when AppData and the installation are on different volumes. Never overwrite
  // an existing recovery file, and never remove the source before both copies match the saved bytes.
  if (CompareText(GetSHA256OfFile(Destination), ExistingTemporaryJournalHash) <> 0) or
    (CompareText(GetSHA256OfFile(CleanupJournal + '.tmp'), ExistingTemporaryJournalHash) <> 0) then
    RaiseException('Interrupted cleanup journal verification failed.');
  if not PathWithoutReparse(CleanupJournal + '.tmp') or not DeleteFile(CleanupJournal + '.tmp') then
    RaiseException('Could not remove the preserved temporary journal.');
  Log('Preserved interrupted cleanup journal: ' + Destination);
end;

procedure SaveCleanupJournal;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  if GetArrayLength(PreviousFiles) = 0 then Exit;
  if not PathWithoutReparse(CleanupJournal) then RaiseException('Unsafe cleanup journal path.');
  if ExistingTemporaryJournalHash <> '' then begin
    if not PathWithoutReparse(CleanupJournal + '.tmp') or
      (CompareText(GetSHA256OfFile(CleanupJournal + '.tmp'), ExistingTemporaryJournalHash) <> 0) then
      RaiseException('The recovered cleanup journal changed.');
    if TemporaryJournalMalformed then PreserveInterruptedJournal
    else begin
      // Promote the validated complete interrupted write before creating a new one; an interruption
      // in either subsequent write/rename still leaves an ownership record for every old-only file.
      if ReplaceFileName(CleanupJournal + '.tmp', CleanupJournal, 9) = 0 then
        RaiseException('Could not recover the previous payload manifest.');
    end;
  end;
  SetArrayLength(Lines, GetArrayLength(PreviousFiles));
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do Lines[I] := PreviousHashes[I] + '  ' + PreviousFiles[I];
  if FileExists(CleanupJournal + '.tmp') or not SaveStringsToFile(CleanupJournal + '.tmp', Lines, False) then
    RaiseException('Could not save the previous payload manifest.');
  // Replace atomically on the same volume. Never discard the last journal before its replacement exists.
  if ReplaceFileName(CleanupJournal + '.tmp', CleanupJournal, 9) = 0 then
    RaiseException('Could not commit the previous payload manifest.');
end;

procedure RemoveObsoletePayload;
var
  I: Integer;
  Root, Path, Parent: String;
begin
  Root := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  // Validate the entire manifest before deleting its first file. Recheck each path at the operation.
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do
    if not ContainsPath(IncomingFiles, PreviousFiles[I]) then begin
      Path := AddBackslash(Root) + PreviousFiles[I];
      if not PathWithoutReparse(Path) then RaiseException('Unsafe obsolete payload path: ' + Path);
      if FileExists(Path) then begin
        if CompareText(GetSHA256OfFile(Path), PreviousHashes[I]) <> 0 then RaiseException('Changed obsolete payload: ' + Path);
        if not DeleteFile(Path) then RaiseException('Could not remove obsolete payload: ' + Path);
        Log('Removed obsolete payload: ' + Path);
      end;
      Parent := ExtractFileDir(Path);
      while CompareText(Parent, Root) <> 0 do begin
        if not PathWithoutReparse(Parent) then RaiseException('Unsafe obsolete directory: ' + Parent);
        if not RemoveDir(Parent) then Break; // An unrelated file keeps its directory.
        Parent := ExtractFileDir(Parent);
      end;
    end;
  if FileExists(CleanupJournal) then
    if not DeleteFile(CleanupJournal) then RaiseException('Could not remove the completed cleanup journal.');
end;

#ifdef CleanupPauseTest
procedure PauseBeforeCleanupForTest;
var Ready, ContinueEvent: THandle;
begin
  Ready := HandoffOpenEvent(2, 0, 'Local\BatteryManager.CleanupTest.{#IsolationTestId}.Ready');
  if Ready = 0 then Exit;
  ContinueEvent := HandoffOpenEvent($00100000, 0, 'Local\BatteryManager.CleanupTest.{#IsolationTestId}.Continue');
  try
    if ContinueEvent = 0 then RaiseException('Isolated cleanup continuation event is missing.');
    if HandoffSignal(Ready) = 0 then RaiseException('Isolated cleanup ready event failed.');
    if HandoffWait(ContinueEvent, 10000) <> 0 then RaiseException('Isolated cleanup continuation timed out.');
  finally
    HandoffClose(Ready);
    if ContinueEvent <> 0 then HandoffClose(ContinueEvent);
  end;
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
  if Result = '' then Result := PreparePayloadCleanup;
end;

#ifdef StartupTaskEnabled
procedure SaveStartup;
var
  Command: String;
  Saved: Boolean;
  Notice: String;
begin
  Saved := True;
  if WizardIsTaskSelected('startup') then begin
    // A same-name entry owned by another program or using an unknown value type is not ours to replace.
    if RegValueExists(HKCU, RunKey, RunValueName) and
      (not RegQueryStringValue(HKCU, RunKey, RunValueName, Command) or not IsBatteryManagerCommand(Command)) then begin
      Saved := False;
      Log('Startup registration preserved: existing Run value is not recognized as Battery Manager.');
    end else
      Saved := RegWriteStringValue(HKCU, RunKey, RunValueName, '"' + ExpandConstant('{app}\BatteryManager.App.exe') + '"');
  end
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
#endif

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssInstall then begin
    InstallationStarted := True;
    SaveCleanupJournal;
  end;
  if CurStep = ssPostInstall then begin
    try
#ifdef CleanupPauseTest
      PauseBeforeCleanupForTest;
#endif
      RemoveObsoletePayload;
    except
      CleanupFailed := True;
      Log('Payload cleanup failed: ' + GetExceptionMessage);
      SuppressibleMsgBox('The new files were installed, but old-version cleanup failed. Exit code 20. Close programs using the installation directory and run this installer again.', mbError, MB_OK, IDOK);
    end;
    if CleanupFailed then Exit;
#ifdef StartupTaskEnabled
    SaveStartup;
#endif
    InstallCompleted := True;
  end;
  if (CurStep = ssDone) and InstallCompleted and AutoUpdate then begin
    if not Exec(ExpandConstant('{app}\BatteryManager.App.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code) then begin
      LaunchFailed := True;
      Log('Updated application could not be started: ' + IntToStr(Code));
      SuppressibleMsgBox('The update was installed, but Battery Manager could not be started. Start it from the Start menu.', mbError, MB_OK, IDOK);
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if LaunchFailed then Result := 21;
  if CleanupFailed then Result := 20;
end;

function OtherRegisteredInstallationAt(Directory: String): Boolean;
var RegisteredDirectory: String;
begin
  Result := False;
  if not RegKeyExists(HKCU64, RegistrationKey) then Exit;
  // An unreadable/unknown registration is protected as well.
  Result := True;
  if not RegQueryStringValue(HKCU64, RegistrationKey, 'InstallLocation', RegisteredDirectory) or (RegisteredDirectory = '') then Exit;
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(RegisteredDirectory)), RemoveBackslashUnlessRoot(Directory)) <> 0;
end;

function OtherRegisteredInstallation: Boolean;
begin
  Result := OtherRegisteredInstallationAt(ExpandConstant('{app}'));
end;

procedure DeinitializeSetup;
var Code: Integer;
begin
  // A late preflight refusal has changed no payload. Recover the verified original after its
  // orderly exit; cancellation before Ready, timeouts and failures after ssInstall never take this path.
  if not AutoUpdate or not HandoffConfirmedExited or InstallationStarted then Exit;
  if (HandoffDirectory = '') or OtherRegisteredInstallationAt(HandoffDirectory) or not TreeWithoutReparse(HandoffDirectory) or
    not FileExists(AddBackslash(HandoffDirectory) + 'BatteryManager.App.exe') or CheckForMutexes('{#AppMutexName}') then Exit;
  if Exec(AddBackslash(HandoffDirectory) + 'BatteryManager.App.exe', '', HandoffDirectory, SW_SHOWNORMAL, ewNoWait, Code) then
    Log('Update preflight refused; restarted the unchanged original application.')
  else Log('Update preflight refused; original restart failed: ' + IntToStr(Code));
end;

function SafeCleanupJournals: Boolean;
var
  Path: String;
  I: Integer;
begin
  Result := False;
  for I := 0 to 1 do begin
    Path := CleanupJournal;
    if I = 1 then Path := Path + '.tmp';
    if DirExists(Path) or not PathWithoutReparse(Path) then Exit;
    // Uninstall removes only these two fixed app-owned files, never paths read from their contents.
    // A truncated journal therefore must not block removal of an otherwise safe installation.
  end;
  Result := True;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  if CheckForMutexes('{#AppMutexName}') then begin
    SuppressibleMsgBox(CustomMessage('CloseApp'), mbError, MB_OK, IDOK); Exit;
  end;
  if OtherRegisteredInstallation then begin
    SuppressibleMsgBox('Another Battery Manager installation owns the current registration. Uninstall that copy from Windows Settings.', mbError, MB_OK, IDOK); Exit;
  end;
  if not TreeWithoutReparse(ExpandConstant('{app}')) or not SafeCleanupJournals then begin
    SuppressibleMsgBox('Uninstall refused an unsafe installation directory.', mbError, MB_OK, IDOK); Exit;
  end;
  if not TreeWithoutReparse(DataDirectory) then begin
    SuppressibleMsgBox('Uninstall refused an unsafe application data directory.', mbError, MB_OK, IDOK); Exit;
  end;
  Result := True;
end;

procedure RemoveDataTree(Path: String);
var
  Found: TFindRec;
  Child: String;
begin
  if not TreeWithoutReparse(Path) then RaiseException('Unsafe application data directory: ' + Path);
  if not DirExists(Path) then Exit;
  if FindFirst(AddBackslash(Path) + '*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          Child := AddBackslash(Path) + Found.Name;
          if not PathWithoutReparse(Child) then RaiseException('Unsafe application data path: ' + Child);
          if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then RemoveDataTree(Child)
          else if not DeleteFile(Child) then RaiseException('Could not remove application data: ' + Child);
        end;
      until not FindNext(Found);
    finally FindClose(Found); end;
  end;
  if not RemoveDir(Path) then RaiseException('Could not remove application data directory: ' + Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Journal: String;
  I: Integer;
#ifdef StartupTaskEnabled
  Command: String;
#endif
begin
  if CurUninstallStep <> usUninstall then Exit;
  // Run after the final app mutex check and before Inno removes the uninstaller or payload.
  if not TreeWithoutReparse(ExpandConstant('{app}')) then RaiseException('Unsafe installation directory.');
  if not SafeCleanupJournals then RaiseException('Unsafe cleanup journal.');
  if OtherRegisteredInstallation then RaiseException('Another installation owns the current registration.');
  RemoveDataTree(DataDirectory);
  for I := 0 to 1 do begin
    Journal := CleanupJournal;
    if I = 1 then Journal := Journal + '.tmp';
    if FileExists(Journal) then
      if not DeleteFile(Journal) then RaiseException('Could not remove cleanup journal.');
  end;
#ifdef StartupTaskEnabled
  if (RegQueryStringValue(HKCU, RunKey, RunValueName, Command) and
      (CompareText(CommandExecutable(Command), ExpandConstant('{app}\BatteryManager.App.exe')) = 0)) or
    (not RegValueExists(HKCU, RunKey, RunValueName) and RegKeyExists(HKCU64, RegistrationKey)) then begin
      if RegValueExists(HKCU, ApprovalKey, RunValueName) then
        if not RegDeleteValue(HKCU, ApprovalKey, RunValueName) then RaiseException('Could not remove the owned startup permission.');
      if RegValueExists(HKCU, RunKey, RunValueName) then
        if not RegDeleteValue(HKCU, RunKey, RunValueName) then RaiseException('Could not remove the owned startup entry.');
  end;
#endif
end;
