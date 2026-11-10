[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish folder, not the daily preview.' }
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Publish the test binary first.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Exit the running app before this isolated test.' }
$taskSuffix = 'r4-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"invalid-isolation-sentinel":true}') }
$taskBeforeHash = (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskResult = [ordered]@{
    passed=$false; hardwareMode=$false; checks=@(); evidenceDirectory=$taskDirectory
    imageEvidenceKind='WinUI RenderTargetBitmap exports of actual controls; these are not complete operating-system window screenshots.'
}
$taskProcess = $null
$taskLast = $null

function Command-R4([string]$Command, [string]$Action='', [string]$Value='', [string]$DeviceId='') {
    $arguments = @{ PipeSuffix=$taskSuffix; Command=$Command; ConnectTimeoutMilliseconds=500; ResponseTimeoutMilliseconds=20000 }
    if ($Action) { $arguments.Action=$Action }
    if ($Value) { $arguments.Value=$Value }
    if ($DeviceId) { $arguments.DeviceId=$DeviceId }
    & $taskClient @arguments
}
function Ui-R4([string]$Action, [string]$Value='', [string]$DeviceId='') { Command-R4 'ui' $Action $Value $DeviceId }
function Preference-R4([string]$Action, [string]$DeviceId='') {
    $reply=Command-R4 'preferences' $Action -DeviceId $DeviceId
    if($Action -notin @('open','save')) { $reply=Command-R4 'preferences' 'save' }
    $reply
}
function Check-R4([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Save-R4([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name+'.json')) -Encoding utf8 }
function Wait-R4([scriptblock]$Condition, [int]$Seconds=10) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($deadline.Elapsed.TotalSeconds -lt $Seconds) {
        if ($taskProcess.HasExited) { throw 'The test application exited unexpectedly.' }
        try {
            $last = Command-R4 'status'
            if (& $Condition $last.status) { return $last }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge $Seconds) { throw } }
        Start-Sleep -Milliseconds 30
    }
    if ($null -ne $last) { Save-R4 'last-timeout-state' $last }
    throw 'The expected r4 UI state did not settle.'
}
function Card-R4($Status, [string]$Id) {
    $matching = @($Status.view.cards | Where-Object id -eq $Id)
    if ($matching.Count -ne 1) { throw 'Expected one matching device card.' }
    return $matching[0]
}
function Open-PreferencesR4 {
    $null=Preference-R4 'open'
    Wait-R4 { param($s) $null -ne $s.preferences.dialog -and $s.preferences.dialog.isOpen }
}
function Close-PreferencesR4([string]$Action) {
    if($Action -ne 'save'){throw 'Priority edits save immediately.'}
    $null=Preference-R4 $Action
    $null=Ui-R4 'back'
    Wait-R4 { param($s) $s.view.page -eq 'devices' -and -not $s.preferences.dialog.isOpen }
}
function Check-OrderR4($Status, [string[]]$Expected, [string]$Name) {
    $actual = @($Status.preferences.dialog.rows | ForEach-Object deviceId)
    $draft = @($Status.preferences.dialog.draftOrder)
    Check-R4 (($actual -join ',') -ceq ($Expected -join ',') -and ($draft -join ',') -ceq ($Expected -join ',')) $Name
}

