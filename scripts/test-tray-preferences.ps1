[CmdletBinding()]
param([string]$PreviewExe = '.artifacts/preview/BatteryManager.App.exe')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
function ProjectPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $taskRoot $Path }))
    if (-not $absolute.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside project: $absolute" }
    $cursor = $absolute
    while ($cursor.Length -gt $taskRoot.Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $cursor" }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $absolute
}
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Evidence([string]$Name, $Value) { [IO.File]::WriteAllText((ProjectPath (Join-Path $taskRun $Name)), ($Value | ConvertTo-Json -Depth 100)) }
function Command([string]$Name = 'status', [string]$Action = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Name; ConnectTimeoutMilliseconds = 1000 }
    if ($Action) { $arguments.Action = $Action }
    if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & (Join-Path $PSScriptRoot 'app-test-client.ps1') @arguments
}
function WaitStatus([scriptblock]$Predicate, [int]$Seconds = 40) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $last = 'Waiting for the app pipe.'
    while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
        if ($taskProcess.HasExited) { throw "Preview exited early: $($taskProcess.ExitCode)" }
        try {
            $response = Command
            if (& $Predicate $response.status) { return $response }
            $last = $response.status | ConvertTo-Json -Depth 5 -Compress -WarningAction SilentlyContinue
        } catch { $last = $_.Exception.Message }
        Start-Sleep -Milliseconds 150
    }
    throw "App state did not meet the condition: $last"
}
function Settled {
    WaitStatus { param($s) -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version }
}
function OpenSettings {
    $null = Command 'preferences' 'open'
    $opened = WaitStatus { param($s) $null -ne $s.preferences.dialog -and $s.preferences.dialog.isOpen } 10
    CheckDraftOrder $opened.status
    Check ($opened.status.preferences.dialog.canReorder -eq $opened.status.preferences.dialog.canSave) 'The actual reorder controls disagree with the draft editability.'
    $opened
}
function CloseSettings([string]$Action) {
    if($Action -ne 'save'){throw 'Priority edits now save immediately.'}
    $null = Command 'preferences' $Action
    $null = Command 'ui' 'back'
    WaitStatus { param($s) -not $s.preferences.dialog.isOpen } 10
}
function SaveSettled { $null=Command 'preferences' 'save'; WaitStatus {param($s) -not $s.preferences.dialog.isSaving} 10 }
function StartPreview {
    $script:taskSuffix = 'tray-' + [Guid]::NewGuid().ToString('N')
    $script:taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--test-pipe', $taskSuffix) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    $script:taskStarted = $taskProcess.StartTime.ToUniversalTime()
    Settled
}
function StopPreview {
    if ($null -eq $taskProcess) { return }
    if (-not $taskProcess.HasExited) {
        $owned = Get-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue
        Check ($null -ne $owned -and $owned.Path -eq $taskExe -and $owned.StartTime.ToUniversalTime() -eq $taskStarted) 'The tracked preview process no longer matches this test.'
        $null = Command 'quit'
        Check ($taskProcess.WaitForExit(30000)) 'Preview did not drain and exit within 30 seconds; it was left running.'
    }
    Check ($taskProcess.ExitCode -eq 0) "Preview exited with code $($taskProcess.ExitCode)."
    $taskProcess.Dispose()
    $script:taskProcess = $null
}
function CheckDraftOrder($Status) {
    $dialog = $Status.preferences.dialog
    Check ($null -ne $dialog) 'The preferences dialog is not open.'
    $actual = @($dialog.rows | ForEach-Object { $_.deviceId })
    $draft = @($dialog.draftOrder)
    Check (($actual -join '|') -ceq ($draft -join '|')) 'Actual list items differ from the draft priority order.'
}
function CheckPriorities($Status, [string[]]$Ids) {
    $actual = @($Status.preferences.priorities | ForEach-Object { $_.deviceId })
    Check (($actual -join '|') -ceq ($Ids -join '|')) 'Saved priority order differs from the chosen order.'
}
function CheckTray($Status, [string[]]$Ids) {
    $eligible = @($Status.session.state.devices | Where-Object { $_.connection -in @('Online', 'Recent') -and $null -ne $_.batteryPercent -and $_.freshness -in @('Current', 'Recent', 'Retained') })
    $expected = $null
    foreach ($id in $Ids) {
        $candidate = @($eligible | Where-Object { $_.id -ceq $id })
        if ($candidate.Count) { $expected = $candidate[0]; break }
    }
    Check $Status.tray.available 'The shell tray icon is unavailable.'
    if($null -eq $expected){
        if($eligible.Count -eq 0){Check ($null -eq $Status.tray.deviceId) 'The tray selected an unavailable offline preference.'; return}
        $fallback=@($eligible | Where-Object id -EQ $Status.tray.deviceId)
        Check ($fallback.Count -eq 1) 'Unavailable preferences did not fall back to a currently eligible device.'
        $expected=$fallback[0]
    }
    if ($null -ne $expected) {
        Check ($Status.tray.deviceId -ceq $expected.id) 'The real tray icon selected the wrong preferred device.'
        Check ($Status.tray.percent -eq $expected.batteryPercent) 'The real tray percentage differs from the selected device.'
        Check ($Status.tray.tooltip.Contains($expected.name)) 'The tray tooltip omits its selected device name.'
    }
}

