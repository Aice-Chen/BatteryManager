[CmdletBinding()]
param([string]$PreviewExe = '.artifacts/preview/BatteryManager.App.exe')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$taskExe = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($PreviewExe)) { $PreviewExe } else { Join-Path $taskRoot $PreviewExe }))
if (-not $taskExe.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The preview must be inside this project.' }
$taskCursor = $taskExe
while ($taskCursor.Length -gt $taskRoot.Length) {
    if ((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The preview path must not be redirected.' }
    $taskCursor = [IO.Path]::GetDirectoryName($taskCursor)
}
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Publish the preview first.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Exit the current preview before running the fictional UI scenarios.' }
$taskSuffix = 'scenarios-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.json'
$taskOriginalHash = if (Test-Path -LiteralPath $taskSettings) { (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash } else { $null }
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{ passed = $false; scenarioCount = 0; settingsUnchanged = $false; physicallyDrained = $false }

function Invoke-ScenarioCommand([string]$Command, [string]$Action = '', [string]$DeviceId = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 500; ResponseTimeoutMilliseconds = 20000 }
    if ($Action) { $arguments.Action = $Action }
    if ($DeviceId) { $arguments.DeviceId = $DeviceId }
    & $taskClient @arguments
}
function Save-ScenarioEvidence([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory $Name) -Encoding utf8
}
function Assert-Scenario([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-ScenarioStatus {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($taskProcess.HasExited) { throw 'The scenario app exited unexpectedly.' }
        try {
            $response = Invoke-ScenarioCommand 'status'
            if (-not $response.status.scenario.applying -and -not $response.status.session.isRefreshing -and
                $response.status.session.outstandingOperations -eq 0 -and $response.status.view.version -eq $response.status.session.state.version -and @($response.status.view.cards | Where-Object { $_.headerHeight -le 0 }).Count -eq 0) { return $response }
        } catch { if ($attempt -eq 99) { throw } }
        Start-Sleep -Milliseconds 100
    }
    throw 'The scenario controls did not settle.'
}

try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario', 'overview', '--test-pipe', $taskSuffix) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    $taskResult.processId = $taskProcess.Id
    $initial = Get-ScenarioStatus
    Assert-Scenario (-not [string]::IsNullOrWhiteSpace($initial.status.appVersion)) 'The version is absent from the app status.'
    Assert-Scenario ($initial.status.scenario.usesRealHardware -eq $false -and $initial.status.scenario.savesPreferences -eq $false) 'The scenario is not isolated from ordinary operation.'
    Assert-Scenario (@($initial.status.preferences.priorities).Count -eq 0 -and [string]::IsNullOrEmpty($initial.status.preferences.warning)) 'The scenario loaded ordinary preferences or their validation warning.'
    Save-ScenarioEvidence 'initial.json' $initial

    foreach ($name in @('overview', 'retained', 'pending', 'unknown', 'error', 'expired', 'offline', 'recovered', 'unknown', 'overview')) {
        $null = Invoke-ScenarioCommand 'scenario' $name
        $reply = Get-ScenarioStatus
        $status = $reply.status
        Assert-Scenario ($status.scenario.name -eq $name) 'The selected scenario was not applied.'
        Assert-Scenario ($status.view.modeBanner -like '*演示数据*' -and $status.view.modeBanner -like "*$name*") 'The window does not visibly identify the fictional scenario.'
        Assert-Scenario ($status.tray.tooltip -like '*演示数据*') 'The tray does not identify its fictional battery values.'
        Assert-Scenario ($status.scenario.openedHidChannels -eq $status.scenario.closedHidChannels) 'A fictional HID channel did not close.'
        $devices = @($status.session.state.devices | Where-Object visible)
        Assert-Scenario ($devices.Count -eq @($status.view.cards).Count) 'Visible devices and cards differ.'
        foreach ($device in $devices) {
            $card = @($status.view.cards | Where-Object id -eq $device.id)
            $expected = if ($device.connection -eq 'Pending' -or $null -eq $device.batteryPercent) { '—' } else { "$($device.batteryPercent)%" }
            Assert-Scenario ($card.Count -eq 1 -and $card[0].battery -ceq $expected) 'A rendered battery differs from its actual state.'
            $connections = if ($device.connection -eq 'Pending') { @('暂无响应', 'Not responding') } else { @('') }
            Assert-Scenario ($card[0].connection -cin $connections) 'A rendered connection label differs from the new presentation rules.'
            Assert-Scenario ([string]::IsNullOrEmpty($card[0].explanation)) 'Removed explanatory text is still visible.'
            Assert-Scenario ($card[0].headerHeight -eq 68) 'A device header changed its fixed row height.'
            $expectedGlyph = if ($device.connection -eq 'Pending') { 'NoResponse' } elseif ($null -eq $device.batteryPercent) { 'NoReading' } elseif ($device.kind -eq 'Mouse') { 'Charging' } else { 'Level' }
            $expectedSteps = if ($expectedGlyph -in @('NoResponse','NoReading')) { 0 } else { [int][Math]::Round($device.batteryPercent / 10.0, [MidpointRounding]::AwayFromZero) }
            Assert-Scenario ($card[0].batteryGlyph -ceq $expectedGlyph -and $card[0].glyphSteps -eq $expectedSteps) 'The main battery glyph does not represent the responsive reading or missing-response state.'
            Assert-Scenario (-not $card[0].barVisible -and $card[0].barValue -eq 0) 'An obsolete battery bar remains visible in the main card.'
            $panelCard = @($status.panel.cards | Where-Object id -EQ $device.id)
            Assert-Scenario ($panelCard.Count -eq 1 -and $panelCard[0].battery -ceq $expected -and $panelCard[0].batteryGlyph -ceq $expectedGlyph -and $panelCard[0].glyphSteps -eq $expectedSteps) 'The tray panel and main card disagree about the rendered battery glyph or reading.'
            Assert-Scenario (-not $panelCard[0].barVisible -and $panelCard[0].barValue -eq 0) 'An obsolete battery bar remains visible in the tray panel.'
        }
        $headphones = @($status.session.state.devices | Where-Object name -eq '演示耳机')
        Assert-Scenario ($headphones.Count -eq 1) 'The sample headphones are missing.'
        $device = $headphones[0]
        if ($name -ne 'offline') {
            $headphoneCard = @($status.view.cards | Where-Object id -eq $device.id)[0]
            $expectedTone = switch ($name) { { $_ -in @('overview','recovered') } { 'Normal' } { $_ -in @('unknown','expired') } { 'None' } default { 'Stale' } }
            Assert-Scenario ($headphoneCard.tone -ceq $expectedTone) 'Incorrect battery tone.'
            Assert-Scenario ($headphoneCard.readFailed -eq ($name -eq 'error')) 'Warning icon visibility is incorrect.'
            Assert-Scenario (-not $headphoneCard.charging) 'Unknown charging was rendered as charging.'
            $mouse = @($status.session.state.devices | Where-Object kind -eq 'Mouse')[0]
            $mouseCard = @($status.view.cards | Where-Object id -eq $mouse.id)[0]
            Assert-Scenario ($mouseCard.charging -and $mouseCard.tone -eq 'Normal') 'The known charging sample lacks its charging icon.'
            if ($name -eq 'overview') { $taskNormalColor = $headphoneCard.batteryColor }
            if ($expectedTone -eq 'Stale') { Assert-Scenario ($headphoneCard.batteryColor -cne $taskNormalColor) 'Retained battery text did not change color.' }
            if ($name -in @('error','expired')) {
                $expanded = Invoke-ScenarioCommand 'ui' 'expand' $device.id
                $details = @($expanded.status.view.cards | Where-Object id -eq $device.id)[0].detailText -join ' '
                Assert-Scenario ($details -notmatch '73%|08:|过期|expired|上次读数|Last available') 'The expanded section exposed an old value or removed explanation.'
                if ($name -eq 'error') { Assert-Scenario ($details -match 'CR_0x00000017') 'The specific read error is missing from the expanded section.' }
                $null = Invoke-ScenarioCommand 'ui' 'collapse' $device.id
            }
        }
        Assert-Scenario ([string]::IsNullOrEmpty($status.view.summary)) 'The removed global summary is still visible.'
        switch ($name) {
            'overview' { Assert-Scenario ($device.batteryPercent -eq 73 -and $device.freshness -eq 'Recent') 'Overview battery is incorrect.' }
            'retained' { Assert-Scenario ($device.batteryPercent -eq 73 -and $device.freshness -eq 'Retained' -and $null -eq $device.lastError) 'Missing-value retention is incorrect.' }
            'pending' { Assert-Scenario ($device.connection -eq 'Pending' -and $device.batteryPercent -eq 73) 'Pending connection is incorrect.' }
            'unknown' { Assert-Scenario ($null -eq $device.batteryPercent -and $device.freshness -eq 'Unknown') 'The unknown example retained previous scenario data.' }
            'error' { Assert-Scenario ($device.batteryPercent -eq 73 -and $null -ne $device.lastError) 'The read error example is incorrect.' }
            'expired' { Assert-Scenario ($null -eq $device.batteryPercent -and $device.freshness -eq 'Expired') 'The expired example is incorrect.' }
            'offline' { Assert-Scenario (@($status.view.cards).Count -eq 0 -and -not $device.visible) 'Offline cards were not hidden.' }
            'recovered' { Assert-Scenario ($device.connection -eq 'Online' -and $device.batteryPercent -eq 79 -and $null -eq $device.lastError) 'The restored example is incorrect.' }
        }
        $taskResult.scenarioCount++
        Save-ScenarioEvidence ("{0:00}-{1}.json" -f $taskResult.scenarioCount, $name) $reply
    }

    $null = Invoke-ScenarioCommand 'preferences' 'open'
    $null = Invoke-ScenarioCommand 'preferences' 'add' $device.id
    $null = Invoke-ScenarioCommand 'preferences' 'save'
    $saved = Get-ScenarioStatus
    Assert-Scenario (@($saved.status.preferences.priorities).Count -eq 1) 'The example preference did not apply in memory.'
    Save-ScenarioEvidence 'preferences-in-memory.json' $saved
    $taskAfterHash = if (Test-Path -LiteralPath $taskSettings) { (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash } else { $null }
    Assert-Scenario ($taskAfterHash -ceq $taskOriginalHash) 'The scenario changed ordinary settings.json.'
    $taskResult.settingsUnchanged = $true
    $exit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Assert-Scenario ($exit.outcome -eq 'Exited' -and $exit.processId -eq $taskProcess.Id -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'The scenario app did not exit normally.'
    $events = @(Get-Content -LiteralPath $saved.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opens = @($events | Where-Object kind -eq 'hid-open')
    $closed = @($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Assert-Scenario (@($opens | Where-Object { $_.data.channelId -notin $closed }).Count -eq 0) 'A recorded scenario HID channel was not released.'
    Assert-Scenario (@($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'The scenario session did not drain.'
    Assert-Scenario (@($events | Where-Object kind -eq 'capture-limit').Count -eq 0 -and @($events | Where-Object kind -eq 'closed').Count -gt 0) 'The scenario recording is incomplete.'
    $taskResult.physicallyDrained = $true
    $taskResult.openedHidChannels = $opens.Count
    $taskResult.closedHidChannels = $closed.Count
    $taskResult.passed = $true
}
catch { $taskResult.error = $_.Exception.Message; throw }
finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) {
        try { $null = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe }
        catch { $taskResult.cleanupError = $_.Exception.Message }
    }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    Save-ScenarioEvidence 'result.json' $taskResult
}
[pscustomobject]@{ passed = $true; evidenceDirectory = $taskDirectory; scenarios = $taskResult.scenarioCount }

