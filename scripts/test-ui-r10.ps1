[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
$taskSuffix = 'r10-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path (Split-Path $taskExe) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"isolated-r10-test":true}') }
$taskHash = (Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{
    passed = $false; hardwareMode = $false; checks = @(); evidenceDirectory = $taskDirectory
    scope = 'r10 value alignment state and native-window screenshots for independent pixel review'
    nativeCaptureCount = 0; controlCaptureCount = 0; samples = @()
    physicalInkAlignmentVerified = $false; popupCoverageVerified = $false
    imageEvidenceKind = 'PrintWindow native-window PNG plus separately identified ContentDialog control exports'
    limitations = @('Screenshot creation does not verify text ink alignment or clipping.', 'Popup coverage of native captures requires visual review.', 'No system setting, physical input, optical animation or alternate DPI test is performed.')
}
function Command-R10([string]$Command, [string]$Action = '', [string]$Value = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }; if ($Value) { $arguments.Value = $Value }; if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Ui-R10([string]$Action, [string]$Value = '', [string]$DeviceId = '') { Command-R10 'ui' $Action $Value $DeviceId }
function Save-R10([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8 }
function Check-R10([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $taskResult.checks += $Name
}
function Wait-R10([scriptblock]$Condition = { param($s) $true }) {
    $deadline = [Diagnostics.Stopwatch]::StartNew(); $last = $null
    while ($deadline.Elapsed.TotalSeconds -lt 20) {
        if ($taskProcess.HasExited) { throw 'The r10 scenario app exited unexpectedly.' }
        try {
            $last = Command-R10 'status'; $s = $last.status
            if (-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version -and (& $Condition $s)) { return $last }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge 20) { throw } }
        Start-Sleep -Milliseconds 50
    }
    if ($null -ne $last) { Save-R10 'last-timeout-state' $last }
    throw 'The requested r10 scenario view did not settle.'
}
function Scenario-R10([string]$Name, [int]$Count) {
    $null = Ui-R10 'panel-close'
    $null = Ui-R10 'back'
    $null = Command-R10 'scenario' $Name
    Wait-R10 { param($s) $s.scenario.name -eq $Name -and $s.view.cards.Count -eq $Count -and @($s.view.cards | Where-Object { $_.isAnimating -or $null -eq $_.glyphRootBounds -or $_.glyphRootBounds.width -le 0 }).Count -eq 0 }
}
function Mode-R10([string]$Mode) {
    $null = Ui-R10 'system-theme' ($Mode + ',' + $Mode)
    Wait-R10 { param($s) $s.theme.simulated -and $s.view.mainTheme -eq $Mode -and $s.panel.theme -eq $Mode }
}
function Panel-R10([int]$Count) {
    $null = Ui-R10 'panel-open'
    Wait-R10 { param($s) $s.panel.visible -and [Math]::Abs($s.panel.contentOffsetY) -lt 0.0001 -and $s.panel.contentOpacity -ge 0.999 -and $s.panel.cards.Count -eq $Count -and @($s.panel.cards | Where-Object { $null -eq $_.glyphRootBounds -or $_.glyphRootBounds.width -le 0 }).Count -eq 0 }
}
function ClosePanel-R10 {
    $null = Ui-R10 'panel-close'
    $null = Wait-R10 { param($s) -not $s.panel.visible }
}
function CheckValueState-R10($Rows, [string]$Label) {
    $incorrectDash = @($Rows | Where-Object { $_.valueDashVisible -ne ($_.batteryGlyph -eq 'NoReading') })
    Check-R10 ($incorrectDash.Count -eq 0) "$Label shows the drawn value dash only for no-reading values"
    $badDash = @($Rows | Where-Object valueDashVisible | Where-Object {
        $d = $_.valueDashRootBounds; $g = $_.glyphRootBounds
        $null -eq $d -or $null -eq $g -or $d.width -le 0 -or $d.height -le 0 -or
        [Math]::Abs(($d.top + $d.height / 2 - $g.top - $g.height / 2) * $g.rasterizationScale) -gt 0.51
    })
    Check-R10 ($badDash.Count -eq 0) "$Label actual drawn-dash bounds center on the battery glyph within half a physical pixel"
    $badValues = @($Rows | Where-Object { $_.batteryGlyph -in @('Level', 'Charging') } | Where-Object {
        $layout = $_.glyphLayout
        $null -eq $layout -or $layout.valueBottom -le $layout.valueTop -or
        [Math]::Abs(($layout.valueTop + $layout.valueBottom - $layout.glyphTop - $layout.glyphBottom) / 2 * $layout.rasterizationScale) -gt 0.51
    })
    Check-R10 ($badValues.Count -eq 0) "$Label actual numeric text boxes center on the glyph within half a physical pixel"
}
function Capture-R10([string]$Name, [string]$Surface = 'main', [string]$Popup = '') {
    # Allow normal entrance/fade transitions to finish without disabling production animations or dismissal.
    Start-Sleep -Milliseconds 500
    $before = Command-R10 'status'
    if ($Surface -eq 'panel' -and (-not $before.status.panel.visible -or $before.status.panel.contentOpacity -lt 0.999)) {
        Save-R10 ($Name + '-interrupted') $before
        throw 'The panel closed or faded before its native capture; no visual pass is claimed.'
    }
    $action = if ($Surface -eq 'panel') { 'capture-panel-native' } else { 'capture-main-native' }
    $file = 'r10-' + $Name + '-native.png'
    $reply = Ui-R10 $action $file
    $shots = Join-Path (Split-Path $reply.status.diagnosticLog) 'screenshots'
    $path = Join-Path $shots $file
    $metadata = [IO.Path]::ChangeExtension($path, '.json')
    if (-not (Test-Path -LiteralPath $path) -or -not (Test-Path -LiteralPath $metadata)) { throw 'Native capture is missing its image or metadata.' }
    Save-R10 $Name $reply
    $taskResult.nativeCaptureCount++
    $taskResult.samples += [pscustomobject]@{ name = $Name; image = $path; metadata = $metadata; action = $action; popupExpected = $Popup; popupCoverageVerified = $false; state = (Join-Path $taskDirectory ($Name + '.json')) }
    if ($Popup -in @('rename', 'type', 'merge')) {
        # Keep a labelled control export as supplementary evidence if native capture omits popup composition.
        $controlName = 'r10-' + $Name + '-control.png'
        $null = Ui-R10 'capture-dialog' $controlName
        $taskResult.controlCaptureCount++
        $taskResult.samples += [pscustomobject]@{ name = $Name + '-control'; image = (Join-Path $shots $controlName); action = 'capture-dialog'; popupExpected = $Popup; popupCoverageVerified = $false; nativeWindowCapture = $false }
    }
}
function CancelDialog-R10 {
    $null = Ui-R10 'dialog-cancel'
    $null = Wait-R10 { param($s) $null -eq $s.uiState.dialogKind }
}
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'panel-battery-range', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $ready = Wait-R10 { param($s) $s.view.cards.Count -eq 5 }
    Check-R10 (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences -and $ready.status.view.modeBanner -match '演示数据') 'r10 screenshots use isolated simulated hardware and clearly labelled demo data'
    Save-R10 'startup' $ready
    foreach ($mode in @('dark', 'light')) {
        $null = Scenario-R10 'panel-battery-range' 5
        $range = Mode-R10 $mode
        Check-R10 ((@($range.status.view.cards.battery | Sort-Object) -join '|') -ceq (@('0%', '100%', '9%', '91%', '—' | Sort-Object) -join '|')) "$mode range includes real zero, one/two/three digits and no-reading state"
        CheckValueState-R10 @($range.status.view.cards) "$mode main range"
        Capture-R10 ($mode + '-main-range')
        $panel = Panel-R10 5
        CheckValueState-R10 @($panel.status.panel.cards) "$mode panel range"
        Capture-R10 ($mode + '-panel-range-top') 'panel'
        $lastIds = @($panel.status.panel.cards | Select-Object -Last 2 -ExpandProperty id)
        $null = Ui-R10 'panel-scroll' 'bottom'
        $bottom = Wait-R10 { param($s) $s.panel.visible -and @($lastIds | Where-Object { $_ -notin $s.panel.fullyVisibleDeviceIds }).Count -eq 0 }
        Check-R10 ($bottom.status.panel.verticalOffset -gt 0 -and @($lastIds | Where-Object { $_ -notin $bottom.status.panel.fullyVisibleDeviceIds }).Count -eq 0) "$mode native panel bottom includes the final two rows after ordinary scrolling"
        Capture-R10 ($mode + '-panel-range-bottom') 'panel'
        ClosePanel-R10

        $pending = Scenario-R10 'pending' 3
        CheckValueState-R10 @($pending.status.view.cards) "$mode main pending"
        Check-R10 (@($pending.status.view.cards | Where-Object batteryGlyph -EQ 'NoResponse').Count -gt 0 -and @($pending.status.view.cards | Where-Object { $_.batteryGlyph -eq 'NoResponse' -and $_.valueDashVisible }).Count -eq 0) "$mode pending text remains distinct from the no-reading value dash"
        Capture-R10 ($mode + '-main-pending')
        $panel = Panel-R10 3
        CheckValueState-R10 @($panel.status.panel.cards) "$mode panel pending"
        Capture-R10 ($mode + '-panel-pending') 'panel'
        ClosePanel-R10

        $charging = Scenario-R10 'charging-60' 3
        CheckValueState-R10 @($charging.status.view.cards) "$mode main charging"
        Check-R10 (@($charging.status.view.cards | Where-Object { $_.charging -and $_.battery -eq '60%' }).Count -eq 1) "$mode charging alignment sample uses a current 60 percent reader result"
        Capture-R10 ($mode + '-main-charging-60')
        $panel = Panel-R10 3
        CheckValueState-R10 @($panel.status.panel.cards) "$mode panel charging"
        Capture-R10 ($mode + '-panel-charging-60') 'panel'
        ClosePanel-R10

        $connections = Scenario-R10 'connection-switch' 3
        $primary = @($connections.status.session.state.detectedDevices | Where-Object name -eq '演示键盘 · 蓝牙')[0].id
        $other = @($connections.status.session.state.detectedDevices | Where-Object name -eq 'Lofree Flow Lite100')[0].id
        $null = Command-R10 'preferences' 'open'
        $null = Command-R10 'preferences' 'reset'
        $null = Wait-R10 { param($s) -not $s.preferences.dialog.isSaving -and $s.preferences.priorities.Count -eq 0 }
        foreach ($id in @($primary, $other)) {
            $null = Command-R10 'preferences' 'add' '' $id
            $null = Wait-R10 { param($s) -not $s.preferences.dialog.isSaving }
        }
        $settings = Wait-R10 { param($s) $s.view.page -eq 'settings' -and $s.preferences.dialog.rows.Count -eq 2 -and $s.preferences.dialog.available.Count -gt 0 }
        Check-R10 ($settings.status.preferences.dialog.rows.Count -eq 2 -and $settings.status.preferences.dialog.available.Count -gt 0) "$mode settings captures include both priority and candidate groups"
        $null = Ui-R10 'settings-scroll' 'top'
        Capture-R10 ($mode + '-settings-top')
        $null = Ui-R10 'settings-scroll' 'bottom'
        Capture-R10 ($mode + '-settings-bottom')
        $null = Ui-R10 'back'
        $null = Ui-R10 'window-show'
        $null = Ui-R10 'card-menu-open' '' $primary
        Capture-R10 ($mode + '-card-menu') 'main' 'card-menu'
        $null = Ui-R10 'card-menu-close' '' $primary

        $null = Ui-R10 'rename-open' '' $primary
        $rename = Wait-R10 { param($s) $s.uiState.dialogKind -eq 'rename' }
        $null = Ui-R10 'dialog-value' 'Battery Manager gyp'
        Check-R10 ($rename.status.uiState.dialogDeviceId -eq $primary) "$mode rename dialog targets the chosen demo card"
        Capture-R10 ($mode + '-rename-dialog') 'main' 'rename'
        $null = Ui-R10 'dialog-save'
        $renamed = Wait-R10 { param($s) $null -eq $s.uiState.dialogKind -and @($s.view.cards | Where-Object { $_.id -eq $primary -and $_.name -eq 'Battery Manager gyp' }).Count -eq 1 }
        $english = Panel-R10 3
        Check-R10 (@($english.status.panel.cards | Where-Object name -EQ 'Battery Manager gyp').Count -eq 1) "$mode panel includes a Latin name with descenders for separate clipping review"
        Capture-R10 ($mode + '-panel-english-name') 'panel'
        ClosePanel-R10
        $null = Ui-R10 'type-open' '' $primary
        $type = Wait-R10 { param($s) $s.uiState.dialogKind -eq 'type' }
        Check-R10 ($type.status.uiState.dialogDeviceId -eq $primary) "$mode type dialog targets the same card"
        Capture-R10 ($mode + '-type-dialog') 'main' 'type'
        CancelDialog-R10
        $null = Ui-R10 'merge-open' '' $primary
        $null = Wait-R10 { param($s) $s.uiState.dialogKind -eq 'merge' }
        $null = Ui-R10 'dialog-value' $other
        $merge = Wait-R10 { param($s) $s.deviceMerge.selected -eq $other }
        Check-R10 ($merge.status.uiState.dialogDeviceId -eq $primary -and $merge.status.deviceMerge.candidates.Count -gt 0) "$mode merge dialog includes an explicitly selected demo connection"
        Capture-R10 ($mode + '-merge-dialog') 'main' 'merge'
        $null = Ui-R10 'dialog-save'
        $merged = Wait-R10 { param($s) $null -eq $s.uiState.dialogKind -and -not $s.deviceMerge.isSaving -and $s.view.cards.Count -eq 2 -and @($s.view.cards | Where-Object { $_.id -eq $primary -and $_.merged -and $_.expanded -and -not $_.isAnimating }).Count -eq 1 }
        Check-R10 ($merged.status.deviceMerge.noticeKind -eq 'merged' -and $merged.status.deviceMerge.canUndo) "$mode merged-card capture includes expanded content and the saved-result notice"
        Capture-R10 ($mode + '-merged-expanded-notice')
        $null = Ui-R10 'merge-notice-close'
        $null = Ui-R10 'scenario-banner' 'hidden'
        $bannerHidden = Wait-R10
        $taskProcess.Refresh()
        Check-R10 ($taskProcess.MainWindowTitle -match '演示数据' -and $bannerHidden.status.tray.tooltip -match '演示数据|Demo') "$mode hidden-banner header retains demo identification in the window title and tray"
        Capture-R10 ($mode + '-main-no-banner')
        $null = Ui-R10 'scenario-banner' 'visible'
        $null = Ui-R10 'unmerge' '' $primary
        $unmerged = Wait-R10 { param($s) -not $s.deviceMerge.isSaving -and $s.settings.deviceLinks.Count -eq 0 -and $s.view.cards.Count -eq 3 }
        Check-R10 ($unmerged.status.settings.deviceLinks.Count -eq 0) "$mode screenshot setup releases its temporary in-memory merge"
        $null = Ui-R10 'merge-notice-close'
    }
    $null = Ui-R10 'system-theme' 'reset'
    $last = Command-R10 'status'
    Check-R10 ($taskResult.nativeCaptureCount -eq 32 -and $taskResult.controlCaptureCount -eq 6) 'Both themes produce the requested 32 native views plus six separately labelled dialog exports'
    Check-R10 ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'r10 screenshots leave the isolated on-disk settings unchanged'
    $exit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-R10 ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'r10 screenshot app exits normally'
    $events = @(Get-Content -LiteralPath $last.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opened = @($events | Where-Object kind -EQ 'hid-open')
    $closed = @($events | Where-Object kind -EQ 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-R10 ($opened.Count -gt 0 -and @($opened | Where-Object { $_.data.channelId -notin $closed }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'r10 screenshot app drains every recorded fictional HID channel'
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { Save-R10 'failed-state' (Command-R10 'status') } catch {} }
    throw
} finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError = $_.Exception.Message } }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed = $taskResult.passed; checks = $taskResult.checks.Count; nativeCaptureCount = $taskResult.nativeCaptureCount; controlCaptureCount = $taskResult.controlCaptureCount; evidenceDirectory = $taskDirectory; physicalInkAlignmentVerified = $false; popupCoverageVerified = $false }

