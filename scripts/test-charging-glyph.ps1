[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
$taskSuffix = 'charging-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path (Split-Path $taskExe) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"isolated-charging-test":true}') }
$taskHash = (Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{
    passed = $false; hardwareMode = $false; checks = @(); evidenceDirectory = $taskDirectory
    scope = 'Charging 10/60/100 percent through the production reader, session and actual controls'
    imageEvidenceKind = 'PrintWindow native PNGs and RenderTargetBitmap card exports; pixels require separate analysis'
    nativeCaptureCount = 0; cardExportCount = 0; glyphExportCount = 0; systemSettingsChanged = $false; computerUseUsed = $false
    multipleDisplayScalesVerified = $false; opticalDisplayVerified = $false; samples = @()
}
function Command-Charging([string]$Command, [string]$Action = '', [string]$Value = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }; if ($Value) { $arguments.Value = $Value }; if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Ui-Charging([string]$Action, [string]$Value = '', [string]$DeviceId = '') { Command-Charging 'ui' $Action $Value $DeviceId }
function Save-Charging([string]$Name, $Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8 }
function Check-Charging([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Wait-Charging([scriptblock]$Condition = { param($s) $true }) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($deadline.Elapsed.TotalSeconds -lt 15) {
        if ($taskProcess.HasExited) { throw 'The charging scenario app exited unexpectedly.' }
        try {
            $last = Command-Charging 'status'; $s = $last.status
            if (-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version -and (& $Condition $s)) { return $last }
        } catch { if ($deadline.Elapsed.TotalSeconds -ge 15) { throw } }
        Start-Sleep -Milliseconds 50
    }
    if ($null -ne $last) { Save-Charging 'last-timeout-state' $last }
    throw 'Charging scenario controls did not settle.'
}
function CheckRows-Charging($Rows, [int]$Percent, [string]$Label) {
    $charge = @($Rows | Where-Object charging)
    $reference = @($Rows | Where-Object { -not $_.charging -and $_.battery -eq ($Percent.ToString() + '%') })
    Check-Charging ($Rows.Count -eq 3 -and $charge.Count -eq 1 -and $reference.Count -eq 1 -and $charge[0].id -ne $reference[0].id) "$Label includes one charging device and a distinct noncharging reference"
    Check-Charging ($charge[0].battery -ceq ($Percent.ToString() + '%') -and $charge[0].batteryGlyph -ceq 'Charging' -and $charge[0].glyphSteps -eq $Percent / 10 -and $reference[0].batteryGlyph -ceq 'Level' -and $reference[0].glyphSteps -eq $Percent / 10) "$Label preserves the reported percentage and corresponding charging/noncharging state"
    Check-Charging ($charge[0].glyphPixelLayout -ceq $reference[0].glyphPixelLayout -and $null -ne $charge[0].glyphRootBounds -and $null -ne $reference[0].glyphRootBounds -and $charge[0].glyphRootBounds.width -gt 0 -and $charge[0].glyphRootBounds.height -gt 0) "$Label charging and reference share dimensions and fill width with actual root coordinates"
    return [pscustomobject]@{ charging = $charge[0]; reference = $reference[0] }
}
function Capture-Charging([string]$Action, [string]$File, [string]$DeviceId = '') {
    # Let the existing entrance transition and acrylic settle; this does not measure animation performance.
    Start-Sleep -Milliseconds 1200
    $reply = Ui-Charging $Action $File $DeviceId
    $shots = Join-Path (Split-Path $reply.status.diagnosticLog) 'screenshots'
    $path = Join-Path $shots $File
    $metadata = [IO.Path]::ChangeExtension($path, '.json')
    if (-not (Test-Path -LiteralPath $path) -or -not (Test-Path -LiteralPath $metadata)) { throw 'Capture did not return an image with metadata.' }
    if ($Action -like '*-native') { $taskResult.nativeCaptureCount++ }
    elseif ($Action -eq 'capture-main-glyph') { $taskResult.glyphExportCount++ }
    else { $taskResult.cardExportCount++ }
    return [pscustomobject]@{ image = $path; metadata = $metadata; action = $Action }
}
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'charging-10', '--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $ready = Wait-Charging { param($s) $s.scenario.name -eq 'charging-10' -and $s.view.cards.Count -eq 3 }
    Check-Charging (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences) 'Charging scenarios isolate hardware and settings'
    Save-Charging 'startup' $ready
    foreach ($mode in @('dark', 'light')) {
        foreach ($percent in @(10, 60, 100)) {
            $name = 'charging-' + $percent
            $null = Command-Charging 'scenario' $name
            $null = Ui-Charging 'system-theme' ($mode + ',' + $mode)
            $main = Wait-Charging { param($s) $s.scenario.name -eq $name -and $s.view.cards.Count -eq 3 -and @($s.view.cards | Where-Object { $null -eq $_.glyphRootBounds -or $_.glyphRootBounds.width -le 0 -or $_.glyphRootBounds.height -le 0 }).Count -eq 0 }
            $mainPair = CheckRows-Charging @($main.status.view.cards) $percent "$mode main $percent"
            $null = Ui-Charging 'panel-open'
            $panel = Wait-Charging { param($s) $s.panel.visible -and [Math]::Abs($s.panel.contentOffsetY) -lt 0.0001 -and $s.panel.cards.Count -eq 3 -and $s.panel.fullyVisibleDeviceIds.Count -eq 3 -and @($s.panel.cards | Where-Object { $null -eq $_.glyphRootBounds -or $_.glyphRootBounds.width -le 0 -or $_.glyphRootBounds.height -le 0 }).Count -eq 0 }
            $panelPair = CheckRows-Charging @($panel.status.panel.cards) $percent "$mode panel $percent"
            Check-Charging ($panelPair.charging.id -eq $mainPair.charging.id -and $panelPair.reference.id -eq $mainPair.reference.id -and $panel.status.panel.scrollableHeight -le 0.5) "$mode $percent main and panel show the same actual devices without scrolling"
            Save-Charging ($mode + '-' + $percent + '-panel') $panel
            $panelImage = Capture-Charging 'capture-panel-native' ('r9-panel-' + $mode + '-charging-' + $percent + '-native.png')
            $null = Ui-Charging 'panel-close'
            $main = Wait-Charging { param($s) -not $s.panel.visible }
            Save-Charging ($mode + '-' + $percent + '-main') $main
            $mainImage = Capture-Charging 'capture-main-native' ('r9-main-' + $mode + '-charging-' + $percent + '-native.png')
            $chargeImage = Capture-Charging 'capture-main-card' ('r9-card-' + $mode + '-charging-' + $percent + '.png') $mainPair.charging.id
            $referenceImage = Capture-Charging 'capture-main-card' ('r9-card-' + $mode + '-reference-' + $percent + '.png') $mainPair.reference.id
            $glyphImage = Capture-Charging 'capture-main-glyph' ('r9-glyph-' + $mode + '-' + $percent + '.png') $mainPair.charging.id
            $taskResult.samples += [pscustomobject]@{ theme = $mode; percent = $percent; scenario = $name; mainChargingId = $mainPair.charging.id; referenceId = $mainPair.reference.id; main = $mainImage; panel = $panelImage; chargingCard = $chargeImage; referenceCard = $referenceImage; transparentGlyph = $glyphImage }
        }
    }
    $null = Ui-Charging 'system-theme' 'reset'
    $last = Command-Charging 'status'
    Check-Charging ($taskResult.nativeCaptureCount -eq 12 -and $taskResult.cardExportCount -eq 12 -and $taskResult.glyphExportCount -eq 6) 'All six settled cases produce native main/panel images, charging/reference card exports and transparent charging glyphs'
    Check-Charging ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'Charging checks preserve the on-disk settings sentinel'
    $exit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Charging ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'Charging scenario exits normally'
    $events = @(Get-Content -LiteralPath $last.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opened = @($events | Where-Object kind -EQ 'hid-open')
    $closed = @($events | Where-Object kind -EQ 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-Charging ($opened.Count -gt 0 -and @($opened | Where-Object { $_.data.channelId -notin $closed }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'Charging checks release every fictional hardware channel'
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { Save-Charging 'failed-state' (Command-Charging 'status') } catch {} }
    throw
} finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError = $_.Exception.Message } }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed = $taskResult.passed; checks = $taskResult.checks.Count; nativeCaptureCount = $taskResult.nativeCaptureCount; cardExportCount = $taskResult.cardExportCount; glyphExportCount = $taskResult.glyphExportCount; evidenceDirectory = $taskDirectory }
