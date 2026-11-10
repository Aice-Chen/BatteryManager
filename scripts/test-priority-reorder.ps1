[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish folder, not the daily preview.' }
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Publish the test binary first.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the running app before this isolated test.' }
$taskSuffix = 'reorder-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"invalid-isolation-sentinel":true}') }
$taskBeforeHash = (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskResult = [ordered]@{
    passed=$false; hardwareMode=$false; checks=@(); evidenceDirectory=$taskDirectory
    interaction='The test simulates native collection remove/insert and invokes the formal completed/cancelled drag path; it does not inject a real mouse drag.'
    imageEvidenceKind='WinUI RenderTargetBitmap exports of actual controls, after layout settles; not complete operating-system window screenshots.'
}
$taskProcess = $null
$taskLast = $null

function Command-Reorder([string]$Command, [string]$Action='', [string]$DeviceId='', [string]$Value='') {
    $arguments = @{ PipeSuffix=$taskSuffix; Command=$Command; ConnectTimeoutMilliseconds=500; ResponseTimeoutMilliseconds=20000 }
    if ($Action) { $arguments.Action=$Action }
    if ($DeviceId) { $arguments.DeviceId=$DeviceId }
    if ($Value) { $arguments.Value=$Value }
    & $taskClient @arguments
}
function Preference-Reorder([string]$Action, [string]$DeviceId='') {
    $reply=Command-Reorder 'preferences' $Action $DeviceId
    if($Action -notin @('open','save')){$reply=Command-Reorder 'preferences' 'save'}
    $reply
}
function Check-Reorder([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Save-Reorder([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name+'.json')) -Encoding utf8 }
function Wait-Reorder([scriptblock]$Condition, [int]$Seconds=10) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $last=$null; $lastError=$null
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        if ($taskProcess.HasExited) { throw 'The test application exited unexpectedly.' }
        try {
            $last=Command-Reorder 'status'
            if (& $Condition $last.status) { return $last }
        } catch { $lastError=$_.Exception.Message }
        Start-Sleep -Milliseconds 40
    }
    if ($null -ne $last) { Save-Reorder 'last-timeout-state' $last }
    throw "The expected priority list did not settle. $lastError"
}
function Has-VisualRows($Status, [int]$Count) {
    if ($null -eq $Status.preferences.dialog -or -not $Status.preferences.dialog.isOpen) { return $false }
    $rows=@($Status.preferences.dialog.rows)
    if ($rows.Count -ne $Count) { return $false }
    foreach ($row in $rows) {
        if (-not $row.hasContent -or -not $row.contentAttached -or -not $row.containerMatches -or
            $row.actualWidth -le 0 -or $row.actualHeight -le 0 -or $row.opacity -lt 0.99 -or $row.visibility -ne 'Visible') { return $false }
    }
    return $true
}
function Wait-VisualOrder([string[]]$Expected) {
    Wait-Reorder {
        param($s)
        if (-not (Has-VisualRows $s $Expected.Count)) { return $false }
        (@($s.preferences.dialog.rows | ForEach-Object deviceId) -join ',') -ceq ($Expected -join ',') -and
            (@($s.preferences.dialog.draftOrder) -join ',') -ceq ($Expected -join ',')
    }
}
function Check-VisualOrder($Status, [string[]]$Expected, [string]$Context) {
    Check-Reorder ((@($Status.preferences.dialog.rows | ForEach-Object deviceId) -join ',') -ceq ($Expected -join ',') -and
        (@($Status.preferences.dialog.draftOrder) -join ',') -ceq ($Expected -join ',')) "$Context preserves the requested actual-row and draft order"
    Check-Reorder (Has-VisualRows $Status $Expected.Count) "$Context has attached, sized, visible content in every matching item container"
    Check-Reorder ((@($Status.preferences.priorities | ForEach-Object deviceId) -join ',') -ceq ($Expected -join ',')) "$Context is already saved"
}
function Open-ReorderDialog {
    $null=Preference-Reorder 'open'
    Wait-Reorder { param($s) $null -ne $s.preferences.dialog -and $s.preferences.dialog.isOpen }
}
function Close-ReorderDialog([string]$Action) {
    if($Action -ne 'save'){throw 'Priority edits save immediately.'}
    $null=Preference-Reorder $Action
    $null=Command-Reorder 'ui' 'back'
    Wait-Reorder { param($s) $s.view.page -eq 'devices' -and -not $s.preferences.dialog.isOpen }
}
function Simulate-Reorder([string[]]$Order, [switch]$Cancel) {
    $action=if ($Cancel) { 'reorder-remove-insert-cancel' } else { 'reorder-remove-insert' }
    $null=Preference-Reorder $action (ConvertTo-Json -InputObject $Order -Compress)
}
function Capture-ReorderDialog([string]$Filename) {
    # Allow implicit row entrance transitions to finish for this static export only.
    # This delay is not an assertion about optical animation duration or performance.
    Start-Sleep -Milliseconds 1200
    $null=Command-Reorder 'ui' 'capture-tray-priority' -Value $Filename
}

