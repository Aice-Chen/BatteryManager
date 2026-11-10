[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish folder.' }
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Publish the test binary first.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the running app before this isolated test.' }
$taskSuffix = 'save-queue-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.json'
$taskBeforeHash = if (Test-Path -LiteralPath $taskSettings) { (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash } else { $null }
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskLast = $null
$taskResult = [ordered]@{
    passed=$false; checks=@(); evidenceDirectory=$taskDirectory; hardwareMode=$false
    scope='Fictional I/O and memory-only settings; controlled pauses/failures exercise the formal immediate-save queue. No mouse drag, disk-failure or persistence claim.'
}
function Command-Queue([string]$Command, [string]$Action='', [string]$DeviceId='', [string]$Value='') {
    $arguments=@{ PipeSuffix=$taskSuffix; Command=$Command; ConnectTimeoutMilliseconds=500; ResponseTimeoutMilliseconds=20000 }
    if ($Action) { $arguments.Action=$Action }
    if ($DeviceId) { $arguments.DeviceId=$DeviceId }
    if ($Value) { $arguments.Value=$Value }
    & $taskClient @arguments
}
function Preference-Queue([string]$Action, [string]$DeviceId='') { Command-Queue 'preferences' $Action $DeviceId }
function Control-Queue([string]$Value) { Command-Queue 'ui' 'priority-save-test' -Value $Value }
function Check-Queue([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Save-Queue([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name+'.json')) -Encoding utf8 }
function Equal-Queue($Actual, [string[]]$Expected) { (@($Actual) -join ',') -ceq ($Expected -join ',') }
function Wait-Queue([scriptblock]$Condition) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $last=$null; $lastError=$null
    while ($clock.Elapsed.TotalSeconds -lt 15) {
        if ($taskProcess.HasExited) { throw 'The test app exited unexpectedly.' }
        try {
            $last=Command-Queue 'status'
            if (& $Condition $last.status) { return $last }
        } catch { $lastError=$_.Exception.Message }
        Start-Sleep -Milliseconds 40
    }
    if ($null -ne $last) { Save-Queue 'timeout' $last }
    throw "The expected immediate-save state did not settle. $lastError"
}
function Check-VisualQueue($Status, [string[]]$Expected) {
    $rows=@($Status.preferences.dialog.rows)
    Check-Queue (Equal-Queue @($rows | ForEach-Object deviceId) $Expected) 'Actual row order matches the saved order'
    Check-Queue (Equal-Queue $Status.preferences.dialog.draftOrder $Expected) 'The draft agrees with the actual row order'
    Check-Queue (@($rows | Where-Object { -not $_.hasContent -or -not $_.contentAttached -or -not $_.containerMatches -or $_.actualWidth -le 0 -or $_.actualHeight -le 0 }).Count -eq 0) 'Every priority row retains attached content, a matching container and nonzero size'
}

try {
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--scenario','panel-many','--test-pipe',$taskSuffix) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    $ready=Wait-Queue { param($s) -not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.view.cards.Count -eq 5 }
    Check-Queue (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences) 'The scenario uses fictional I/O and memory-only settings'
    Check-Queue (-not $ready.status.preferences.dialog.isOpen -and -not $ready.status.preferences.dialog.canReorder) 'The hidden settings page does not report itself as editable or open'
    $null=Preference-Queue 'open'
    $opened=Wait-Queue { param($s) $s.preferences.dialog.isOpen -and $s.preferences.dialog.available.Count -ge 3 }
    [string[]]$ids=@($opened.status.preferences.dialog.available | Select-Object -First 3 | ForEach-Object deviceId)

    $null=Control-Queue 'hold'
    $null=Preference-Queue 'add' $ids[0]
    $held=Wait-Queue { param($s) $s.preferences.dialog.isSaving }
    Check-Queue (@($held.status.preferences.priorities).Count -eq 0) 'The held first write has not changed the applied priorities'
    $null=Preference-Queue 'add' $ids[1]
    [string[]]$ordered=@($ids[1],$ids[0])
    $null=Preference-Queue 'reorder' (ConvertTo-Json -InputObject $ordered -Compress)
    $queued=Command-Queue 'status'
    Check-Queue ($queued.status.preferences.dialog.isSaving -and (Equal-Queue $queued.status.preferences.dialog.draftOrder $ordered)) 'Edits accepted during a held write retain the newest requested order'
    Save-Queue 'queued-while-held' $queued
    $null=Command-Queue 'ui' 'back'
    $hidden=Command-Queue 'status'
    Check-Queue (-not $hidden.status.preferences.dialog.isOpen -and -not $hidden.status.preferences.dialog.canReorder) 'Leaving settings closes its editing and reorder gate'
    $rejected=$false
    try { $null=Preference-Queue 'add' $ids[2] } catch { $rejected=$true }
    Check-Queue $rejected 'A hidden settings page rejects a new edit'
    $null=Control-Queue 'release'
    $saved=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and (Equal-Queue @($s.preferences.priorities | ForEach-Object deviceId) $ordered) }
    Check-Queue ([string]::IsNullOrEmpty($saved.status.preferences.dialog.error)) 'The accepted queue finishes while its page is hidden'
    $null=Preference-Queue 'open'
    $visible=Wait-Queue { param($s) $s.preferences.dialog.isOpen -and @($s.preferences.dialog.rows | Where-Object { -not $_.contentAttached -or $_.actualWidth -le 0 }).Count -eq 0 }
    Check-VisualQueue $visible.status $ordered
    Save-Queue 'saved-queue' $visible

    $null=Control-Queue 'hold'
    [string[]]$ordered=@($ids[0],$ids[1])
    $null=Preference-Queue 'reorder' (ConvertTo-Json -InputObject $ordered -Compress)
    $null=Wait-Queue { param($s) $s.preferences.dialog.isSaving }
    $null=Preference-Queue 'drag-begin' $ids[1]
    $null=Preference-Queue 'drag-remove-insert' (ConvertTo-Json -InputObject @($ids[1],$ids[0]) -Compress)
    $null=Command-Queue 'ui' 'window-hide'
    $windowHidden=Command-Queue 'status'
    Check-Queue (-not $windowHidden.status.preferences.dialog.windowVisible -and -not $windowHidden.status.preferences.dialog.isOpen -and -not $windowHidden.status.preferences.dialog.canReorder -and -not $windowHidden.status.preferences.dialog.isDragging) 'Hiding the whole window closes the edit gate and invalidates an unfinished drag'
    $rejected=$false
    try { $null=Preference-Queue 'add' $ids[2] } catch { $rejected=$true }
    Check-Queue $rejected 'A settings page inside the hidden window rejects a new edit'
    $rejected=$false
    try { $null=Preference-Queue 'drag-end' } catch { $rejected=$true }
    Check-Queue $rejected 'A drag invalidated by hiding the window cannot later commit'
    $null=Control-Queue 'release'
    $windowSaved=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and (Equal-Queue @($s.preferences.priorities | ForEach-Object deviceId) $ordered) }
    Check-Queue (-not $windowSaved.status.preferences.dialog.isOpen) 'Already accepted writes finish while the whole window remains hidden'
    Save-Queue 'window-hidden' $windowSaved
    $null=Command-Queue 'ui' 'window-show'
    $windowShown=Wait-Queue { param($s) $s.preferences.dialog.windowVisible -and $s.preferences.dialog.isOpen -and @($s.preferences.dialog.rows | Where-Object { -not $_.contentAttached -or -not $_.hasContent -or -not $_.containerMatches -or $_.actualWidth -le 0 }).Count -eq 0 }
    Check-VisualQueue $windowShown.status $ordered
    Save-Queue 'window-restored' $windowShown

    $null=Control-Queue 'hold-fail'
    $null=Preference-Queue 'add' $ids[2]
    $null=Wait-Queue { param($s) $s.preferences.dialog.isSaving }
    $null=Preference-Queue 'up' $ids[2]
    $null=Control-Queue 'release'
    $failed=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and -not [string]::IsNullOrEmpty($s.preferences.dialog.error) }
    Check-Queue (Equal-Queue @($failed.status.preferences.priorities | ForEach-Object deviceId) $ordered) 'A failed write leaves the last successfully applied priorities intact'
    Check-Queue (Equal-Queue $failed.status.preferences.dialog.draftOrder $ordered) 'Failure rolls back the pending draft, including edits queued behind the failed write'
    Save-Queue 'failure-rollback' $failed
    $null=Preference-Queue 'add' $ids[2]
    [string[]]$retried=@($ordered)+@($ids[2])
    $retry=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and (Equal-Queue @($s.preferences.priorities | ForEach-Object deviceId) $retried) }
    Check-Queue ([string]::IsNullOrEmpty($retry.status.preferences.dialog.error)) 'A new successful edit clears the failure and saves normally'

    # Keep the same IDs in both orders: a length mismatch must not mask a stale drag commit.
    $null=Control-Queue 'hold-fail'
    $null=Preference-Queue 'up' $ids[2]
    $null=Wait-Queue { param($s) $s.preferences.dialog.isSaving }
    $null=Preference-Queue 'drag-begin' $ids[2]
    [string[]]$dragOrder=@($ids[2],$ids[0],$ids[1])
    $null=Preference-Queue 'drag-remove-insert' (ConvertTo-Json -InputObject $dragOrder -Compress)
    $dragHeld=Command-Queue 'status'
    Check-Queue ($dragHeld.status.preferences.dialog.isDragging -and $dragHeld.status.preferences.dialog.dragCanCommit -and (Equal-Queue @($dragHeld.status.preferences.dialog.rows | ForEach-Object deviceId) $dragOrder)) 'A real RemoveAt/Insert collection sequence remains inside an unfinished drag while saving is held'
    $null=Control-Queue 'release'
    $dragFailed=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and -not [string]::IsNullOrEmpty($s.preferences.dialog.error) }
    Check-Queue ($dragFailed.status.preferences.dialog.isDragging -and -not $dragFailed.status.preferences.dialog.dragCanCommit) 'A failed save revokes the active drag commit while retaining its container barrier'
    Check-Queue ((Equal-Queue $dragFailed.status.preferences.dialog.draftOrder $retried) -and (Equal-Queue @($dragFailed.status.preferences.priorities | ForEach-Object deviceId) $retried)) 'A failure during drag restores both the saved draft and applied priorities'
    Save-Queue 'failure-during-drag' $dragFailed
    $null=Preference-Queue 'drag-end'
    $dragRestored=Wait-Queue { param($s) -not $s.preferences.dialog.isDragging -and -not $s.preferences.dialog.isSaving -and @($s.preferences.dialog.rows | Where-Object { -not $_.contentAttached -or -not $_.hasContent -or -not $_.containerMatches -or $_.actualWidth -le 0 }).Count -eq 0 }
    Check-Queue ((Equal-Queue @($dragRestored.status.preferences.priorities | ForEach-Object deviceId) $retried) -and -not [string]::IsNullOrEmpty($dragRestored.status.preferences.dialog.error)) 'The old drag completion does not resubmit the failed order or clear its failure'
    Check-VisualQueue $dragRestored.status $retried
    Save-Queue 'failed-drag-completed' $dragRestored
    $null=Preference-Queue 'up' $ids[2]
    [string[]]$afterDrag=@($ids[0],$ids[2],$ids[1])
    $dragRetry=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and (Equal-Queue @($s.preferences.priorities | ForEach-Object deviceId) $afterDrag) }
    Check-Queue (-not $dragRetry.status.preferences.dialog.isDragging -and [string]::IsNullOrEmpty($dragRetry.status.preferences.dialog.error)) 'A fresh edit succeeds after the invalidated drag finishes'

    $null=Preference-Queue 'reset'
    $null=Wait-Queue { param($s) -not $s.preferences.dialog.isSaving -and @($s.preferences.priorities).Count -eq 0 }
    $null=Control-Queue 'hold'
    $null=Preference-Queue 'add' $ids[0]
    $null=Wait-Queue { param($s) $s.preferences.dialog.isSaving }
    $null=Preference-Queue 'add' $ids[1]
    [string[]]$exitOrder=@($ids[0],$ids[1])
    $taskLast=Command-Queue 'status'
    Check-Queue ($taskLast.status.preferences.dialog.isSaving -and (Equal-Queue $taskLast.status.preferences.dialog.draftOrder $exitOrder) -and @($taskLast.status.preferences.priorities).Count -eq 0) 'Exit begins with both a held write and a newer accepted order still pending'
    Save-Queue 'before-exit' $taskLast
    $exit=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Queue ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'The app exits normally after releasing only the artificial test hold'
    $events=@(Get-Content -LiteralPath $taskLast.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $drained=@($events | Where-Object kind -eq 'priority-save-drained')
    Check-Queue ($drained.Count -gt 0 -and (Equal-Queue $drained[-1].data.order $exitOrder)) 'Shutdown drains the entire accepted priority queue and records the final order'
    $applied=@($events | Where-Object kind -eq 'priority-save-applied')
    Check-Queue ($applied.Count -gt 0 -and (Equal-Queue $applied[-1].data.order $exitOrder)) 'The final queued order reached the formal save path before shutdown completed'
    Check-Queue (@($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'The hardware session reaches its formal physical drain path'
    $taskAfterHash=if (Test-Path -LiteralPath $taskSettings) { (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash } else { $null }
    Check-Queue ($taskAfterHash -ceq $taskBeforeHash) 'The scenario leaves the original settings file unchanged'
    $taskResult.passed=$true
} catch { $taskResult.error=$_.Exception.Message; throw }
finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) {
        try { $null=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe }
        catch { $taskResult.cleanupError=$_.Exception.Message; $taskResult.passed=$false }
    }
    if ($null -ne $taskProcess) {
        if (-not $taskProcess.HasExited) { $taskResult.cleanupPending=$true; $taskResult.passed=$false }
        $taskProcess.Dispose()
    }
    Save-Queue 'result' $taskResult
}
[pscustomobject]@{ passed=$taskResult.passed; evidenceDirectory=$taskDirectory; checks=$taskResult.checks.Count }
