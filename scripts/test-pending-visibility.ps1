[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
$taskSuffix = 'pending-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path (Split-Path $taskExe) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"isolated-pending-test":true}') }
$taskHash = (Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{ passed = $false; hardwareMode = $false; checks = @(); evidenceDirectory = $taskDirectory }
function Command-Pending([string]$Command, [string]$Action = '', [string]$Value = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }; if ($Value) { $arguments.Value = $Value }; if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Save-Pending([string]$Name, $Reply) {
    $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8
}
function Check-Pending([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $taskResult.checks += $Name
}
function Wait-Pending([scriptblock]$Condition = { param($s) $true }) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 15) {
        if ($taskProcess.HasExited) { throw 'The scenario app exited unexpectedly.' }
        try {
            $last = Command-Pending 'status'
            $s = $last.status
            if (-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version -and (& $Condition $s)) { return $last }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge 15) { throw } }
        Start-Sleep -Milliseconds 50
    }
    throw 'Pending visibility controls did not settle.'
}
function Step-Pending([string]$Action) {
    $null = Command-Pending 'ui' 'receiver-step' $Action
    Wait-Pending
}
function ReceiverExcludedFromMerge-Pending($Snapshot, [string]$ReceiverId) {
    $peer = @($Snapshot.status.view.cards | Where-Object id -NE $ReceiverId)[0].id
    $null = Command-Pending 'ui' 'merge-open' '' $peer
    $dialog = Wait-Pending { param($s) $s.uiState.dialogKind -eq 'merge' }
    $excluded = @($dialog.status.deviceMerge.candidates | Where-Object targetId -EQ $ReceiverId).Count -eq 0
    $null = Command-Pending 'ui' 'dialog-cancel'
    $null = Wait-Pending { param($s) $null -eq $s.uiState.dialogKind }
    return $excluded
}
function Stop-Pending($Snapshot, [string]$Label) {
    $exit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Pending ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) "$Label exits normally"
    $events = @(Get-Content -LiteralPath $Snapshot.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opened = @($events | Where-Object kind -eq 'hid-open')
    $closed = @($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-Pending ($opened.Count -gt 0 -and @($opened | Where-Object { $_.data.channelId -notin $closed }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -eq 1) "$Label drains and closes every simulated HID channel"
}
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--test-pipe', $taskSuffix, '--scenario', 'receiver-pending') -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $start = Wait-Pending
    $taskId = @($start.status.session.state.devices | Where-Object name -eq 'MCHOSE G3 A')[0].id
    Check-Pending (@($start.status.view.cards | Where-Object id -eq $taskId).Count -eq 0 -and @($start.status.panel.cards | Where-Object id -eq $taskId).Count -eq 0) 'An initially unconfirmed receiver stays hidden on both surfaces'
    Check-Pending (@($start.status.session.state.devices | Where-Object { $_.id -eq $taskId -and $_.connection -eq 'Pending' -and -not $_.visible }).Count -eq 1) 'The hidden receiver keeps its original Pending evidence and remains monitored'
    Check-Pending (ReceiverExcludedFromMerge-Pending $start $taskId) 'A never-confirmed receiver does not become an actual merge-dialog candidate'
    Check-Pending (-not $start.status.scenario.usesRealHardware -and -not $start.status.scenario.savesPreferences) 'The lifecycle uses fictional I/O and in-memory preferences'
    for ($i = 0; $i -lt 3; $i++) { $start = Step-Pending 'wait' }
    $null = Command-Pending 'refresh'
    $start = Wait-Pending
    Check-Pending (@($start.status.view.cards | Where-Object id -eq $taskId).Count -eq 0) 'Repeated inactive replies and manual refresh do not admit a never-confirmed receiver'
    Save-Pending 'initial-hidden' $start
    Stop-Pending $start 'The initial hidden process'
    $taskProcess.Dispose()
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--test-pipe', $taskSuffix, '--scenario', 'receiver-pending') -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $restarted = Wait-Pending
    Check-Pending (@($restarted.status.session.state.devices | Where-Object { $_.id -eq $taskId -and $_.connection -eq 'Pending' -and -not $_.visible }).Count -eq 1 -and @($restarted.status.view.cards | Where-Object id -eq $taskId).Count -eq 0) 'A real process restart does not grant an inactive receiver a new visible window'
    Save-Pending 'restarted-hidden' $restarted
    $awakeError = Step-Pending 'wake-error'
    $device = @($awakeError.status.session.state.devices | Where-Object id -eq $taskId)[0]
    $card = @($awakeError.status.view.cards | Where-Object id -eq $taskId)[0]
    Check-Pending ($device.connection -eq 'Online' -and $device.visible -and $null -eq $device.batteryPercent -and $card.battery -eq '—' -and $card.readFailed) 'Confirmed activity admits the device even when its first battery read fails'
    Save-Pending 'first-active-battery-error' $awakeError
    $null = Command-Pending 'ui' 'rename-open' '' $taskId
    $null = Wait-Pending { param($s) $s.uiState.dialogKind -eq 'rename' }
    $null = Command-Pending 'ui' 'dialog-value' '测试 G3 优先设备'
    $null = Command-Pending 'ui' 'dialog-save'
    $null = Wait-Pending { param($s) $null -eq $s.uiState.dialogKind }
    $null = Command-Pending 'preferences' 'open'
    $null = Command-Pending 'preferences' 'add' '' $taskId
    $null = Wait-Pending { param($s) -not $s.preferences.dialog.isSaving -and @($s.preferences.priorities | Where-Object deviceId -eq $taskId).Count -eq 1 }
    $awake = Step-Pending 'wake'
    Check-Pending ($awake.status.tray.deviceId -eq $taskId -and $awake.status.tray.percent -eq 8) 'A later successful battery read supplies the already confirmed preferred device'
    $null = Step-Pending 'idle'
    for ($i = 0; $i -lt 32; $i++) { $before = Step-Pending 'wait' }
    Check-Pending (@($before.status.view.cards | Where-Object id -eq $taskId).Count -eq 1) 'A confirmed device remains visible through its grace and until just before the Pending deadline'
    Save-Pending 'before-deadline' $before
    $hidden = Step-Pending 'wait'
    $device = @($hidden.status.session.state.devices | Where-Object id -eq $taskId)[0]
    Check-Pending ($device.connection -eq 'Pending' -and -not $device.visible) 'After the Pending deadline the record stays Pending but becomes hidden'
    Check-Pending (@($hidden.status.view.cards | Where-Object id -eq $taskId).Count -eq 0 -and @($hidden.status.panel.cards | Where-Object id -eq $taskId).Count -eq 0) 'Main window and tray panel both remove the hidden row'
    Check-Pending (@($hidden.status.view.cards | Where-Object { $_.name -match '未提供电量' }).Count -eq 1) 'A connected device without a battery value stays visible'
    Check-Pending (@($hidden.status.preferences.dialog.rows | Where-Object { $_.deviceId -eq $taskId -and $_.status -match '暂无响应|No response' }).Count -eq 1) 'The saved priority keeps its unconfirmed status instead of being labeled disconnected'
    Check-Pending (@($hidden.status.preferences.priorities | Where-Object deviceId -eq $taskId).Count -eq 1) 'Hiding preserves the saved priority'
    Save-Pending 'hidden' $hidden
    $null = Command-Pending 'refresh'
    $refreshed = Wait-Pending
    Check-Pending (@($refreshed.status.view.cards | Where-Object id -eq $taskId).Count -eq 0) 'Manual refresh does not restart the visibility window'
    $null = Command-Pending 'preferences' 'remove' '' $taskId
    $removed = Wait-Pending { param($s) -not $s.preferences.dialog.isSaving }
    Check-Pending (@($removed.status.preferences.dialog.available | Where-Object deviceId -eq $taskId).Count -eq 0) 'A removed hidden priority does not appear among addable devices'
    $rejected = $false
    try { $null = Command-Pending 'preferences' 'add' '' $taskId } catch { $rejected = $true }
    Check-Pending $rejected 'The add action rejects hidden devices'
    $awake = Step-Pending 'wake'
    $device = @($awake.status.session.state.devices | Where-Object id -eq $taskId)[0]
    Check-Pending ($device.connection -eq 'Online' -and $device.visible -and $device.batteryPercent -eq 8) 'A real reader status-2 reply restores the same device with a new battery value'
    Check-Pending (@($awake.status.view.cards | Where-Object { $_.id -eq $taskId -and $_.name -eq '测试 G3 优先设备' }).Count -eq 1) 'The user name survives hiding and recovery'
    $null = Command-Pending 'preferences' 'add' '' $taskId
    $awake = Wait-Pending { param($s) -not $s.preferences.dialog.isSaving -and $s.tray.deviceId -eq $taskId }
    Check-Pending ($awake.status.tray.percent -eq 8) 'The recovered preferred device supplies the tray number'
    Save-Pending 'recovered' $awake
    $idle = Step-Pending 'idle'
    Check-Pending (@($idle.status.view.cards | Where-Object id -eq $taskId).Count -eq 1) 'Confirmed recovery resets the later inactivity window'
    for ($i = 0; $i -lt 33; $i++) { $hiddenAgain = Step-Pending 'wait' }
    Check-Pending (@($hiddenAgain.status.view.cards | Where-Object id -eq $taskId).Count -eq 0) 'A later full inactivity window hides the recovered receiver again'
    $awakeAgain = Step-Pending 'wake'
    Check-Pending ($awakeAgain.status.tray.deviceId -eq $taskId -and @($awakeAgain.status.preferences.priorities | Where-Object deviceId -eq $taskId).Count -eq 1) 'The unchanged priority automatically resumes after a second recovery'
    Save-Pending 'second-recovery' $awakeAgain
    Check-Pending ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'No scenario step changes on-disk settings'
    Stop-Pending $awakeAgain 'The confirmed lifecycle process'
    $taskProcess.Dispose()
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--test-pipe', $taskSuffix, '--scenario', 'receiver-pending') -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $restartedAgain = Wait-Pending
    Check-Pending (@($restartedAgain.status.view.cards | Where-Object id -eq $taskId).Count -eq 0 -and (ReceiverExcludedFromMerge-Pending $restartedAgain $taskId)) 'Activity in a previous process does not admit an inactive device in a new process'
    Check-Pending ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'Restart checks preserve the original settings bytes'
    Save-Pending 'restart-after-activity' $restartedAgain
    Stop-Pending $restartedAgain 'The final restarted process'
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { Save-Pending 'failed-state' (Command-Pending 'status') } catch { } }
    throw
} finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError = $_.Exception.Message } }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed = $taskResult.passed; checks = $taskResult.checks.Count; evidenceDirectory = $taskDirectory }
