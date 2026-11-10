[CmdletBinding()]
param(
    [string]$PreviewExe = '.artifacts/preview/BatteryManager.App.exe',
    [string]$ArtifactsDirectory = '.artifacts/app-tests',
    [switch]$ExercisePowerLifecycle,
    [switch]$ExerciseClockChanges,
    [switch]$ExitThroughCommandLine
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'

function Resolve-ProjectPath([string]$Path) {
    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $taskRoot $Path }
    $absolute = [System.IO.Path]::GetFullPath($candidate)
    if (-not $absolute.Equals($taskRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $absolute.StartsWith($taskRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test paths must stay inside '$taskRoot': $absolute"
    }
    # Reject redirects through an existing junction or symbolic link before creating output.
    $cursor = $absolute
    while ($cursor.Length -ge $taskRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A test path traverses a junction or symbolic link: $cursor"
            }
        }
        if ($cursor.Equals($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = [System.IO.Path]::GetDirectoryName($cursor)
    }
    return $absolute
}

function Save-Evidence([string]$Name, $Value) {
    $file = Resolve-ProjectPath (Join-Path $taskRunDirectory $Name)
    $json = $Value | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($file, $json, [System.Text.UTF8Encoding]::new($false))
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-AppCommand([string]$Command, [int]$ResponseTimeout = 25000, [string]$Action = '') {
    $arguments = @{ PipeSuffix = $taskSuffix; Command = $Command; ConnectTimeoutMilliseconds = 750; ResponseTimeoutMilliseconds = $ResponseTimeout }
    if ($Action) { $arguments.Action = $Action }
    & $taskClient @arguments
}

function Assert-OwnedProcess {
    if ($null -eq $taskProcess -or $taskProcess.HasExited) { return $false }
    $current = Get-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue
    if ($null -eq $current) { return $false }
    if (-not [string]::Equals($current.Path, $taskExe, [StringComparison]::OrdinalIgnoreCase) -or
        $current.StartTime.ToUniversalTime().Ticks -ne $taskProcessStarted.Ticks) {
        throw 'The process identity no longer matches the preview started by this test; it will not be stopped.'
    }
    return $true
}

function Get-SettledStatus([int]$TimeoutMilliseconds = 35000) {
    $deadline = [System.Diagnostics.Stopwatch]::StartNew()
    $lastProblem = 'The test pipe is not ready.'
    while ($deadline.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        if ($taskProcess.HasExited) { throw "The test preview exited before a settled snapshot was available (exit $($taskProcess.ExitCode))." }
        $response = $null
        try { $response = Invoke-AppCommand 'status' 3000 }
        catch { $lastProblem = $_.Exception.Message }
        if ($null -ne $response) {
            $snapshot = $response.status
            Save-Evidence 'latest-status.json' $response
            if ($snapshot.view.version -eq $snapshot.session.state.version -and
                -not $snapshot.session.isRefreshing -and $snapshot.session.outstandingOperations -eq 0 -and
                $snapshot.view.refreshEnabled) {
                Assert-ViewMatchesState $snapshot
                return $response
            }
            $lastProblem = "The app has not settled: generation=$($snapshot.session.generation), status=$($snapshot.session.status), pending=$($snapshot.session.outstandingOperations), stateVersion=$($snapshot.session.state.version), viewVersion=$($snapshot.view.version)."
        }
        Start-Sleep -Milliseconds 200
    }
    throw "The app did not provide a settled, rendered snapshot within the time limit. $lastProblem"
}

function Assert-ViewMatchesState($Snapshot) {
    Assert-Condition ($Snapshot.view.version -eq $Snapshot.session.state.version) 'The rendered view and state snapshot have different versions.'
    $devices = @($Snapshot.session.state.devices | Where-Object { $_.visible })
    $cards = @($Snapshot.view.cards)
    Assert-Condition ($devices.Count -eq $cards.Count) 'The number of rendered cards differs from the visible device count.'
    Assert-Condition (@($cards | Select-Object -ExpandProperty id -Unique).Count -eq $cards.Count) 'The rendered view contains duplicate device cards.'
    $connectionTexts = @{ Online = @(''); Recent = @(''); Pending = @('暂无响应', 'Not responding') }
    $customizations = if ($null -ne $Snapshot.PSObject.Properties['settings'] -and $null -ne $Snapshot.settings) {
        @($Snapshot.settings.deviceCustomizations)
    } else { @() }
    foreach ($device in $devices) {
        $matches = @($cards | Where-Object { [string]::Equals($_.id, $device.id, [StringComparison]::Ordinal) })
        Assert-Condition ($matches.Count -eq 1) "The visible device '$($device.id)' does not have exactly one card."
        $card = $matches[0]
        $customization = @($customizations | Where-Object { $_.deviceId -ceq $device.id })
        $expectedName = $device.name
        if ($customization.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($customization[0].name)) {
            $expectedName = $customization[0].name.Trim()
        }
        Assert-Condition ([string]::Equals($card.name, $expectedName, [StringComparison]::Ordinal)) "The card name differs from the effective name for device '$($device.id)'."
        $usable = $null -ne $device.batteryPercent -and $device.batteryPercent -ge 0 -and $device.batteryPercent -le 100 -and
            $device.freshness -cin @('Current', 'Recent', 'Retained')
        $expectedBattery = if ($usable -and $device.connection -cne 'Pending') { "$($device.batteryPercent)%" } else { '—' }
        $expectedTone = if (-not $usable) { 'None' } elseif ($device.freshness -ceq 'Retained') { 'Stale' } else { 'Normal' }
        Assert-Condition ([string]::Equals($card.battery, $expectedBattery, [StringComparison]::Ordinal)) "The card battery differs from device '$($device.id)': expected '$expectedBattery', got '$($card.battery)'."
        Assert-Condition ($connectionTexts.ContainsKey([string]$device.connection)) "Unknown connection state '$($device.connection)'."
        Assert-Condition ($connectionTexts[[string]$device.connection] -ccontains $card.connection) "The card connection label differs from device '$($device.id)'."
        Assert-Condition ($card.tone -ceq $expectedTone) "The card battery tone differs from device '$($device.id)'."
        Assert-Condition ([string]::IsNullOrEmpty($card.explanation)) "The card for '$($device.id)' still displays a removed battery explanation."
        $attempts = @($device.sourceAttempts)
        $expectedFailed = $device.connection -cin @('Online', 'Recent') -and $expectedTone -cne 'Normal' -and
            @($attempts | Where-Object { $_.status -cin @('Error', 'Timeout') }).Count -gt 0
        Assert-Condition ($card.readFailed -eq $expectedFailed) "The card failure marker differs from device '$($device.id)'."
        $selected = @($device.readings | Where-Object {
            $_.source -ceq $device.batterySource -and $_.component -ceq 'device' -and
            $_.percent -eq $device.batteryPercent -and $_.freshness -ceq $device.freshness
        } | Select-Object -First 1)
        $expectedCharging = $device.connection -cne 'Pending' -and $expectedTone -ceq 'Normal' -and $selected.Count -eq 1 -and $selected[0].charging -eq $true
        Assert-Condition ($card.charging -eq $expectedCharging) "The card charging marker differs from device '$($device.id)'."
        Assert-Condition (-not $card.barVisible) "The card for '$($device.id)' still exposes the replaced battery bar."
        $expectedGlyph = if ($device.connection -ceq 'Pending') { 'NoResponse' } elseif ($usable) { 'Level' } else { 'NoReading' }
        $expectedSteps = if ($expectedGlyph -ceq 'Level') { [int][Math]::Round($device.batteryPercent / 10.0, [MidpointRounding]::AwayFromZero) } else { 0 }
        Assert-Condition ($card.batteryGlyph -ceq $expectedGlyph -and $card.glyphSteps -eq $expectedSteps) "The battery glyph differs from device '$($device.id)'."
    }
    Assert-Condition ([string]::IsNullOrEmpty($Snapshot.view.summary)) 'The main window still displays the removed global status summary.'
    $expectedEnabled = -not $Snapshot.session.isSuspended -and $Snapshot.session.status -ne 'Resuming' -and -not $Snapshot.session.isRefreshing -and $Snapshot.session.outstandingOperations -eq 0
    Assert-Condition ($Snapshot.view.refreshEnabled -eq $expectedEnabled) 'The refresh button does not match the session lifecycle.'
}

$taskExe = Resolve-ProjectPath $PreviewExe
$taskArtifacts = Resolve-ProjectPath $ArtifactsDirectory
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw "Preview executable not found: $taskExe" }
foreach ($candidateProcess in @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($taskExe)) -ErrorAction SilentlyContinue)) {
    if ([string]::Equals($candidateProcess.Path, $taskExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw "This preview is already running (PID $($candidateProcess.Id)). Exit it from its tray menu before running the app test. The script will not stop it."
    }
}

$taskSuffix = 'check-' + [Guid]::NewGuid().ToString('N')
$taskRunDirectory = Resolve-ProjectPath (Join-Path $taskArtifacts ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix))
$null = New-Item -ItemType Directory -Path $taskRunDirectory -Force
$taskProcess = $null
$taskProcessStarted = [DateTime]::MinValue
$taskFailure = $null
$taskForcedCleanup = $false
$taskPassed = $false
$taskResult = [ordered]@{
    startedAt = [DateTimeOffset]::UtcNow
    previewExe = $taskExe
    pipeSuffix = $taskSuffix
    processId = $null
    initialGeneration = $null
    refreshGeneration = $null
    refreshOutcome = $null
    exitCode = $null
    quitAcknowledged = $false
    forcedCleanup = $false
    cleanupPending = $false
    passed = $false
    error = $null
}