try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario','panel-many','--test-pipe',$taskSuffix) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    $ready=Wait-R4 { param($s) -not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.view.version -eq $s.session.state.version -and $s.view.cards.Count -eq 5 }
    Check-R4 (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences) 'r4 checks use fictional hardware and memory-only settings'
    Check-R4 ($null -eq $ready.status.preferences.warning) 'The invalid settings sentinel was not loaded'
    Save-R4 'startup' $ready
    $eligible=@($ready.status.view.cards | Where-Object { $_.battery -match '^\d+%$' } | Select-Object -First 3)
    Check-R4 ($eligible.Count -eq 3) 'Three currently readable devices are available for priority verification'
    [string[]]$ids=@($eligible | ForEach-Object id)
    $device=@($ready.status.view.cards | Where-Object name -eq '演示耳机')[0]
    $deviceId=[string]$device.id

    $opened=Open-PreferencesR4
    Check-R4 ($opened.status.preferences.dialog.canReorder) 'The editable priority dialog permits reordering'
    foreach ($id in $ids) { $null=Preference-R4 'add' $id }
    $added=Command-R4 'status'
    Check-OrderR4 $added.status $ids 'Adding devices synchronizes actual list and priority draft'
    [string[]]$savedOrder=@($ids[2],$ids[0],$ids[1])
    $null=Preference-R4 'reorder' (ConvertTo-Json -InputObject $savedOrder -Compress)
    $reordered=Command-R4 'status'
    Check-OrderR4 $reordered.status $savedOrder 'The completed-reorder path synchronizes draft and actual row order'
    Save-R4 'reordered-draft' $reordered
    # Allow WinUI's row entrance/reorder transitions to settle for this static export only;
    # this delay is not an animation-duration or performance assertion.
    Start-Sleep -Milliseconds 1200
    $null=Ui-R4 'capture-tray-priority' 'r4-priority-order.png'
    $duplicateRejected=$false
    try { $null=Preference-R4 'reorder' (ConvertTo-Json -InputObject @($ids[0],$ids[0],$ids[1]) -Compress) }
    catch { $duplicateRejected=$true }
    Check-R4 $duplicateRejected 'Repeated device IDs are rejected during reorder'
    $afterRejected=Command-R4 'status'
    Check-OrderR4 $afterRejected.status $savedOrder 'Rejected reorder leaves both list and draft unchanged'
    $saved=Close-PreferencesR4 'save'
    Check-R4 ((@($saved.status.preferences.priorities | ForEach-Object deviceId) -join ',') -ceq ($savedOrder -join ',')) 'Saving retains the reordered priority sequence'
    $selected=Wait-R4 { param($s) $s.tray.deviceId -eq $savedOrder[0] }
    Check-R4 ($selected.status.tray.percent -eq [int]$eligible[2].battery.TrimEnd('%')) 'Tray selection uses the newly first readable device'
    $reopened=Open-PreferencesR4
    Check-OrderR4 $reopened.status $savedOrder 'Reopening restores the saved priority order'
    $null=Preference-R4 'reorder' (ConvertTo-Json -InputObject $ids -Compress)
    $null=Close-PreferencesR4 'save'
    $revisited=Open-PreferencesR4
    Check-OrderR4 $revisited.status $ids 'Returning to settings preserves the immediately saved reorder'
    Check-R4 ((@($revisited.status.preferences.priorities | ForEach-Object deviceId) -join ',') -ceq ($ids -join ',')) 'The latest order is already committed without a save button'
    $null=Close-PreferencesR4 'save'

    $null=Ui-R4 'resize-main' '880,720'
    $null=Ui-R4 'type-open' -DeviceId $deviceId
    $type=Wait-R4 { param($s) $s.uiState.dialogKind -eq 'type' -and $s.uiState.typeListActualHeight -gt 0 }
    $originalMax=[double]$type.status.uiState.typeListMaxHeight
    Check-R4 ($type.status.uiState.typeListActualHeight -le $originalMax + 1 -and $originalMax -lt $type.status.uiState.windowHeight) 'Type list is constrained inside the initial dialog window'
    Save-R4 'type-initial' $type
    # Capture a settled static dialog, independently of the layout assertion above.
    # This delay provides no evidence about animation duration or performance.
    Start-Sleep -Milliseconds 1200
    $null=Ui-R4 'capture-dialog' 'r4-type-top.png'
    $null=Ui-R4 'resize-main' '880,640'
    $short=Wait-R4 { param($s) $s.uiState.typeListMaxHeight -lt $originalMax -and $s.uiState.typeListActualHeight -le $s.uiState.typeListMaxHeight + 1 }
    Check-R4 ($short.status.uiState.windowHeight -lt $type.status.uiState.windowHeight) 'An open type list follows a shorter main window'
    Save-R4 'type-short' $short
    $null=Ui-R4 'type-scroll-last'
    $typeBottom=Wait-R4 { param($s) $s.uiState.typeLastOptionVisible }
    Check-R4 ($typeBottom.status.uiState.typeLastOptionVisible) 'The final device type is fully visible after scrolling the shorter dialog'
    Save-R4 'type-bottom' $typeBottom
    $null=Ui-R4 'capture-dialog' 'r4-type-bottom.png'
    $shortMax=[double]$short.status.uiState.typeListMaxHeight
    $null=Ui-R4 'resize-main' '880,960'
    $tall=Wait-R4 { param($s) $s.uiState.typeListMaxHeight -gt $shortMax -and $s.uiState.typeListActualHeight -le $s.uiState.typeListMaxHeight + 1 }
    Check-R4 ($tall.status.uiState.windowHeight -gt $short.status.uiState.windowHeight) 'An open type list follows a taller main window'
    Save-R4 'type-tall' $tall
    $null=Ui-R4 'dialog-cancel'
    $null=Wait-R4 { param($s) $null -eq $s.uiState.dialogKind }
    $null=Ui-R4 'resize-main' '880,720'

    $null=Ui-R4 'animations' 'on'
    $null=Ui-R4 'collapse' -DeviceId $deviceId
    $collapsed=Wait-R4 { param($s) $c=Card-R4 $s $deviceId; -not $c.isAnimating -and $c.revealProgress -le 0.001 }
    $collapsedHeight=[double](Card-R4 $collapsed.status $deviceId).actualCardHeight
    $null=Ui-R4 'expand-animated' -DeviceId $deviceId
    $middle=Wait-R4 { param($s) $c=Card-R4 $s $deviceId; $c.isAnimating -and $c.revealProgress -gt 0 -and $c.revealProgress -lt 1 }
    Check-R4 ((Card-R4 $middle.status $deviceId).expanded) 'Animated expansion produces a real intermediate reveal state'
    Save-R4 'expand-middle' $middle
    $expanded=Wait-R4 { param($s) $c=Card-R4 $s $deviceId; $c.expanded -and -not $c.isAnimating -and $c.revealProgress -ge 0.999 }
    Check-R4 ((Card-R4 $expanded.status $deviceId).actualCardHeight -gt $collapsedHeight) 'Expansion grows the actual device card before settling'
    Save-R4 'expanded' $expanded
    $null=Ui-R4 'capture-main' 'r4-expanded.png'
    $null=Ui-R4 'collapse' -DeviceId $deviceId
    $null=Ui-R4 'expand-animated' -DeviceId $deviceId
    $beforeReverse=Wait-R4 { param($s) $c=Card-R4 $s $deviceId; $c.isAnimating -and $c.revealProgress -gt 0 -and $c.revealProgress -lt 1 }
    $reversed=Ui-R4 'collapse-animated' -DeviceId $deviceId
    Check-R4 (-not (Card-R4 $reversed.status $deviceId).expanded) 'A second action reverses an in-progress expansion toward closed'
    Save-R4 'reverse-start' ([pscustomobject]@{before=$beforeReverse;after=$reversed})
    $closed=Wait-R4 { param($s) $c=Card-R4 $s $deviceId; -not $c.expanded -and -not $c.isAnimating -and $c.revealProgress -le 0.001 -and [Math]::Abs($c.actualCardHeight - $collapsedHeight) -le 1 }
    Check-R4 ([Math]::Abs((Card-R4 $closed.status $deviceId).actualCardHeight - $collapsedHeight) -le 1) 'Reversed motion reaches the original collapsed card height'
    $null=Ui-R4 'animations' 'off'
    $instantOpen=Ui-R4 'expand-animated' -DeviceId $deviceId
    $instantCard=Card-R4 $instantOpen.status $deviceId
    Check-R4 ($instantCard.expanded -and -not $instantCard.isAnimating -and $instantCard.revealProgress -ge 0.999) 'Animation-disabled expansion settles immediately'
    $instantClosed=Ui-R4 'collapse-animated' -DeviceId $deviceId
    $instantCard=Card-R4 $instantClosed.status $deviceId
    Check-R4 (-not $instantCard.expanded -and -not $instantCard.isAnimating -and $instantCard.revealProgress -le 0.001) 'Animation-disabled collapse settles immediately'

    $null=Ui-R4 'animations' 'on'
    $null=Ui-R4 'expand' -DeviceId $deviceId
    $null=Ui-R4 'panel-open'
    $null=Wait-R4 { param($s) $s.panel.visible }
    Start-Sleep -Milliseconds 350
    $null=Ui-R4 'read-delay' '200'
    $refreshBefore=Command-R4 'status'
    $null=Ui-R4 'refresh-start'
    $spinning=Wait-R4 { param($s) $s.session.isRefreshing -and $s.uiState.refreshMotion.spinning -and $s.panel.refreshMotion.spinning }
    Check-R4 (-not $spinning.status.view.refreshEnabled) 'Both refresh icons spin while the formal read is in progress and refresh is disabled'
    Save-R4 'refresh-spinning' $spinning
    $coasting=Wait-R4 { param($s) -not $s.session.isRefreshing -and $s.uiState.refreshMotion.coasting -and $s.panel.refreshMotion.coasting }
    Check-R4 ($coasting.status.session.generation -gt $refreshBefore.status.session.generation) 'The completed formal refresh enters deceleration on both icons'
    Save-R4 'refresh-coasting' $coasting
    $stopped=Wait-R4 { param($s) -not $s.uiState.refreshMotion.spinning -and -not $s.uiState.refreshMotion.coasting -and -not $s.panel.refreshMotion.spinning -and -not $s.panel.refreshMotion.coasting -and [Math]::Abs($s.uiState.refreshMotion.angle) -le 0.01 -and [Math]::Abs($s.panel.refreshMotion.angle) -le 0.01 }
    Check-R4 ((Card-R4 $stopped.status $deviceId).expanded -and -not (Card-R4 $stopped.status $deviceId).isAnimating) 'Refresh settles both icons at zero while preserving card expansion'
    Save-R4 'refresh-stopped' $stopped
    $null=Ui-R4 'read-delay' '0'
    $null=Ui-R4 'panel-close'
    $null=Ui-R4 'animations' 'reset'
    $taskLast=Command-R4 'status'
    Check-R4 ((Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash -ceq $taskBeforeHash) 'All r4 actions preserve the on-disk settings sentinel'
    $exit=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-R4 ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'The r4 preview exits normally through the formal drain path'
    $events=@(Get-Content -LiteralPath $taskLast.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $openedChannels=@($events | Where-Object kind -eq 'hid-open')
    $closedChannels=@($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-R4 (@($openedChannels | Where-Object { $_.data.channelId -notin $closedChannels }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'All fictional HID channels are released after the animation and dialog checks'
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
