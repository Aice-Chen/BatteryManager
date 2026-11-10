[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
$taskSuffix = 'r11-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path (Split-Path $taskExe) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"isolated-r11-test":true}') }
$taskHash = (Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{
    passed = $false; hardwareMode = $false; checks = @(); evidenceDirectory = $taskDirectory
    scope = 'r11 main title, settings current/version text, and type-option alignment evidence'
    nativeCaptureCount = 0; samples = @()
    physicalInkAlignmentVerified = $false; bannerOverlapVerified = $false; popupCoverageVerified = $false
    imageEvidenceKind = 'PrintWindow native-window PNG with physical-pixel metadata and corresponding app state'
    limitations = @('Capture success and layout bounds do not prove ink alignment or lack of clipping.', 'Banner clearance and dialog coverage require inspection of the captured pixels.', 'No physical input, real hardware, system setting, alternate DPI or optical-animation test is performed.')
}
function Command-R11([string]$Command, [string]$Action = '', [string]$Value = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }; if ($Value) { $arguments.Value = $Value }; if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Ui-R11([string]$Action, [string]$Value = '', [string]$DeviceId = '') { Command-R11 'ui' $Action $Value $DeviceId }
function Save-R11([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8 }
function Check-R11([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $taskResult.checks += $Name
}
function Wait-R11([scriptblock]$Condition = { param($s) $true }) {
    $deadline = [Diagnostics.Stopwatch]::StartNew(); $last = $null
    while ($deadline.Elapsed.TotalSeconds -lt 20) {
        if ($taskProcess.HasExited) { throw 'The r11 scenario app exited unexpectedly.' }
        try {
            $last = Command-R11 'status'; $s = $last.status
            if (-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version -and (& $Condition $s)) { return $last }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge 20) { throw } }
        Start-Sleep -Milliseconds 50
    }
    if ($null -ne $last) { Save-R11 'last-timeout-state' $last }
    throw 'The requested r11 scenario view did not settle.'
}
function Capture-R11([string]$Name, [string]$Page, [string]$Popup = '', [string]$BannerRequested = 'visible', [string]$SettingsScroll = '') {
    # Let existing transitions finish. This delay is not an animation-performance measurement.
    Start-Sleep -Milliseconds 500
    $before = Command-R11 'status'
    $dialogMatches = if ($Popup) { $before.status.uiState.dialogKind -eq $Popup } else { $null -eq $before.status.uiState.dialogKind }
    Check-R11 ($before.status.view.page -eq $Page -and $dialogMatches) "$Name captures the requested page/dialog state"
    $file = 'r11-' + $Name + '-native.png'
    $scrollAttempts = @(); $previous = $null; $scrollSettled = $false
    $captureDeadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        # Rebuilding preference rows can leave layout/scroll anchoring queued after isSaving clears.
        # Reissue ChangeView after the settling delay and require two stable physical-capture observations.
        if ($SettingsScroll) { $null = Ui-R11 'settings-scroll' $SettingsScroll; Start-Sleep -Milliseconds 200 }
        $reply = Ui-R11 'capture-main-native' $file
        $shots = Join-Path (Split-Path $reply.status.diagnosticLog) 'screenshots'
        $path = Join-Path $shots $file
        $metadata = [IO.Path]::ChangeExtension($path, '.json')
        if (-not (Test-Path -LiteralPath $path) -or -not (Test-Path -LiteralPath $metadata)) { throw 'Native capture is missing its image or metadata.' }
        $native = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        if ($native.width -le 0 -or $native.height -le 0 -or $native.rasterizationScale -le 0) { throw 'Native capture metadata has invalid dimensions or scale.' }
        if (-not $SettingsScroll) { break }
        $atTarget = @($native.scrollBefore, $native.scrollAfter | Where-Object {
            $target = if ($SettingsScroll -eq 'top') { 0 } else { $_.scrollableHeight }
            [Math]::Abs($_.verticalOffset - $target) -gt 0.01
        }).Count -eq 0
        $stable = $null -ne $previous -and $previous.width -eq $native.width -and $previous.height -eq $native.height
        foreach ($field in @('scrollableHeight', 'extentHeight', 'viewportHeight', 'viewportWidth', 'rootTop')) {
            $stable = $stable -and [Math]::Abs($native.scrollBefore.$field - $native.scrollAfter.$field) -le 0.01
            if ($null -ne $previous) { $stable = $stable -and [Math]::Abs($previous.scrollAfter.$field - $native.scrollAfter.$field) -le 0.01 }
        }
        $scrollSettled = $atTarget -and $stable
        $scrollAttempts += [pscustomobject]@{ elapsedMilliseconds = $captureDeadline.ElapsedMilliseconds; target = $SettingsScroll; atTarget = $atTarget; stableLayout = $stable; metadata = $native }
        Save-R11 ($Name + '-scroll-synchronization') $scrollAttempts
        $previous = $native
    } while (-not $scrollSettled -and $captureDeadline.Elapsed.TotalSeconds -lt 10)
    if ($SettingsScroll -and -not $scrollSettled) { throw "$Name settings scroll did not settle at its requested endpoint." }
    Save-R11 $Name $reply
    $taskProcess.Refresh()
    $taskResult.nativeCaptureCount++
    $taskResult.samples += [pscustomobject]@{
        name = $Name; image = $path; metadata = $metadata; state = (Join-Path $taskDirectory ($Name + '.json'))
        action = 'capture-main-native'; page = $Page; popupExpected = $Popup; bannerRequested = $BannerRequested
        windowTitle = $taskProcess.MainWindowTitle; popupCoverageVerified = $false
        scrollSynchronizationCaptures = $scrollAttempts.Count
    }
    return $native
}
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'connection-switch', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $ready = Wait-R11 { param($s) $s.scenario.name -eq 'connection-switch' -and $s.view.cards.Count -eq 3 -and @($s.view.cards | Where-Object isAnimating).Count -eq 0 }
    Check-R11 (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences -and $ready.status.view.modeBanner -match '演示数据' -and $null -eq $ready.status.preferences.warning) 'r11 uses fictional I/O, formal app state and in-memory settings despite the isolated disk sentinel'
    Save-R11 'startup' $ready
    $primary = @($ready.status.session.state.detectedDevices | Where-Object name -eq '演示键盘 · 蓝牙')[0].id
    $other = @($ready.status.session.state.detectedDevices | Where-Object name -eq 'Lofree Flow Lite100')[0].id
    foreach ($mode in @('dark', 'light')) {
        $null = Ui-R11 'back'
        $null = Ui-R11 'window-show'
        $null = Ui-R11 'system-theme' ($mode + ',' + $mode)
        $null = Wait-R11 { param($s) $s.theme.simulated -and $s.view.mainTheme -eq $mode -and $s.panel.theme -eq $mode -and $s.view.page -eq 'devices' }
        $null = Ui-R11 'main-scroll' 'top'
        $null = Ui-R11 'scenario-banner' 'hidden'
        $hidden = Wait-R11
        $taskProcess.Refresh()
        Check-R11 ($taskProcess.MainWindowTitle -match '演示数据' -and $hidden.status.tray.tooltip -match '演示数据|Demo') "$mode hides only the content banner while window/tray demo labels remain"
        $null = Capture-R11 ($mode + '-main-no-banner') 'devices' '' 'hidden'
        $null = Ui-R11 'scenario-banner' 'visible'
        $null = Capture-R11 ($mode + '-main-with-banner') 'devices'

        $null = Command-R11 'preferences' 'open'
        $null = Command-R11 'preferences' 'reset'
        $null = Wait-R11 { param($s) -not $s.preferences.dialog.isSaving -and $s.preferences.priorities.Count -eq 0 }
        foreach ($id in @($primary, $other)) {
            $null = Command-R11 'preferences' 'add' '' $id
            $null = Wait-R11 { param($s) -not $s.preferences.dialog.isSaving }
        }
        $settings = Wait-R11 { param($s) $s.view.page -eq 'settings' -and $s.preferences.dialog.rows.Count -eq 2 -and $s.preferences.dialog.available.Count -gt 0 }
        Check-R11 ($settings.status.preferences.dialog.rows.Count -eq 2 -and $settings.status.preferences.dialog.available.Count -gt 0) "$mode settings has both priority and candidate groups"
        $null = Ui-R11 'settings-scroll' 'top'
        $top = Capture-R11 ($mode + '-settings-top') 'settings' -SettingsScroll 'top'
        Check-R11 ([Math]::Abs($top.scrollAfter.verticalOffset) -le 0.01) "$mode settings top capture is at the start of the actual settings scroll viewer"
        $null = Ui-R11 'settings-scroll' 'bottom'
        $bottom = Capture-R11 ($mode + '-settings-bottom') 'settings' -SettingsScroll 'bottom'
        Check-R11 ([Math]::Abs($bottom.scrollAfter.verticalOffset - $bottom.scrollAfter.scrollableHeight) -le 0.01) "$mode settings bottom capture reaches the end of the actual settings scroll viewer"
        $null = Ui-R11 'back'
        $null = Wait-R11 { param($s) $s.view.page -eq 'devices' }
        $null = Ui-R11 'type-open' '' $primary
        $type = Wait-R11 { param($s) $s.uiState.dialogKind -eq 'type' -and $s.uiState.typeListActualHeight -gt 0 }
        Check-R11 ($type.status.uiState.dialogDeviceId -eq $primary -and $type.status.uiState.typeListActualHeight -le $type.status.uiState.typeListMaxHeight + 1) "$mode type dialog targets the demo keyboard and has a constrained list"
        $null = Capture-R11 ($mode + '-type-dialog') 'devices' 'type'
        $null = Ui-R11 'type-scroll-last'
        $typeBottom = Wait-R11 { param($s) $s.uiState.typeLastOptionVisible }
        Check-R11 ($typeBottom.status.uiState.typeLastOptionVisible) "$mode type dialog final option is fully reachable"
        Save-R11 ($mode + '-type-last-option') $typeBottom
        $null = Ui-R11 'dialog-cancel'
        $closed = Wait-R11 { param($s) $null -eq $s.uiState.dialogKind }
        Check-R11 ($closed.status.view.page -eq 'devices' -and $closed.status.view.cards.Count -eq 3) "$mode closing the type dialog restores the three-card device page"
        Save-R11 ($mode + '-type-closed') $closed
    }
    $null = Ui-R11 'system-theme' 'reset'
    $last = Command-R11 'status'
    Check-R11 ($taskResult.nativeCaptureCount -eq 10) 'Both themes produce ten focused native captures with metadata and corresponding state'
    Check-R11 ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'r11 scenario operations leave the isolated on-disk settings unchanged'
    $exit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-R11 ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'r11 screenshot app exits normally'
    $events = @(Get-Content -LiteralPath $last.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opened = @($events | Where-Object kind -EQ 'hid-open')
    $closedChannels = @($events | Where-Object kind -EQ 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-R11 ($opened.Count -gt 0 -and @($opened | Where-Object { $_.data.channelId -notin $closedChannels }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'r11 screenshot app drains every recorded fictional HID channel'
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { Save-R11 'failed-state' (Command-R11 'status') } catch {} }
    throw
} finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError = $_.Exception.Message } }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed = $taskResult.passed; checks = $taskResult.checks.Count; nativeCaptureCount = $taskResult.nativeCaptureCount; evidenceDirectory = $taskDirectory; physicalInkAlignmentVerified = $false; bannerOverlapVerified = $false; popupCoverageVerified = $false }
