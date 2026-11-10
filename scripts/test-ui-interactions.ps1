[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
$taskExe = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($PreviewExe)) { $PreviewExe } else { Join-Path $taskRoot $PreviewExe }))
if (-not $taskExe.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The test binary must be inside this project.' }
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish folder, not the daily preview.' }
$taskCursor = $taskExe
while ($taskCursor.Length -gt $taskRoot.Length) {
    if ((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The test path must not be redirected.' }
    $taskCursor = [IO.Path]::GetDirectoryName($taskCursor)
}
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Publish the test binary first.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Exit the running app before this isolated scenario test.' }
$taskSuffix = 'ui-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path ([IO.Path]::GetDirectoryName($taskExe)) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"invalid-isolation-sentinel":true}') }
$taskBeforeHash = (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskResult = [ordered]@{ passed=$false; hardwareMode=$false; checks=@(); evidenceDirectory=$taskDirectory }
$taskProcess = $null
$taskLast = $null
function Invoke-UiCommand([string]$Action, [string]$Value='', [string]$DeviceId='') {
    $arguments = @{ PipeSuffix=$taskSuffix; Command='ui'; Action=$Action; ConnectTimeoutMilliseconds=500; ResponseTimeoutMilliseconds=20000 }
    if ($Value -or $Action -eq 'dialog-value') { $arguments.Value=$Value }
    if ($DeviceId) { $arguments.DeviceId=$DeviceId }
    & $taskClient @arguments
}
function Get-UiStatus { & $taskClient -PipeSuffix $taskSuffix -Command status -ConnectTimeoutMilliseconds 500 }
function Wait-Ui([scriptblock]$Condition) {
    for ($attempt=0; $attempt -lt 100; $attempt++) {
        if ($taskProcess.HasExited) { throw 'The test application exited unexpectedly.' }
        try { $reply=Get-UiStatus; if (& $Condition $reply.status) { return $reply } } catch { if ($attempt -eq 99) { throw } }
        Start-Sleep -Milliseconds 100
    }
    throw 'The expected UI state did not settle.'
}
function Check-Ui([bool]$Condition,[string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Save-Ui([string]$Name,$Reply) { $Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name+'.json')) -Encoding utf8 }
function Wait-DialogClosed { Wait-Ui { param($s) $null -eq $s.uiState.dialogKind -and $s.view.version -eq $s.session.state.version } }
try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--scenario','overview','--test-pipe',$taskSuffix) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    $ready = Wait-Ui { param($s) -not $s.scenario.applying -and $s.view.version -eq $s.session.state.version -and $s.view.cards.Count -eq 3 -and @($s.view.cards | Where-Object { $_.headerHeight -ne 68 }).Count -eq 0 }
    Check-Ui (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences) 'Scenario hardware and settings are isolated'
    Check-Ui ($null -eq $ready.status.preferences.warning -and $ready.status.settings.mainColorMode -eq 'System') 'Invalid disk sentinel was not read'
    $taskDevice = @($ready.status.session.state.devices | Where-Object name -eq '演示耳机')[0]
    $taskId = $taskDevice.id
    Save-Ui 'startup' $ready
    $expanded = Invoke-UiCommand 'expand' -DeviceId $taskId
    Check-Ui (@($expanded.status.view.cards | Where-Object id -eq $taskId)[0].expanded) 'Existing source data can expand'
    $orderBefore = @($expanded.status.view.cards.id) -join ','
    $taskKeyboardId = @($expanded.status.session.state.devices | Where-Object kind -eq 'Keyboard')[0].id
    $taskMouseId = @($expanded.status.session.state.devices | Where-Object kind -eq 'Mouse')[0].id
    $expectedOrder = @($taskMouseId, $taskKeyboardId, $taskId) -join ','
    Check-Ui ($orderBefore -ceq $expectedOrder) 'Actual main cards use mouse, keyboard, headphones order'
    Check-Ui ((@($expanded.status.panel.cards.id) -join ',') -ceq $expectedOrder) 'Actual panel rows use the same type and name order'
    $null = & $taskClient -PipeSuffix $taskSuffix -Command refresh
    $afterRefresh = Wait-Ui { param($s) -not $s.session.isRefreshing -and $s.view.version -eq $s.session.state.version }
    Check-Ui ((@($afterRefresh.status.view.cards.id) -join ',') -ceq $orderBefore -and @($afterRefresh.status.view.cards | Where-Object id -eq $taskId)[0].expanded) 'Refresh preserves card order and expansion'
    $null=Invoke-UiCommand 'rename-open' -DeviceId $taskId
    $opened=Wait-Ui { param($s) $s.uiState.dialogKind -eq 'rename' }
    Check-Ui ($opened.status.uiState.dialogValue -eq $taskDevice.name) 'Rename opens the actual dialog with current name'
    $null=Invoke-UiCommand 'dialog-value' 'Discard this draft'
    $null=Invoke-UiCommand 'dialog-cancel'; $canceled=Wait-DialogClosed
    Check-Ui (@($canceled.status.view.cards | Where-Object id -eq $taskId)[0].name -eq $taskDevice.name) 'Cancel discards rename draft'
    $taskAlias='我的耳机 · UI test'
    $null=Invoke-UiCommand 'rename-open' -DeviceId $taskId
    $null=Invoke-UiCommand 'dialog-value' $taskAlias
    $null=Invoke-UiCommand 'dialog-save'; $renamed=Wait-DialogClosed
    Check-Ui (@($renamed.status.view.cards | Where-Object id -eq $taskId)[0].name -eq $taskAlias) 'Rename save updates the main card'
    Check-Ui (@($renamed.status.panel.cards | Where-Object id -eq $taskId)[0].name -eq $taskAlias) 'Rename save updates the cached tray panel'
    $null=& $taskClient -PipeSuffix $taskSuffix -Command preferences -Action open
    $null=& $taskClient -PipeSuffix $taskSuffix -Command preferences -Action add -DeviceId $taskId
    $null=& $taskClient -PipeSuffix $taskSuffix -Command preferences -Action save
    $chosen=Wait-Ui { param($s) $s.tray.deviceId -eq $taskId -and -not $s.preferences.dialog.isSaving }
    Check-Ui ($chosen.status.tray.tooltip -like "*$taskAlias 73%*") 'Renamed preferred device appears in tray tooltip'
    $null=Invoke-UiCommand 'back'
    $null=Invoke-UiCommand 'type-open' -DeviceId $taskId
    $null=Invoke-UiCommand 'dialog-value' 'Mouse'
    $null=Invoke-UiCommand 'dialog-save'; $moved=Wait-DialogClosed
    $movedIds = @($moved.status.view.cards.id)
    Check-Ui ([Array]::IndexOf($movedIds, $taskId) -lt [Array]::IndexOf($movedIds, $taskKeyboardId)) 'Changing shown type moves the existing card into its new group'
    Check-Ui (@($moved.status.view.cards | Where-Object id -eq $taskId)[0].expanded) 'Moving a card preserves its expanded state'
    Check-Ui ((@($moved.status.panel.cards.id) -join ',') -ceq ($movedIds -join ',')) 'Panel follows the reordered main cards'
    $null=Invoke-UiCommand 'type-open' -DeviceId $taskId
    $null=Invoke-UiCommand 'dialog-value' 'Earbuds'
    $null=Invoke-UiCommand 'dialog-save'; $typed=Wait-DialogClosed
    $custom=@($typed.status.settings.deviceCustomizations | Where-Object deviceId -eq $taskId)[0]
    Check-Ui ($custom.kind -eq 'Earbuds' -and $custom.name -eq $taskAlias) 'Type dialog saves choice and preserves alias'
    Check-Ui (@($typed.status.session.state.devices | Where-Object id -eq $taskId)[0].kind -eq $taskDevice.kind) 'Type override leaves hardware classification unchanged'
    Save-Ui 'customization' $typed
    $null=Invoke-UiCommand 'type-open' -DeviceId $taskId; $null=Invoke-UiCommand 'dialog-value' 'Automatic'; $null=Invoke-UiCommand 'dialog-save'; $automatic=Wait-DialogClosed
    Check-Ui ($null -eq @($automatic.status.settings.deviceCustomizations | Where-Object deviceId -eq $taskId)[0].kind) 'Automatic restores detected device type'
    $null=Invoke-UiCommand 'rename-open' -DeviceId $taskId; $null=Invoke-UiCommand 'dialog-reset'; $restored=Wait-DialogClosed
    Check-Ui (@($restored.status.view.cards | Where-Object id -eq $taskId)[0].name -eq $taskDevice.name) 'Restore original name uses actual save path'
    $null=Invoke-UiCommand 'rename-open' -DeviceId $taskId; $null=Invoke-UiCommand 'dialog-value' ''; $null=Invoke-UiCommand 'dialog-save'; $emptyName=Wait-DialogClosed
    Check-Ui (@($emptyName.status.view.cards | Where-Object id -eq $taskId)[0].name -eq $taskDevice.name) 'Empty name saves as original name'
    $settings=Invoke-UiCommand 'settings'; Check-Ui ($settings.status.view.page -eq 'settings') 'Gear route opens separate settings page'
    $null=Invoke-UiCommand 'system-theme' 'dark,light'
    $mixed=Wait-Ui { param($s) $s.theme.simulated -and $s.view.mainTheme -eq 'Light' -and $s.panel.theme -eq 'Dark' }
    Check-Ui ($mixed.status.tray.windowsDark) 'Dark Windows mode and light application mode remain separate'
    $null=Invoke-UiCommand 'theme' 'Dark'
    $manual=Wait-Ui { param($s) $s.view.mainTheme -eq 'Dark' -and $s.settings.mainColorMode -eq 'Dark' }
    $null=Invoke-UiCommand 'system-theme' 'light,dark'
    $manualMixed=Wait-Ui { param($s) $s.view.mainTheme -eq 'Dark' -and $s.panel.theme -eq 'Light' -and -not $s.tray.windowsDark }
    Check-Ui ($manualMixed.status.settings.mainColorMode -eq 'Dark') 'Manual main theme does not change tray theme'
    Save-Ui 'separate-themes' $manualMixed
    $null=Invoke-UiCommand 'theme' 'System'; $null=Invoke-UiCommand 'system-theme' 'light,light'
    $light=Wait-Ui { param($s) $s.view.mainTheme -eq 'Light' -and $s.panel.theme -eq 'Light' }
    $null=Invoke-UiCommand 'back'
    $taskMetrics=@()
    for ($index=0; $index -lt 3; $index++) {
        $before=Get-UiStatus
        $null=Invoke-UiCommand 'panel-open'
        $panel=Wait-Ui { param($s) $s.panel.visible -and $null -ne $s.panel.firstRenderingMilliseconds }
        Start-Sleep -Milliseconds 350
        $panel=Get-UiStatus
        Check-Ui ($panel.status.session.generation -eq $before.status.session.generation -and $panel.status.scenario.openedHidChannels -eq $before.status.scenario.openedHidChannels) "Panel open $index uses cached state without I/O"
        Check-Ui ($panel.status.panel.rowCount -eq 3 -and $panel.status.panel.cards.Count -eq 3) "Panel open $index renders all devices"
        Check-Ui ($panel.status.panel.focusState -eq 'Pointer' -and $panel.status.panel.focusedDeviceId -eq $panel.status.panel.cards[0].id) "Mouse panel open $index focuses the first displayed row without keyboard focus styling"
        $taskMetrics += $panel.status.panel
        $null=Invoke-UiCommand 'panel-close'
        $closed=Wait-Ui { param($s) -not $s.panel.visible }
    }
    $null=Invoke-UiCommand 'panel-open-keyboard'
    $keyboardPanel=Wait-Ui { param($s) $s.panel.visible -and $s.panel.focusState -eq 'Keyboard' }
    Check-Ui ($keyboardPanel.status.panel.focusedDeviceId -eq $keyboardPanel.status.panel.cards[0].id) 'Keyboard activation gives the first displayed row keyboard focus'
    Save-Ui 'keyboard-focus' $keyboardPanel
    $null=Invoke-UiCommand 'panel-close'
    $null=Wait-Ui { param($s) -not $s.panel.visible }
    $taskResult.panelMeasurements=$taskMetrics
    Save-Ui 'panel-measurement' $panel
    $threeRowHeight = $null
    foreach ($case in @(
        @{ name='panel-one'; count=1 }, @{ name='panel-two'; count=2 },
        @{ name='panel-three'; count=3 }, @{ name='panel-many'; count=5 })) {
        $null=& $taskClient -PipeSuffix $taskSuffix -Command scenario -Action $case.name
        $null=Wait-Ui { param($s) -not $s.scenario.applying -and $s.view.version -eq $s.session.state.version }
        $null=Invoke-UiCommand 'panel-open'
        $layout=Wait-Ui { param($s) $s.panel.visible -and $s.panel.viewportHeight -gt 0 -and $null -ne $s.panel.firstRenderingMilliseconds }
        Start-Sleep -Milliseconds 350
        $layout=Get-UiStatus
        $actual=$layout.status.panel
        Check-Ui ($actual.rowCount -eq $case.count) "$($case.name) uses the requested number of actual device rows"
        Check-Ui ($actual.windowHeight -ge $actual.clientHeight -and $actual.clientHeight -gt 0) "$($case.name) exposes actual window and client geometry"
        Check-Ui ($actual.frameSubclassAttached -and $actual.frameChangeApplied -and $null -eq $actual.frameChangeError) "$($case.name) native client-frame override attached and applied successfully"
        Check-Ui ($actual.windowHeight -eq $actual.clientHeight) "$($case.name) client area covers the complete native window height"
        if ($case.count -le 3) {
            Check-Ui ($actual.scrollableHeight -le 0.01 -and $actual.fullyVisibleDeviceIds.Count -eq $case.count) "$($case.name) fits every complete row without overflow"
            Check-Ui ($actual.scrollBarPolicy -eq 'Hidden') "$($case.name) has no unnecessary scroll bar"
            if ($case.count -eq 3) { $threeRowHeight=$actual.windowHeight }
        } else {
            Check-Ui ($actual.windowHeight -eq $threeRowHeight -and $actual.scrollableHeight -gt 0) 'Five devices keep the three-row window height and retain scrolling'
            Check-Ui ($actual.scrollBarPolicy -eq 'Auto' -and $actual.fullyVisibleDeviceIds.Count -eq 3) 'Five-device panel initially shows three complete rows'
        }
        Check-Ui ($actual.theme -eq 'Light' -and $actual.nativeDarkMode -eq $false) "$($case.name) applies light mode to both XAML and the native frame"
        Check-Ui ($actual.nativeBorderColorResult -eq 0 -and $actual.requestedBorderColor -eq [uint32]::MaxValue) "$($case.name) native border accepts the system default light color"
        Save-Ui ("layout-"+$case.name) $layout
        $null=Invoke-UiCommand 'panel-close'
        $null=Wait-Ui { param($s) -not $s.panel.visible }
    }
    $null=Invoke-UiCommand 'system-theme' 'dark,light'
    $darkFrame=Wait-Ui { param($s) $s.panel.theme -eq 'Dark' -and $s.panel.nativeDarkMode -eq $true }
    Check-Ui ($darkFrame.status.view.mainTheme -eq 'Light') 'Dark native panel frame stays independent from the light main window'
    Check-Ui ($darkFrame.status.panel.nativeBorderColorResult -eq 0 -and $darkFrame.status.panel.requestedBorderColor -eq 0x002C2C2C) 'Dark native outline accepts the existing dark panel fallback color'
    Save-Ui 'native-dark-frame' $darkFrame
    $null=Invoke-UiCommand 'system-theme' 'reset'
    $taskLast=Get-UiStatus
    Check-Ui ((Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash -ceq $taskBeforeHash) 'Scenario actions never rewrote the disk settings sentinel'
    $exit=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Ui ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'Actual shutdown drains and exits normally'
    $events=@(Get-Content -LiteralPath $taskLast.status.diagnosticLog | ForEach-Object { $_ | ConvertFrom-Json })
    $opens=@($events | Where-Object kind -eq 'hid-open'); $closes=@($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    Check-Ui (@($opens | Where-Object { $_.data.channelId -notin $closes }).Count -eq 0 -and @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0) 'All fictional HID channels completed release'
    $taskResult.passed=$true
} catch { $taskResult.error=$_.Exception.Message; throw }
finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) { try { $null=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe } catch { $taskResult.cleanupError=$_.Exception.Message } }
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{ passed=$taskResult.passed; evidenceDirectory=$taskDirectory; checks=$taskResult.checks.Count }

