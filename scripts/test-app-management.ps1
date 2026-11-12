[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue | Where-Object Path -eq $taskExe).Count) { throw 'The isolated test executable is already running.' }
$taskOtherProcesses = @(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue | Select-Object Id, StartTime, Path)
$taskSuffix = 'management-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{ passed = $false; checks = @(); scope = 'Actual App controls and lifecycle, fictional hardware/network/registry/settings/installer'; evidenceDirectory = $taskDirectory }
function Command-Management([string]$Command, [string]$Action = '', [string]$Value = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }; if ($Value) { $arguments.Value = $Value }
    if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Ui-Management([string]$Action, [string]$Value = '') { Command-Management 'ui' $Action $Value }
function Save-Management([string]$Name, $Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8 }
function Check-Management([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Wait-Management([scriptblock]$Condition = { param($s) $true }) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 20) {
        if ($taskProcess.HasExited) { throw 'The scenario app exited unexpectedly.' }
        try {
            $reply = Command-Management 'status'; $s = $reply.status
            if (-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and (& $Condition $s)) { return $reply }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge 20) { throw } }
        Start-Sleep -Milliseconds 50
    }
    throw 'Management state did not settle.'
}
function File-Management([string]$Name) {
    $path = Join-Path (Split-Path $taskExe) $Name
    if (Test-Path -LiteralPath $path) { return (Get-FileHash -LiteralPath $path).Hash }
    return 'missing'
}
function Restart-Management {
    if ($null -ne $script:taskProcess) {
        if (-not $script:taskProcess.HasExited) {
            $null = Command-Management 'quit'
            if (-not $script:taskProcess.WaitForExit(15000)) { throw 'The isolated scenario did not drain.' }
        }
        $script:taskProcess.Dispose()
    }
    $script:taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    return Wait-Management
}
$taskSettings = File-Management 'settings.json'
$taskManagement = File-Management 'management.json'
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix, '--background') -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $initial = Wait-Management
    Check-Management (-not $initial.status.uiState.windowVisible -and $initial.status.tray.available) 'Background startup keeps the main window hidden with an available tray icon'
    $helper = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix, '--background') -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    try { Check-Management ($helper.WaitForExit(10000) -and $helper.ExitCode -eq 0 -and -not (Wait-Management).status.uiState.windowVisible) 'A second background instance exits without showing the existing window' }
    finally { $helper.Dispose() }
    $helper = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    try {
        if (-not $helper.WaitForExit(10000) -or $helper.ExitCode -ne 0) { throw 'The normal activation helper failed.' }
        $visible = Wait-Management { param($s) $s.uiState.windowVisible }
        Check-Management $visible.status.uiState.windowVisible 'A normal second instance shows the existing background window'
    } finally { $helper.Dispose() }
    Save-Management 'background-startup' $initial
    Check-Management ($initial.status.management.simulated -and -not $initial.status.scenario.usesRealHardware) 'Management scenario replaces hardware, HTTP, registry and settings boundaries'
    Check-Management ($initial.status.management.updatePhase -eq 'Idle' -and $initial.status.management.autoCheck) 'Scenario startup performs no automatic network check'
    $null = Command-Management 'preferences' 'open'
    $opened = Wait-Management { param($s) $s.view.page -eq 'settings' }
    Check-Management $opened.status.management.checkEnabled 'Settings exposes manual update check'
    $null = Ui-Management 'management-auto' 'off'
    $changed = Wait-Management { param($s) -not $s.management.autoCheck }
    Check-Management (-not $changed.status.management.autoCheck) 'Automatic checking can be disabled independently of device settings'
    $null = Ui-Management 'management-auto' 'on'
    $null = Wait-Management { param($s) $s.management.autoCheck }
    Check-Management $true 'Automatic checking can be enabled again'
    $null = Ui-Management 'management-startup' 'on'
    $enabled = Wait-Management { param($s) $s.management.startupEnabled }
    Check-Management ($enabled.status.management.startupStatus -eq 'Enabled') 'Enabling startup is reflected by the registry service result'
    $null = Ui-Management 'management-block-startup'
    $blocked = Wait-Management { param($s) $s.management.startupStatus -eq 'BlockedByWindows' }
    Check-Management (-not $blocked.status.management.startupEnabled) 'Windows-disabled registration is not reported as enabled'
    $null = Ui-Management 'management-startup' 'off'
    $removed = Wait-Management { param($s) $s.management.startupStatus -eq 'Disabled' }
    Check-Management (-not $removed.status.management.startupEnabled) 'A Windows-disabled registration can still be removed'
    $null = Ui-Management 'management-network' 'none'
    $null = Ui-Management 'management-primary'
    $none = Wait-Management { param($s) $s.management.updatePhase -eq 'UpToDate' }
    Check-Management (-not $none.status.management.downloadVisible) 'No newer stable release offers no download'
    $null = Ui-Management 'management-network' 'error'
    $null = Ui-Management 'management-primary'
    $failed = Wait-Management { param($s) $s.management.updatePhase -eq 'Failed' }
    Check-Management ($failed.status.management.checkEnabled -and $failed.status.management.error -match '模拟网络') 'A failed check remains visible and can be retried'
    Check-Management ($failed.status.management.primaryActionEnabled -and $failed.status.management.primaryActionText -in @('重试', 'Retry')) 'The real update button presents an enabled retry after a failed check'
    $null = Ui-Management 'management-network' 'available'
    $null = Ui-Management 'management-primary'
    $available = Wait-Management { param($s) $s.management.updatePhase -eq 'Available' }
    Check-Management ($available.status.management.downloadVisible -and -not $available.status.management.installVisible -and -not $available.status.management.error) 'A successful retry offers download and clears the old error'
    Check-Management ($available.status.management.primaryActionEnabled -and $available.status.management.primaryActionText -in @('下载', 'Download')) 'The real update button presents download for the discovered version'
    $null = Ui-Management 'management-primary'
    $running = Wait-Management { param($s) $s.management.updatePhase -eq 'Downloading' }
    Check-Management ($running.status.management.busy -and -not $running.status.management.checkEnabled) 'Downloading disables a conflicting check'
    Check-Management ($running.status.management.primaryActionEnabled -and $running.status.management.primaryActionText -in @('取消', 'Cancel')) 'The real update button offers effective cancellation during download'
    $null = Ui-Management 'management-primary'
    $canceled = Wait-Management { param($s) $s.management.updatePhase -eq 'Canceled' }
    Check-Management (-not $canceled.status.management.installVisible -and $canceled.status.management.downloadVisible) 'Canceled download cannot be installed and can be retried'
    $null = Ui-Management 'management-primary'
    $downloaded = Wait-Management { param($s) $s.management.updatePhase -eq 'Downloaded' }
    Check-Management ($downloaded.status.management.installVisible -and -not $downloaded.status.management.downloadVisible) 'A completed verified download offers installation'
    Check-Management ($downloaded.status.management.primaryActionEnabled -and $downloaded.status.management.primaryActionText -in @('安装', 'Install')) 'The real update button offers installation only after verified download'
    $null = Ui-Management 'management-primary'
    $null = Wait-Management { param($s) $s.management.confirmationOpen }
    Check-Management (-not $taskProcess.HasExited) 'Installation confirmation keeps the app running'
    $null = Ui-Management 'management-install-cancel'
    $null = Wait-Management { param($s) -not $s.management.confirmationOpen }
    Check-Management (-not $taskProcess.HasExited) 'Canceling installation confirmation preserves the running app and download'
    $null = Ui-Management 'priority-save-test' 'hold-fail'
    $choice = (Wait-Management).status.preferences.dialog.available[0].deviceId
    $null = Command-Management 'preferences' 'add' -DeviceId $choice
    $null = Wait-Management { param($s) $s.preferences.dialog.isSaving }
    $null = Ui-Management 'management-install-start'
    $preparing = Wait-Management { param($s) $s.uiState.preparingInstallation }
    Check-Management (-not $preparing.status.uiState.preferencesEnabled -and -not $preparing.status.uiState.appearanceEnabled -and -not $preparing.status.uiState.backEnabled) 'Installation preparation freezes new device-setting edits while accepted saves drain'
    $null = Ui-Management 'priority-save-test' 'release'
    $saveFailed = Wait-Management { param($s) -not $s.uiState.preparingInstallation -and $s.uiState.settingsError -match '停止安装' }
    Check-Management ($saveFailed.status.uiState.preferencesEnabled -and $saveFailed.status.uiState.appearanceEnabled -and $saveFailed.status.uiState.backEnabled -and -not $taskProcess.HasExited) 'Failed settings save cancels installation, displays its error and restores editing'
    Check-Management ($saveFailed.status.management.installVisible -and $saveFailed.status.preferences.priorities.Count -eq 0) 'Save failure preserves the verified download and last successfully saved settings'
    Save-Management 'save-failure-prevents-install' $saveFailed
    foreach ($mode in @('dark', 'light')) {
        $null = Ui-Management 'system-theme' ($mode + ',' + $mode)
        $null = Ui-Management 'settings-scroll' 'bottom'
        Start-Sleep -Milliseconds 250
        $null = Ui-Management 'capture-main-native' ('management-' + $mode + '.png')
    }
    Save-Management 'ready-to-install' (Wait-Management)
    $null = Ui-Management 'management-install-start'
    Check-Management ($taskProcess.WaitForExit(15000) -and $taskProcess.ExitCode -eq 0) 'Confirmed simulated installation completes normal shutdown'
    $events = @(Get-Content -LiteralPath $downloaded.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $kinds = @($events | ForEach-Object kind)
    $stopped = [Array]::IndexOf($kinds, 'session-stopped'); $launch = [Array]::IndexOf($kinds, 'installer-launch')
    Check-Management ($stopped -ge 0 -and $launch -gt $stopped -and $events[$launch].data.simulated) 'Installation handoff follows physical session drain and does not execute an installer in scenario mode'
    $opens = @($events | Where-Object kind -eq 'hid-open')
    $closes = @($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-Management ($opens.Count -gt 0 -and @($opens | Where-Object { $_.data.channelId -notin $closes }).Count -eq 0) 'All fictional native HID channels close before exit'
    Check-Management ((File-Management 'settings.json') -ceq $taskSettings -and (File-Management 'management.json') -ceq $taskManagement) 'Scenario management leaves disk settings unchanged'
    $taskProcess.Dispose()
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $null = Wait-Management
    $null = Command-Management 'preferences' 'open'
    $null = Ui-Management 'management-check'
    $null = Ui-Management 'management-download'
    $null = Ui-Management 'priority-save-test' 'hold'
    $choice = (Wait-Management).status.preferences.dialog.available[0].deviceId
    $null = Command-Management 'preferences' 'add' -DeviceId $choice
    $null = Wait-Management { param($s) $s.preferences.dialog.isSaving }
    $null = Ui-Management 'management-install-start'
    $preflight = Wait-Management { param($s) $s.uiState.preparingInstallation }
    $null = Command-Management 'quit'
    Check-Management ($taskProcess.WaitForExit(15000) -and $taskProcess.ExitCode -eq 0) 'Ordinary exit during installation preparation drains the accepted save without deadlock'
    $events = @(Get-Content -LiteralPath $preflight.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    Check-Management (@($events | Where-Object kind -eq 'installer-launch').Count -eq 0 -and @($events | Where-Object kind -eq 'session-stopped').Count -eq 1) 'Ordinary exit supersedes a pending installation and never launches an installer'
    Check-Management (@($events | Where-Object kind -eq 'priority-save-applied').Count -eq 1) 'A previously accepted settings write finishes before ordinary exit'

    $null = Restart-Management
    $null = Ui-Management 'management-network' 'error'
    $null = Ui-Management 'management-auto-run'
    $autoFailed = Wait-Management { param($s) $s.management.updatePhase -eq 'Failed' }
    Check-Management ($autoFailed.status.view.page -eq 'devices' -and $autoFailed.status.management.error -match '模拟网络' -and -not $taskProcess.HasExited) 'Automatic checking runs outside settings and leaves the app running on network failure'
    $null = Ui-Management 'management-network' 'available'
    $null = Ui-Management 'management-auto-run'
    Check-Management ((Wait-Management).status.management.updatePhase -eq 'Failed') 'An automatic failure is throttled instead of repeatedly retrying'
    Save-Management 'automatic-network-failure' $autoFailed

    $null = Restart-Management
    $null = Ui-Management 'management-auto' 'off'
    $null = Ui-Management 'management-auto-run'
    Check-Management ((Wait-Management).status.management.updatePhase -eq 'Idle') 'Disabled automatic updates perform no check or installation'
    $null = Command-Management 'preferences' 'open'
    $null = Ui-Management 'management-check'
    $manualOnly = Wait-Management { param($s) $s.management.updatePhase -eq 'Available' }
    Check-Management ($manualOnly.status.management.downloadVisible -and -not $manualOnly.status.management.busy) 'Manual checking remains an explicit download flow when automatic updates are disabled'

    $null = Restart-Management
    $null = Ui-Management 'management-auto-run-start'
    $null = Wait-Management { param($s) $s.management.updatePhase -eq 'Downloading' }
    $null = Ui-Management 'management-auto-toggle' 'off'
    $autoCanceled = Wait-Management { param($s) $s.management.updatePhase -eq 'Canceled' }
    Check-Management (-not $taskProcess.HasExited -and -not $autoCanceled.status.management.autoCheck -and -not $autoCanceled.status.management.installVisible) 'Turning off automatic updates cancels an active download without installation'
    Save-Management 'automatic-download-canceled' $autoCanceled

    $null = Restart-Management
    $null = Command-Management 'preferences' 'open'
    $null = Ui-Management 'priority-save-test' 'hold'
    $choice = (Wait-Management).status.preferences.dialog.available[0].deviceId
    $null = Command-Management 'preferences' 'add' -DeviceId $choice
    $null = Wait-Management { param($s) $s.preferences.dialog.isSaving }
    $null = Ui-Management 'management-auto-run-start'
    $autoPreparing = Wait-Management { param($s) $s.uiState.preparingInstallation }
    Check-Management ($autoPreparing.status.management.automaticControlEnabled -and $autoPreparing.status.management.cancelControlEnabled -and -not $autoPreparing.status.uiState.preferencesEnabled) 'Automatic preflight freezes device edits while keeping effective update cancellation controls enabled'
    $null = Ui-Management 'management-auto-toggle' 'off'
    $null = Wait-Management { param($s) -not $s.management.autoCheck }
    $null = Ui-Management 'priority-save-test' 'release'
    $preflightCanceled = Wait-Management { param($s) -not $s.uiState.preparingInstallation -and $s.management.updatePhase -eq 'Canceled' }
    Check-Management ($preflightCanceled.status.uiState.preferencesEnabled -and -not $taskProcess.HasExited) 'Disabling automatic updates during accepted-save drain prevents commit and restores editing'
    Save-Management 'automatic-preflight-canceled' $preflightCanceled

    $automatic = Restart-Management
    Check-Management ($automatic.status.view.page -eq 'devices') 'Automatic success starts from the normal device page'
    $null = Ui-Management 'window-hide'
    $null = Ui-Management 'management-auto-loop-start'
    Check-Management ($taskProcess.WaitForExit(15000) -and $taskProcess.ExitCode -eq 0) 'The actual periodic automatic loop downloads and completes simulated installation while hidden without self-await deadlock'
    $events = @(Get-Content -LiteralPath $automatic.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $kinds = @($events | ForEach-Object kind)
    $stopped = [Array]::IndexOf($kinds, 'session-stopped'); $launch = [Array]::IndexOf($kinds, 'installer-launch')
    Check-Management ($stopped -ge 0 -and $launch -gt $stopped -and $events[$launch].data.simulated) 'Automatic simulated installation follows normal physical session drain'
    Save-Management 'automatic-hidden-success-events' $events
    Check-Management ((File-Management 'settings.json') -ceq $taskSettings -and (File-Management 'management.json') -ceq $taskManagement) 'Automatic scenarios also leave disk settings unchanged'
    foreach ($other in $taskOtherProcesses) {
        $current = Get-Process -Id $other.Id -ErrorAction Stop
        Check-Management ($current.Path -eq $other.Path -and $current.StartTime -eq $other.StartTime) 'An existing ordinary app remains the same process while isolated scenarios run'
    }
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { Save-Management 'failed-state' (Command-Management 'status') } catch { } }
    throw
} finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) {
        try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError = $_.Exception.Message }
    }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    Save-Management 'result' $taskResult
}
[pscustomobject]@{ passed = $taskResult.passed; checks = $taskResult.checks.Count; evidenceDirectory = $taskDirectory }
