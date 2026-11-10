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

[Setup]
AppId={{99051A29-4531-489B-8808-FB079EA80C2C}
AppName=Battery Manager
AppVersion={#AppVersion}
AppPublisher=Battery Manager contributors
AppPublisherURL=https://github.com/Aice-Chen/BatteryManager
AppSupportURL=https://github.com/Aice-Chen/BatteryManager/issues
AppUpdatesURL=https://github.com/Aice-Chen/BatteryManager/releases
DefaultDirName={localappdata}\Programs\BatteryManager
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
OutputBaseFilename=BatteryManager-{#AppVersion}-win-x64-setup
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#AppVersion}
SetupIconFile=..\..\src\BatteryManager.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\BatteryManager.App.exe
AppMutex=Local\BatteryManager.NewApp.2026
SetupMutex=Local\BatteryManager.Setup.2026
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

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"; Flags: unchecked

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Battery Manager"; Filename: "{app}\BatteryManager.App.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\BatteryManager.App.exe"; Description: "{cm:LaunchProgram,Battery Manager}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
english.StartupTask=Start Battery Manager when I sign in
chinesesimplified.StartupTask=登录 Windows 时启动 Battery Manager
english.CloseApp=Please exit Battery Manager from its tray menu and retry. Installation will not force it to close.
chinesesimplified.CloseApp=请先从托盘菜单退出 Battery Manager，再重试。安装程序不会强行关闭程序。
english.StartupFailed=The startup setting could not be saved. Change it later in Battery Manager settings.
chinesesimplified.StartupFailed=开机启动设置未能保存，请稍后在 Battery Manager 设置中修改。

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'BatteryManager';

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
  if RegQueryStringValue(HKCU, RunKey, RunValueName, Command) and IsBatteryManagerCommand(Command) then
    WizardSelectTasks('startup');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes('Local\BatteryManager.NewApp.2026') then Result := CustomMessage('CloseApp');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Command: String;
  Saved: Boolean;
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