try {
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--test-pipe', $taskSuffix) -WorkingDirectory (Split-Path -Parent $taskExe) -WindowStyle Hidden -PassThru
    $taskProcessStarted = $taskProcess.StartTime.ToUniversalTime()
    $taskResult.processId = $taskProcess.Id
    $initial = Get-SettledStatus
    Save-Evidence 'startup.json' $initial
    $taskResult.initialGeneration = $initial.status.session.generation

    if ($ExercisePowerLifecycle) {
        Assert-Condition $initial.status.power.registered 'Windows suspend/resume notification registration failed.'
        $null = Invoke-AppCommand 'power' 5000 'suspend'
        $pauseDeadline = [Diagnostics.Stopwatch]::StartNew()
        $paused = $null
        while ($pauseDeadline.Elapsed.TotalSeconds -lt 15) {
            $candidate = Invoke-AppCommand 'status'
            if ($candidate.status.session.isSuspended -and $candidate.status.session.outstandingOperations -eq 0 -and
                -not $candidate.status.view.refreshEnabled -and $null -eq $candidate.status.tray.deviceId -and
                $candidate.status.view.version -eq $candidate.status.session.state.version) { $paused = $candidate; break }
            Start-Sleep -Milliseconds 100
        }
        Assert-Condition ($null -ne $paused) 'The actual app did not pause and drain its current reads.'
        Save-Evidence 'power-paused.json' $paused
        Assert-ViewMatchesState $paused.status
        $blocked = Invoke-AppCommand 'refresh'
        Assert-Condition ($blocked.result.outcome -eq 'Suspended') 'A manual refresh started while the app was paused.'
        $pauseWatch = [Diagnostics.Stopwatch]::StartNew()
        while ($pauseWatch.Elapsed.TotalSeconds -lt 35) {
            $during = Invoke-AppCommand 'status'
            Assert-Condition ($during.status.session.generation -eq $paused.status.session.generation) 'Polling started a scan during suspend.'
            Assert-Condition ($during.status.session.isSuspended -and $during.status.session.outstandingOperations -eq 0) 'Suspension lost the physical idle state.'
            Start-Sleep -Milliseconds 1000
        }
        Save-Evidence 'power-after-poll-interval.json' $during
        $null = Invoke-AppCommand 'power' 5000 'resume'
        $resumed = Get-SettledStatus
        Assert-Condition (-not $resumed.status.session.isSuspended) 'Resume left the app suspended.'
        Assert-Condition ($resumed.status.session.generation -gt $paused.status.session.generation) 'Resume did not start a fresh generation.'
        Assert-Condition ($resumed.status.power.lastEvent -eq 'ResumeAutomatic' -and $resumed.status.power.simulated) 'The simulated resume did not pass through the production power handler.'
        Save-Evidence 'power-resumed.json' $resumed
        $interactive = Invoke-AppCommand 'power' 5000 'interactive-resume'
        Assert-Condition ($interactive.status.power.lastEvent -eq 'ResumeInteractive') 'Interactive wake notification was not recorded.'
        Assert-Condition ($interactive.status.session.generation -eq $resumed.status.session.generation) 'The paired interactive wake notification started another scan.'
        Save-Evidence 'power-interactive.json' $interactive
        $taskResult.powerLifecycle = 'Simulated events through the real app; OS sleep delivery requires a separate hardware check.'
    }

    if ($ExerciseClockChanges) {
        $clockBefore = Get-SettledStatus
        Assert-Condition ($null -ne $clockBefore.status.clock -and $clockBefore.status.clock.offsetSeconds -eq 0) 'The app test clock is not available at its normal offset.'
        foreach ($clockAction in @('back-hour', 'forward-hour', 'reset')) {
            $before = Get-SettledStatus
            $availableBefore = @($before.status.session.state.devices | Where-Object { $_.connection -eq 'Online' -and $null -ne $_.batteryPercent })
            Assert-Condition ($availableBefore.Count -gt 0) 'Clock integration testing requires an online device with a battery reading.'
            $changed = Invoke-AppCommand 'clock' 5000 $clockAction
            $expectedOffset = switch ($clockAction) { 'back-hour' { -3600 }; 'forward-hour' { 3600 }; default { 0 } }
            Assert-Condition ($changed.status.clock.offsetSeconds -eq $expectedOffset) 'The app-only wall-clock offset did not change.'
            foreach ($device in $availableBefore) {
                $afterDevice = @($changed.status.session.state.devices | Where-Object { $_.id -ceq $device.id })
                Assert-Condition ($afterDevice.Count -eq 1 -and $afterDevice[0].batteryPercent -eq $device.batteryPercent) 'Adjusting the calendar clock alone changed an available battery.'
                # Source reading times remain the original wall times; only the expiry estimate is projected anew.
                foreach ($reading in @($device.readings | Where-Object { $null -ne $_.percent -and $_.freshness -in @('Current', 'Recent', 'Retained') })) {
                    $sameReading = @($afterDevice[0].readings | Where-Object { $_.endpointId -ceq $reading.endpointId -and $_.source -ceq $reading.source -and $_.property -ceq $reading.property -and $_.receivedAt -eq $reading.receivedAt -and $_.percent -eq $reading.percent })
                    Assert-Condition ($sameReading.Count -gt 0) 'A calendar adjustment rewrote an existing software read timestamp.'
                }
            }
            Save-Evidence ("clock-$clockAction-before-refresh.json") $changed
            $refresh = Invoke-AppCommand 'refresh'
            if ($refresh.result.outcome -eq 'Busy') { $null = Get-SettledStatus; $refresh = Invoke-AppCommand 'refresh' }
            Assert-Condition ($refresh.result.outcome -in @('Completed', 'Partial')) 'Clock adjustment prevented a new read from completing.'
            $after = Get-SettledStatus
            $fresh = @($after.status.session.state.devices | Where-Object { $_.connection -eq 'Online' -and $null -ne $_.batteryPercent } | ForEach-Object { $_.readings } | Where-Object {
                $null -ne $_.percent -and $_.attempt -eq 'Success' -and $_.freshness -in @('Current', 'Recent') -and
                [Math]::Abs(([DateTimeOffset]$_.receivedAt - [DateTimeOffset]$after.status.clock.utc).TotalSeconds) -lt 15
            })
            Assert-Condition ($fresh.Count -gt 0) 'Fresh battery readings were rejected or stamped with synthetic stable UTC after clock adjustment.'
            Save-Evidence ("clock-$clockAction-after-refresh.json") $after
        }
        $taskResult.clockChanges = 'App-only UTC offsets -1h/+1h/reset; elapsed time and the Windows system clock were unchanged.'
    }

    # Busy can occur if automatic polling starts between the idle snapshot and this command.
    # Only the actual refresh result can prove that this test's request was accepted.
    $refreshBudget = [System.Diagnostics.Stopwatch]::StartNew()
    $refreshResponse = $null
    while ($refreshBudget.ElapsedMilliseconds -lt 35000) {
        $before = Get-SettledStatus 15000
        $candidateResponse = Invoke-AppCommand 'refresh'
        Save-Evidence 'refresh-response.json' $candidateResponse
        Assert-Condition ($null -ne $candidateResponse.result) 'The refresh response lacks its actual request result.'
        if ($candidateResponse.result.outcome -eq 'Busy') { continue }
        Assert-Condition (@('Completed', 'Partial', 'TimedOut') -contains $candidateResponse.result.outcome) "Refresh was not accepted or did not reach a supported terminal state: $($candidateResponse.result.outcome)."
        Assert-Condition ($candidateResponse.result.generation -gt $before.status.session.generation) 'The accepted refresh did not start a new generation.'
        Assert-Condition ($candidateResponse.status.session.generation -ge $candidateResponse.result.generation) 'The returned session predates the refresh result.'
        $refreshResponse = $candidateResponse
        break
    }
    Assert-Condition ($null -ne $refreshResponse) 'A manual refresh could not be accepted within the retry budget.'
    $taskResult.refreshGeneration = $refreshResponse.result.generation
    $taskResult.refreshOutcome = $refreshResponse.result.outcome
    $after = Get-SettledStatus
    Assert-Condition ($after.status.session.generation -ge $refreshResponse.result.generation) 'The final snapshot is older than the accepted refresh.'
    Save-Evidence 'after-refresh.json' $after

    if ($ExitThroughCommandLine) {
        $quit = & (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
        Assert-Condition ($quit.outcome -eq 'Exited' -and $quit.processId -eq $taskProcess.Id) 'The command-line exit did not finish the test preview.'
        $taskResult.exitRoute = 'Ordinary command-line exit, same drain path as the tray.'
    }
    else { $quit = Invoke-AppCommand 'quit' 5000 }
    Save-Evidence 'quit.json' $quit
    $taskResult.quitAcknowledged = $true
    Assert-Condition ($taskProcess.WaitForExit(10000)) 'The app acknowledged quit but did not exit within 10 seconds.'
    $taskResult.exitCode = $taskProcess.ExitCode
    Assert-Condition ($taskProcess.ExitCode -eq 0) "The app exited with code $($taskProcess.ExitCode)."
    # Process exit alone cannot prove that native operations finished normally.
    # Inspect the same app's bounded diagnostic recording after its final flush.
    $diagnosticFile = Resolve-ProjectPath $after.status.diagnosticLog
    $diagnosticEvents = @(Get-Content -LiteralPath $diagnosticFile | ForEach-Object { $_ | ConvertFrom-Json })
    $starts = @($diagnosticEvents | Where-Object kind -eq 'start')
    Assert-Condition (@($starts | Where-Object { $_.data.processId -eq $taskProcess.Id }).Count -gt 0) 'The diagnostic recording does not belong to the tested process.'
    $openedChannels = @($diagnosticEvents | Where-Object kind -eq 'hid-open' | ForEach-Object { $_.data.channelId })
    $closedChannels = @($diagnosticEvents | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    $unclosedChannels = @($openedChannels | Where-Object { $_ -notin $closedChannels })
    $drained = @($diagnosticEvents | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0
    $captureLimited = @($diagnosticEvents | Where-Object kind -eq 'capture-limit').Count -gt 0
    $recordingClosed = @($diagnosticEvents | Where-Object kind -eq 'closed').Count -gt 0
    Save-Evidence 'drain.json' ([ordered]@{
        diagnosticLog = $diagnosticFile; physicallyDrained = $drained
        openedChannels = $openedChannels.Count; closedChannels = $closedChannels.Count
        unclosedChannels = $unclosedChannels; captureLimited = $captureLimited; recordingClosed = $recordingClosed
    })
    Assert-Condition (-not $captureLimited -and $recordingClosed) 'The diagnostic recording is incomplete; hardware drainage cannot be verified.'
    Assert-Condition ($drained -and $unclosedChannels.Count -eq 0) 'The app exited without evidence that its session and all opened HID channels drained.'
    $taskResult.physicallyDrained = $true
    $taskPassed = $true
}
catch {
    $taskFailure = $_
    $taskResult.error = $_.Exception.Message
}
finally {
    if ($null -ne $taskProcess -and -not $taskProcess.HasExited) {
        try {
            if (Assert-OwnedProcess) {
                try { $null = Invoke-AppCommand 'quit' 3000 } catch { }
                if (-not $taskProcess.WaitForExit(4000) -and (Assert-OwnedProcess)) {
                    $taskResult.cleanupPending = $true
                    throw "The test preview (PID $($taskProcess.Id)) has not finished draining. It was left running; forced termination is not a normal exit."
                }
            }
        }
        catch {
            $cleanupError = $_.Exception.Message
            $taskResult.error = if ($null -eq $taskResult.error) { $cleanupError } else { $taskResult.error + " Cleanup: $cleanupError" }
            if ($null -eq $taskFailure) { $taskFailure = $_ }
        }
    }
    $taskResult.forcedCleanup = $taskForcedCleanup
    $taskResult.passed = $taskPassed -and -not $taskForcedCleanup -and $null -eq $taskFailure
    $taskResult.finishedAt = [DateTimeOffset]::UtcNow
    Save-Evidence 'result.json' $taskResult
    if ($null -ne $taskProcess) { $taskProcess.Dispose() }
}

if (-not $taskResult.passed) {
    throw "App integration test failed. Evidence: $taskRunDirectory. $($taskResult.error)"
}
Write-Output ([pscustomobject]@{ passed = $true; evidenceDirectory = $taskRunDirectory; refreshOutcome = $taskResult.refreshOutcome; refreshGeneration = $taskResult.refreshGeneration })
