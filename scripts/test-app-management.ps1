[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
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
$taskSettings = File-Management 'settings.json'
$taskManagement = File-Management 'management.json'
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $initial = Wait-Management
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
    $null = Ui-Management 'management-check'
    $none = Wait-Management { param($s) $s.management.updatePhase -eq 'UpToDate' }
    Check-Management (-not $none.status.management.downloadVisible) 'No newer stable release offers no download'
    $null = Ui-Management 'management-network' 'error'
    $null = Ui-Management 'management-check'
    $failed = Wait-Management { param($s) $s.management.updatePhase -eq 'Failed' }
    Check-Management ($failed.status.management.checkEnabled -and $failed.status.management.error -match '模拟网络') 'A failed check remains visible and can be retried'
    $null = Ui-Management 'management-network' 'available'
    $null = Ui-Management 'management-check'
    $available = Wait-Management { param($s) $s.management.updatePhase -eq 'Available' }
    Check-Management ($available.status.management.downloadVisible -and -not $available.status.management.installVisible -and -not $available.status.management.error) 'A successful retry offers download and clears the old error'
    $null = Ui-Management 'management-download-start'
    $running = Wait-Management { param($s) $s.management.updatePhase -eq 'Downloading' }
    Check-Management ($running.status.management.busy -and -not $running.status.management.checkEnabled) 'Downloading disables a conflicting check'
    $null = Ui-Management 'management-cancel'
    $canceled = Wait-Management { param($s) $s.management.updatePhase -eq 'Canceled' }
    Check-Management (-not $canceled.status.management.installVisible -and $canceled.status.management.downloadVisible) 'Canceled download cannot be installed and can be retried'
    $null = Ui-Management 'management-download'
    $downloaded = Wait-Management { param($s) $s.management.updatePhase -eq 'Downloaded' }
    Check-Management ($downloaded.status.management.installVisible -and -not $downloaded.status.management.downloadVisible) 'A completed verified download offers installation'
    $null = Ui-Management 'management-install-open'
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