try {
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--scenario','panel-many','--test-pipe',$taskSuffix) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    $ready=Wait-Reorder { param($s) -not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.view.version -eq $s.session.state.version -and $s.view.cards.Count -eq 5 }
    Check-Reorder (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences) 'Reorder regression uses fictional hardware and memory-only preferences'
    Check-Reorder ($null -eq $ready.status.preferences.warning) 'The invalid on-disk settings sentinel is not loaded'
    $eligible=@($ready.status.view.cards | Where-Object { $_.battery -match '^\d+%$' } | Select-Object -First 3)
    Check-Reorder ($eligible.Count -eq 3) 'Three readable fictional devices are available'
    [string[]]$ids=@($eligible | ForEach-Object id)
    $null=Open-ReorderDialog
    foreach ($id in $ids) { $null=Preference-Reorder 'add' $id }
    $initial=Wait-VisualOrder $ids
    Check-VisualOrder $initial.status $ids 'Initial three-device list'
    Save-Reorder 'initial' $initial

    [string[]]$current=@($ids[2],$ids[0],$ids[1])
    Simulate-Reorder $current
    $firstMove=Wait-VisualOrder $current
    Check-VisualOrder $firstMove.status $current 'Moving the third device to first by remove/insert'
    Save-Reorder 'third-to-first' $firstMove
    Capture-ReorderDialog 'priority-reorder-third-to-first.png'

    # Rotate the last item to first repeatedly so every device is removed and
    # inserted again, rather than checking only whichever container moved first.
    $movedIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($iteration=0; $iteration -lt $ids.Count; $iteration++) {
        $moving=$current[$current.Count-1]
        [string[]]$next=@($moving)+@($current[0..($current.Count-2)])
        Simulate-Reorder $next
        $rotated=Wait-VisualOrder $next
        Check-VisualOrder $rotated.status $next "Repeated remove/insert $($iteration+1)"
        $null=$movedIds.Add($moving)
        Save-Reorder ("repeat-"+($iteration+1)) $rotated
        $current=$next
    }
    Check-Reorder ($movedIds.SetEquals($ids)) 'Repeated reorder exercises every original device as the moved item'
    Capture-ReorderDialog 'priority-reorder-after-cycles.png'

    [string[]]$cancelAttempt=@($current[2],$current[0],$current[1])
    Simulate-Reorder $cancelAttempt -Cancel
    $cancelledDrag=Wait-VisualOrder $current
    Check-VisualOrder $cancelledDrag.status $current 'Cancelling a collection move'
    Save-Reorder 'cancelled-drag' $cancelledDrag

    [string[]]$savedOrder=@($current)
    $saved=Close-ReorderDialog 'save'
    Check-Reorder ((@($saved.status.preferences.priorities | ForEach-Object deviceId) -join ',') -ceq ($savedOrder -join ',')) 'Save retains the order produced by repeated native collection operations'
    $selected=Wait-Reorder { param($s) $s.tray.deviceId -eq $savedOrder[0] }
    Check-Reorder ($selected.status.tray.deviceId -eq $savedOrder[0]) 'The saved first readable device drives the tray selection'
    $null=Open-ReorderDialog
    $reopened=Wait-VisualOrder $savedOrder
    Check-VisualOrder $reopened.status $savedOrder 'Reopening the saved list'
    Save-Reorder 'saved-reopened' $reopened
    Capture-ReorderDialog 'priority-reorder-saved-reopened.png'

    [string[]]$nextOrder=@($savedOrder[1],$savedOrder[2],$savedOrder[0])
    Simulate-Reorder $nextOrder
    $nextState=Wait-VisualOrder $nextOrder
    Check-VisualOrder $nextState.status $nextOrder 'An additional immediately saved move'
    $null=Close-ReorderDialog 'save'
    $null=Open-ReorderDialog
    $afterReturn=Wait-VisualOrder $nextOrder
    Check-VisualOrder $afterReturn.status $nextOrder 'Leaving and returning to the settings page'
    Save-Reorder 'saved-returned' $afterReturn
    $savedOrder=$nextOrder

    $removedId=$savedOrder[1]
    [string[]]$remaining=@($savedOrder | Where-Object { $_ -ne $removedId })
    $null=Preference-Reorder 'remove' $removedId
    $removed=Wait-VisualOrder $remaining
    Check-VisualOrder $removed.status $remaining 'Removing a device after repeated drag completion'
    Save-Reorder 'removed' $removed
    $null=Preference-Reorder 'add' $removedId
    [string[]]$readdedOrder=@($remaining)+@($removedId)
    $readded=Wait-VisualOrder $readdedOrder
    Check-VisualOrder $readded.status $readdedOrder 'Adding the removed device again'
    Save-Reorder 'readded' $readded
    Capture-ReorderDialog 'priority-reorder-readded.png'
    $null=Close-ReorderDialog 'save'

    $taskLast=Command-Reorder 'status'
    Check-Reorder ((Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash -ceq $taskBeforeHash) 'Reorder operations leave the on-disk settings sentinel unchanged'
    $exit=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Reorder ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'The test process drains and exits normally'
    $events=@(Get-Content -LiteralPath $taskLast.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $openedChannels=@($events | Where-Object kind -eq 'hid-open')
    $closedChannels=@($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-Reorder (@($openedChannels | Where-Object { $_.data.channelId -notin $closedChannels }).Count -eq 0 -and
        @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'Every fictional HID channel is released after the reorder regression'
    $taskResult.passed=$true
} catch { $taskResult.error=$_.Exception.Message; throw }
finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) {
        try { $null=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe }
        catch { $taskResult.cleanupError=$_.Exception.Message }
    }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed=$taskResult.passed; checks=$taskResult.checks.Count; evidenceDirectory=$taskDirectory }
