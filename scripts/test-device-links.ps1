[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
$taskSuffix = 'links-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path (Split-Path $taskExe) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"isolated-link-test":true}') }
$taskHash = (Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{ passed = $false; hardwareMode = $false; checks = @(); evidenceDirectory = $taskDirectory; scope = 'r8 card merge flow with simulated connection lifecycle'; imageEvidenceKind = 'WinUI actual-control exports; not native screen pixels or multi-monitor evidence' }
function Command-Link([string]$Command, [string]$Action = '', [string]$Value = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }; if ($Value) { $arguments.Value = $Value }; if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Ui-Link([string]$Action, [string]$Value = '', [string]$DeviceId = '') { Command-Link 'ui' $Action $Value $DeviceId }
function Save-Link([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8 }
function Check-Link([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Wait-Link([scriptblock]$Condition = { param($s) $true }) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 20) {
        if ($taskProcess.HasExited) { throw 'The scenario app exited unexpectedly.' }
        try {
            $last = Command-Link 'status'; $s = $last.status
            if (-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version -and (& $Condition $s)) { return $last }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge 20) { throw } }
        Start-Sleep -Milliseconds 50
    }
    throw 'Device-link controls did not settle.'
}
function Step-Link([string]$Action) { $null = Ui-Link 'connection-step' $Action; Wait-Link }
function OpenMerge-Link([string]$Source, [string]$Choice = '') {
    $null = Ui-Link 'merge-open' '' $Source
    $opened = Wait-Link { param($s) $s.uiState.dialogKind -eq 'merge' }
    if ($Choice) {
        $null = Ui-Link 'dialog-value' $Choice
        return Wait-Link { param($s) $s.deviceMerge.selected -eq $Choice }
    }
    return $opened
}
function CommitMerge-Link {
    $null = Ui-Link 'dialog-save'
    Wait-Link { param($s) -not $s.deviceMerge.isSaving -and $null -eq $s.uiState.dialogKind }
}
function Capture-Link([string]$Action, [string]$File) {
    # Exports settled actual controls; this is not a screen-pixel or animation-performance claim.
    Start-Sleep -Milliseconds 500
    $null = Ui-Link $Action $File
}
function Rename-Link([string]$Id, [string]$Name) {
    $null = Ui-Link 'rename-open' '' $Id
    $null = Wait-Link { param($s) $s.uiState.dialogKind -eq 'rename' }
    $null = Ui-Link 'dialog-value' $Name
    $null = Ui-Link 'dialog-save'
    Wait-Link { param($s) $null -eq $s.uiState.dialogKind }
}
try {
    # A genuinely empty list must become usable while the same dialog remains open.
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--test-pipe', $taskSuffix, '--scenario', 'panel-one') -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $one = Wait-Link { param($s) $s.view.cards.Count -eq 1 }
    $taskAnchor = $one.status.view.cards[0].id
    $empty = OpenMerge-Link $taskAnchor
    Check-Link ($empty.status.deviceMerge.candidates.Count -eq 0 -and $null -eq $empty.status.deviceMerge.selected) 'An initially single-device session exposes an empty merge dialog without selecting anything'
    Save-Link 'empty-dialog' $empty
    Capture-Link 'capture-dialog' 'r8-merge-empty.png'
    $null = Command-Link 'scenario' 'connection-switch'
    $start = Wait-Link { param($s) $s.view.cards.Count -eq 3 -and $s.deviceMerge.candidates.Count -eq 2 }
    $taskPrimary = @($start.status.session.state.detectedDevices | Where-Object name -eq '演示键盘 · 蓝牙')[0].id
    $taskOther = @($start.status.session.state.detectedDevices | Where-Object name -eq 'Lofree Flow Lite100')[0].id
    Check-Link ($taskPrimary -ne $taskOther -and $start.status.uiState.dialogKind -eq 'merge' -and $start.status.deviceMerge.dialogDeviceId -eq $taskAnchor) 'New connections populate the still-open empty dialog and retain independent identities'
    Check-Link (@($start.status.deviceMerge.candidates | Where-Object name -eq '演示历史键盘').Count -eq 0) 'An offline Windows history node never becomes a previously seen merge candidate'
    $null = Ui-Link 'dialog-cancel'
    $null = Wait-Link { param($s) $null -eq $s.uiState.dialogKind }
    $null = Rename-Link $taskPrimary '关联测试键盘'
    $null = Command-Link 'preferences' 'open'
    $null = Command-Link 'preferences' 'add' '' $taskOther
    $null = Wait-Link { param($s) -not $s.preferences.dialog.isSaving }
    $null = Command-Link 'preferences' 'add' '' $taskPrimary
    $null = Wait-Link { param($s) -not $s.preferences.dialog.isSaving }
    $null = Ui-Link 'back'
    foreach ($mode in @('dark', 'light')) {
        $null = Ui-Link 'system-theme' ($mode + ',' + $mode)
        $choice = OpenMerge-Link $taskPrimary $taskOther
        Check-Link ($choice.status.settings.deviceLinks.Count -eq 0 -and $choice.status.view.cards.Count -eq 3 -and $choice.status.deviceMerge.keepName -match '关联测试键盘') "$mode selecting a connection keeps the starting card's settings and waits for the Merge button"
        Save-Link ($mode + '-merge-choice') $choice
        Capture-Link 'capture-dialog' ('r8-merge-dialog-' + $mode + '.png')
        if ($mode -eq 'dark') {
            $null = Ui-Link 'dialog-cancel'
            $canceled = Wait-Link { param($s) $null -eq $s.uiState.dialogKind }
            Check-Link ($canceled.status.settings.deviceLinks.Count -eq 0 -and $canceled.status.preferences.priorities.Count -eq 2) 'Cancel leaves independent cards and priority settings untouched'
        }
    }
    $null = Ui-Link 'priority-save-test' 'hold-fail'
    $null = Ui-Link 'dialog-save-start'
    $saving = Wait-Link { param($s) $s.deviceMerge.isSaving }
    Check-Link ($saving.status.view.cards.Count -eq 3 -and $saving.status.settings.deviceLinks.Count -eq 0) 'Cards are not optimistically combined while persistence is pending'
    $null = Ui-Link 'dialog-save-start'
    $selectionBlocked = $false
    try { $null = Ui-Link 'dialog-value' $taskAnchor } catch { $selectionBlocked = $_.Exception.Message -match 'save is in progress' }
    $repeated = Wait-Link { param($s) $s.deviceMerge.isSaving }
    Check-Link ($selectionBlocked -and $repeated.status.deviceMerge.selected -eq $taskOther -and $repeated.status.settings.deviceLinks.Count -eq 0) 'A pending save freezes the selected connection and rejects duplicate submission'
    $null = Ui-Link 'priority-save-test' 'release'
    $failed = Wait-Link { param($s) -not $s.deviceMerge.isSaving -and $null -ne $s.deviceMerge.error }
    Check-Link ($failed.status.settings.deviceLinks.Count -eq 0 -and $failed.status.preferences.priorities.Count -eq 2 -and $failed.status.deviceMerge.selected -eq $taskOther -and $failed.status.deviceMerge.noticeKind -eq 'error' -and -not $failed.status.deviceMerge.canUndo) 'A failed merge preserves cards, priorities and selection, and reports an error without a new Undo action'
    Save-Link 'save-failed' $failed
    $linked = CommitMerge-Link
    Check-Link ($linked.status.view.cards.Count -eq 2 -and $linked.status.panel.cards.Count -eq 2 -and $linked.status.session.state.detectedDevices.Count -eq 3) 'Merge combines both surfaces while retaining all raw hardware records'
    Check-Link ($linked.status.preferences.priorities.Count -eq 1 -and $linked.status.preferences.priorities[0].deviceId -eq $taskPrimary -and $linked.status.settings.suspendedPriorities.Count -eq 1) 'The starting card priority remains while the other priority is suspended'
    Check-Link (@($linked.status.view.cards | Where-Object { $_.id -eq $taskPrimary -and $_.name -eq '关联测试键盘' -and $_.merged -and $_.expanded }).Count -eq 1 -and $linked.status.deviceMerge.canUndo) 'Merge keeps the starting card name and identity, expands it, and exposes Undo'
    Check-Link ((@($linked.status.settings.deviceLinks[0].members | ForEach-Object transports | Sort-Object -Unique) -join '|') -eq 'Bluetooth|Receiver') 'Explicit merge saves verified transport metadata without a connection history'
    foreach ($mode in @('light', 'dark')) {
        $null = Ui-Link 'system-theme' ($mode + ',' + $mode)
        $painted = Wait-Link { param($s) @($s.view.cards | Where-Object { $_.id -eq $taskPrimary -and -not $_.isAnimating }).Count -eq 1 }
        Save-Link ($mode + '-merged-expanded') $painted
        Capture-Link 'capture-main' ('r8-main-' + $mode + '-merged-notice.png')
    }
    # Undo uses the same save boundary. A transient failure must leave a retry action.
    $null = Ui-Link 'priority-save-test' 'hold-fail'
    $null = Ui-Link 'merge-undo-start'
    $null = Wait-Link { param($s) $s.deviceMerge.isSaving }
    $null = Ui-Link 'priority-save-test' 'release'
    $undoFailed = Wait-Link { param($s) -not $s.deviceMerge.isSaving -and $s.deviceMerge.canUndo -and $s.deviceMerge.notice -match '模拟保存失败|Simulated' }
    Check-Link ($undoFailed.status.settings.deviceLinks.Count -eq 1 -and $undoFailed.status.view.cards.Count -eq 2) 'A failed Undo leaves the merge intact and retains the retry action'
    Save-Link 'undo-save-failed' $undoFailed
    $null = Ui-Link 'merge-undo'
    $undone = Wait-Link { param($s) $s.settings.deviceLinks.Count -eq 0 -and -not $s.deviceMerge.isSaving }
    Check-Link ($undone.status.view.cards.Count -eq 3 -and ($undone.status.preferences.priorities.deviceId -join '|') -eq "$taskOther|$taskPrimary" -and -not $undone.status.deviceMerge.canUndo) 'Retrying Undo restores independent cards and their original priority order'
    $null = OpenMerge-Link $taskPrimary $taskOther
    $null = CommitMerge-Link
    # A standalone card can join an existing group; the group's own settings win.
    $groupKey = 'group:' + $taskPrimary
    $groupChoice = OpenMerge-Link $taskAnchor $groupKey
    Check-Link (@($groupChoice.status.deviceMerge.candidates | Where-Object { $_.key -eq $groupKey -and $_.isGroup }).Count -eq 1 -and $groupChoice.status.deviceMerge.keepName -match '关联测试键盘') 'An existing group is one explicit candidate and retains its own name and settings'
    $joined = CommitMerge-Link
    Check-Link ($joined.status.settings.deviceLinks[0].members.Count -eq 3 -and $joined.status.view.cards.Count -eq 1 -and $joined.status.view.cards[0].id -eq $taskPrimary) 'Joining an existing group adds the independent member to its stable primary'
    $null = Ui-Link 'merge-undo'
    $restoredGroup = Wait-Link { param($s) $s.settings.deviceLinks.Count -eq 1 -and $s.settings.deviceLinks[0].members.Count -eq 2 }
    Check-Link ($restoredGroup.status.view.cards.Count -eq 2) 'Undoing a group extension restores the prior group and independent card'
    $null = Ui-Link 'unmerge' '' $taskPrimary
    $unmergeNotice = Wait-Link { param($s) $s.settings.deviceLinks.Count -eq 0 -and $s.deviceMerge.canUndo }
    Check-Link ($unmergeNotice.status.view.cards.Count -eq 3 -and $unmergeNotice.status.deviceMerge.noticeKind -eq 'unmerged') 'Unmerge commits immediately and provides its own Undo action'
    Save-Link 'unmerge-notice' $unmergeNotice
    Capture-Link 'capture-main' 'r8-main-unmerged-notice.png'
    $null = Ui-Link 'merge-undo'
    $restored = Wait-Link { param($s) $s.settings.deviceLinks.Count -eq 1 -and -not $s.deviceMerge.isSaving }
    Check-Link ($restored.status.view.cards.Count -eq 2 -and $restored.status.preferences.priorities[0].deviceId -eq $taskPrimary -and $restored.status.settings.suspendedPriorities.Count -eq 1) 'Undoing Unmerge restores the grouped card and suspended priority'
    $originalName = Rename-Link $taskPrimary ' '
    Check-Link (@($originalName.status.view.cards | Where-Object { $_.id -eq $taskPrimary -and $_.name -eq '演示键盘 · 蓝牙' }).Count -eq 1) 'Clearing a merged alias restores the original hardware name'
    $null = Rename-Link $taskPrimary '关联测试键盘'
    $receiver = Step-Link 'receiver'
    $card = @($receiver.status.view.cards | Where-Object id -eq $taskPrimary)[0]
    Check-Link ($card.battery -eq '43%' -and $receiver.status.tray.deviceId -eq $taskPrimary -and @($card.connections | Where-Object { $_ -match '^(蓝牙|Bluetooth):NotConnected$' }).Count -eq 1) 'Receiver mode supplies new battery on the same preferred card while Bluetooth is explicitly disconnected'
    $bluetooth = Step-Link 'bluetooth'
    $card = @($bluetooth.status.view.cards | Where-Object id -eq $taskPrimary)[0]
    Check-Link ($card.battery -eq '71%' -and $card.transport -match '蓝牙|Bluetooth') 'New Bluetooth evidence takes over from the older receiver measurement'
    for ($i = 0; $i -lt 4; $i++) { $bluetooth = Step-Link 'wait' }
    $card = @($bluetooth.status.view.cards | Where-Object id -eq $taskPrimary)[0]
    Check-Link ($card.transport -notmatch '2.4' -and @($card.tags | Where-Object { $_ -match '^2\.4' }).Count -eq 1 -and @($card.connections | Where-Object { $_ -match '^2\.4.*:NoResponse$' }).Count -eq 1) 'Receiver grace expiry changes its connection row to NoResponse while retaining its fixed transport tag'
    Save-Link 'bluetooth' $bluetooth
    $null = Step-Link 'unknown'
    for ($i = 0; $i -lt 31; $i++) { $unknown = Step-Link 'wait' }
    $device = @($unknown.status.session.state.devices | Where-Object id -eq $taskPrimary)[0]
    Check-Link ($device.visible -and $device.connection -eq 'Online' -and $null -eq $device.batteryPercent) 'A merged device stays visible when connected but all battery caches have expired'
    $null = Step-Link 'idle'
    for ($i = 0; $i -lt 33; $i++) { $hidden = Step-Link 'wait' }
    $device = @($hidden.status.session.state.devices | Where-Object id -eq $taskPrimary)[0]
    Check-Link ($device.connection -eq 'Pending' -and -not $device.visible -and @($hidden.status.view.cards | Where-Object id -eq $taskPrimary).Count -eq 0) 'Only whole-device inactivity triggers the fifteen-minute group hiding rule'
    Save-Link 'hidden' $hidden
    $recovered = Step-Link 'receiver'
    $card = @($recovered.status.view.cards | Where-Object id -eq $taskPrimary)[0]
    Check-Link ($card.name -eq '关联测试键盘' -and $card.battery -eq '43%') 'One active transport restores the hidden group under its original name and identity'
    Check-Link (@($card.connections | Where-Object { $_ -match '^(蓝牙|Bluetooth):NotConnected$' }).Count -eq 1 -and @($card.tags | Where-Object { $_ -match '^(蓝牙|Bluetooth)$' }).Count -eq 1) 'A cleaned offline member keeps its saved transport tag without fabricated online evidence'
    # A save accepted before hiding must finish, but cannot resurrect the hidden notice.
    $null = Ui-Link 'priority-save-test' 'hold'
    $null = Ui-Link 'unmerge-start' '' $taskPrimary
    $null = Wait-Link { param($s) $s.deviceMerge.isSaving }
    $null = Ui-Link 'window-hide'
    $null = Ui-Link 'priority-save-test' 'release'
    $unlinked = Wait-Link { param($s) -not $s.deviceMerge.isSaving -and $s.settings.deviceLinks.Count -eq 0 }
    Check-Link (($unlinked.status.preferences.priorities.deviceId -join '|') -eq "$taskOther|$taskPrimary" -and $unlinked.status.settings.suspendedPriorities.Count -eq 0) 'Unmerge restores the independent priorities in their original order'
    Check-Link (@($unlinked.status.view.cards | Where-Object id -eq $taskOther).Count -eq 1 -and (@($unlinked.status.session.state.devices.id | Sort-Object) -join '|') -eq (@($recovered.status.session.state.detectedDevices.id | Sort-Object) -join '|')) 'Unmerge restores raw identities without recreating a cleaned offline member'
    $null = Ui-Link 'window-show'
    $shown = Wait-Link
    Check-Link ($null -eq $shown.status.deviceMerge.notice -and -not $shown.status.deviceMerge.canUndo) 'Completion after hiding the window does not revive its old merge notice or Undo action'
    Save-Link 'unlinked-after-hide' $shown
    $returned = Step-Link 'bluetooth'
    Check-Link (@($returned.status.view.cards | Where-Object { $_.id -eq $taskPrimary -and $_.name -eq '关联测试键盘' }).Count -eq 1) 'The independent Bluetooth member restores its own identity and alias when it reconnects'
    $null = OpenMerge-Link $taskPrimary $taskOther
    $null = CommitMerge-Link
    $null = Command-Link 'preferences' 'open'
    $null = Command-Link 'preferences' 'reset'
    $cleared = Wait-Link { param($s) -not $s.preferences.dialog.isSaving -and $s.preferences.priorities.Count -eq 0 }
    Check-Link ($cleared.status.settings.suspendedPriorities.Count -eq 0) 'Automatic selection clears both current and suspended priorities'
    $null = Ui-Link 'back'
    $null = Ui-Link 'unmerge' '' $taskPrimary
    $cleared = Wait-Link { param($s) -not $s.deviceMerge.isSaving -and $s.settings.deviceLinks.Count -eq 0 }
    Check-Link ($cleared.status.preferences.priorities.Count -eq 0) 'Unmerge does not resurrect priorities explicitly cleared by the user'
    $null = OpenMerge-Link $taskPrimary $taskOther
    $null = Ui-Link 'window-hide'
    $hiddenWindow = Wait-Link { param($s) $null -eq $s.uiState.dialogKind }
    Check-Link ($null -eq $hiddenWindow.status.deviceMerge.dialogDeviceId -and $hiddenWindow.status.settings.deviceLinks.Count -eq 0) 'Hiding the window cancels an unsubmitted merge dialog'
    $null = Ui-Link 'window-show'
    $null = Ui-Link 'system-theme' 'reset'
    $last = Wait-Link
    Check-Link ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'Scenario merges never write the real settings file'
    $exit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Link ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'The app exits normally after merge work'
    $events = @(Get-Content -LiteralPath $last.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opened = @($events | Where-Object kind -eq 'hid-open'); $closed = @($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-Link ($opened.Count -gt 0 -and @($opened | Where-Object { $_.data.channelId -notin $closed }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'All fictional keyboard channels are physically drained and closed'
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { Save-Link 'failed-state' (Command-Link 'status') } catch { } }
    throw
} finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError = $_.Exception.Message } }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed = $taskResult.passed; checks = $taskResult.checks.Count; evidenceDirectory = $taskDirectory }