$taskExe = ProjectPath $PreviewExe
Check (Test-Path -LiteralPath $taskExe) 'Publish the preview before running this test.'
foreach ($candidate in @(Get-Process -Name 'BatteryManager.App' -ErrorAction SilentlyContinue)) {
    throw "Battery Manager PID $($candidate.Id) is already running. Exit it before this hardware-mode test."
}
$taskRun = ProjectPath ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-tray-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($taskRun)
$taskSettings = ProjectPath (Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.json')
$taskOriginal = ProjectPath (Join-Path $taskRun 'original-settings.json')
$taskV1Backup = ProjectPath (Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.v1.bak')
$taskV1Original = ProjectPath (Join-Path $taskRun 'original-v1-backup.bin')
$taskV1Moved = $false
$taskHadSettings = Test-Path -LiteralPath $taskSettings
$taskOriginalMoved = $false
$taskOwnsSettings = $false
$taskProcess = $null
$taskLock = $null
$taskFailure = $null
$taskResult = [ordered]@{ startedAt = [DateTimeOffset]::UtcNow; passed = $false; originalSettingsRestored = $false; cleanupPending = $false; error = $null;
    saveInProgressObserved = $false; saveInProgressCoverage = 'Real disk replacement failure, immediate saving and restart persistence; controlled save queue coverage is in test-priority-save-queue.ps1.' }
try {
    # Both resolved paths were verified to remain in this project; restore the exact original on exit.
    if ($taskHadSettings) { Move-Item -LiteralPath $taskSettings -Destination $taskOriginal; $taskOriginalMoved = $true }
    if (Test-Path -LiteralPath $taskV1Backup) {Move-Item -LiteralPath $taskV1Backup -Destination $taskV1Original; $taskV1Moved=$true}
    $taskOwnsSettings = $true
    # Persisted offline choices exercise the real store/UI without requiring physical device operations.
    $first='integration-offline-a'; $second='integration-offline-b'
    $seed='{"version":1,"priorityDevices":[{"id":"integration-offline-a","name":"持久化测试 A"},{"id":"integration-offline-b","name":"持久化测试 B"}]}'
    [IO.File]::WriteAllText($taskSettings,$seed)
    $initial = StartPreview
    Evidence 'startup.json' $initial
    CheckPriorities $initial.status @($first,$second)
    Check ([IO.File]::ReadAllText($taskSettings) -ceq $seed) 'Reading v1 changed the original file.'
    $null = OpenSettings
    $null = Command 'preferences' 'up' $second
    $draft = SaveSettled
    CheckDraftOrder $draft.status
    Check $draft.status.preferences.dialog.canReorder 'An editable preferences list does not allow reordering.'
    Check (($draft.status.preferences.dialog.rows.deviceId -join '|') -ceq "$second|$first") 'Actual dialog row order differs after moving up.'
    Check ($draft.status.preferences.dialog.actualWidth -gt 0 -and $draft.status.preferences.dialog.actualHeight -gt 0) 'The settings dialog was not laid out.'
    Evidence 'ordered-dialog.json' $draft
    $saved = CloseSettings 'save'
    CheckPriorities $saved.status @($second, $first)
    CheckTray $saved.status @($second, $first)
    $disk=Get-Content -LiteralPath $taskSettings -Raw | ConvertFrom-Json
    Check ($disk.version -eq 3 -and ($disk.priorityDevices.id -join '|') -ceq "$second|$first") 'Immediate move was not persisted as v3.'
    Check ((Test-Path -LiteralPath $taskV1Backup) -and [IO.File]::ReadAllText($taskV1Backup) -ceq $seed) 'First immediate save did not preserve exact v1 bytes.'
    Evidence 'saved.json' $saved

    $null = OpenSettings
    $beforeFailedSave = [IO.File]::ReadAllText($taskSettings)
    $taskLock = [IO.FileStream]::new($taskSettings, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $null = Command 'preferences' 'remove' $second
    $failedSave = WaitStatus {param($s) -not $s.preferences.dialog.isSaving -and -not [string]::IsNullOrEmpty($s.preferences.dialog.error)} 10
    CheckPriorities $failedSave.status @($second, $first)
    Check ([IO.File]::ReadAllText($taskSettings) -ceq $beforeFailedSave) 'A failed write changed the original settings file.'
    Check ($null -ne $failedSave.status.preferences.dialog -and -not [string]::IsNullOrEmpty($failedSave.status.preferences.dialog.error)) 'A failed write did not keep the dialog open with an error.'
    CheckDraftOrder $failedSave.status
    Check (($failedSave.status.preferences.dialog.draftOrder -join '|') -ceq "$second|$first") 'Failed immediate save did not restore the last persisted order.'
    Check (-not $failedSave.status.preferences.dialog.isSaving -and $failedSave.status.preferences.dialog.canReorder) 'A completed failed save did not restore the actual reorder controls.'
    Evidence 'failed-save.json' $failedSave
    $taskLock.Dispose(); $taskLock = $null
    StopPreview

    $restarted = StartPreview
    CheckPriorities $restarted.status @($second, $first)
    CheckTray $restarted.status @($second, $first)
    Evidence 'restarted.json' $restarted
    $null = OpenSettings
    $null = Command 'preferences' 'remove' $second
    $removed=SaveSettled
    CheckPriorities $removed.status @($first)
    Check ((@(Get-Content -LiteralPath $taskSettings -Raw | ConvertFrom-Json).priorityDevices.id -join '|') -ceq $first) 'Removal was not immediately saved.'
    $null = Command 'preferences' 'reset'
    $reset = CloseSettings 'save'
    CheckPriorities $reset.status @()
    Evidence 'reset.json' $reset
    StopPreview

    $unsupported = '{"version":999,"priorityDevices":[]}'
    [IO.File]::WriteAllText($taskSettings, $unsupported)
    $invalid = StartPreview
    Check (-not [string]::IsNullOrEmpty($invalid.status.preferences.warning)) 'Unsupported settings version did not produce a warning.'
    $blocked = OpenSettings
    Check (-not $blocked.status.preferences.dialog.canSave) 'Invalid settings can be overwritten without an explicit reset.'
    Check (-not $blocked.status.preferences.dialog.canReorder) 'Invalid settings allow dragging before an explicit reset.'
    $saveBlocked=$false
    try {$null = Command 'preferences' 'save'}catch{$saveBlocked=$true}
    Check $saveBlocked 'Invalid configuration accepted save without reset.'
    Check ([IO.File]::ReadAllText($taskSettings) -ceq $unsupported) 'Invalid original settings were overwritten.'
    $requested = Command 'preferences' 'reset'
    Check $requested.status.preferences.dialog.resetConfirmationOpen 'Reset did not open the confirmation.'
    Check ([IO.File]::ReadAllText($taskSettings) -ceq $unsupported) 'Merely opening reset confirmation rewrote settings.'
    $null=Command 'preferences' 'reset-cancel'
    Check ([IO.File]::ReadAllText($taskSettings) -ceq $unsupported) 'Cancelling reset confirmation changed settings.'
    $null=Command 'preferences' 'reset'
    $null=Command 'preferences' 'confirm-reset'
    $resetInvalid=SaveSettled
    CheckDraftOrder $resetInvalid.status
    Check $resetInvalid.status.preferences.dialog.canReorder 'Explicit reset did not enable the reorder controls.'
    $recovered = CloseSettings 'save'
    CheckPriorities $recovered.status @()
    $backups = @(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($taskSettings)) -Filter 'settings.json.invalid-*.bak' | Where-Object { [IO.File]::ReadAllText($_.FullName) -ceq $unsupported })
    Check ($backups.Count -gt 0) 'Explicit reset did not preserve the unsupported original in a backup.'
    Evidence 'recovered-invalid.json' $recovered
    StopPreview
    # Keep the test's reset backup with its evidence, away from ordinary preview settings.
    $newBackup = $backups | Where-Object { $_.LastWriteTimeUtc -ge $taskResult.startedAt.UtcDateTime } | Select-Object -Last 1
    if ($null -ne $newBackup) {
        Move-Item -LiteralPath (ProjectPath $newBackup.FullName) -Destination (ProjectPath (Join-Path $taskRun 'unsupported-settings-backup.json'))
    }
    $taskResult.passed = $true
}
catch { $taskFailure = $_; $taskResult.error = $_.Exception.Message }
finally {
    if ($null -ne $taskLock) { $taskLock.Dispose() }
    try { StopPreview } catch {
        $taskResult.passed = $false
        $taskResult.error = [string]$taskResult.error + ' Cleanup: ' + $_.Exception.Message
    }
    $stillRunning = @((Get-Process -Name 'BatteryManager.App' -ErrorAction SilentlyContinue) | Where-Object { $_.Path -eq $taskExe })
    $taskResult.cleanupPending = ($null -ne $taskProcess -and -not $taskProcess.HasExited) -or $stillRunning.Count -gt 0
    if ($taskResult.cleanupPending) {
        $taskResult.passed = $false
        $taskResult.error = [string]$taskResult.error + ' The preview is still running; no forced termination was used and its settings were left in place.'
        $taskResult.pendingProcessIds = @($stillRunning | ForEach-Object { $_.Id })
        $taskResult.preservedTestSettings = $taskSettings
        $taskResult.originalSettingsBackup = $(if ($taskOriginalMoved) { $taskOriginal } else { $null })
    } elseif ($taskOwnsSettings) {
        try {
            if (Test-Path -LiteralPath $taskSettings) { Move-Item -LiteralPath $taskSettings -Destination (ProjectPath (Join-Path $taskRun 'test-settings-final.json')) }
            if ($taskOriginalMoved) { Move-Item -LiteralPath $taskOriginal -Destination $taskSettings }
            if(Test-Path -LiteralPath $taskV1Backup){Move-Item -LiteralPath $taskV1Backup -Destination (ProjectPath (Join-Path $taskRun 'test-settings-v1-backup.bin'))}
            if($taskV1Moved){Move-Item -LiteralPath $taskV1Original -Destination $taskV1Backup}
            $taskResult.originalSettingsRestored = (-not $taskHadSettings -or (Test-Path -LiteralPath $taskSettings))
        } catch {
            $taskResult.passed = $false
            $taskResult.error = [string]$taskResult.error + ' Settings restore: ' + $_.Exception.Message
        }
    }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult.forcedCleanup = $false
    $taskResult.passed = $taskResult.passed -and -not $taskResult.cleanupPending -and $taskResult.originalSettingsRestored
    $taskResult.finishedAt = [DateTimeOffset]::UtcNow
    Evidence 'result.json' $taskResult
}
if (-not $taskResult.passed) { throw "Tray preferences test failed: $($taskResult.error) Evidence: $taskRun" }
[pscustomobject]@{ passed = $true; evidenceDirectory = $taskRun }
