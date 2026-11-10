[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateRange(0, 3600)][int]$MinimumDurationSeconds = 0,
    [switch]$RequireDrain,
    [switch]$ExpectPolling,
    [ValidateRange(30, 3600)][int]$MaximumPollingGapSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')

function Resolve-ProjectPath([string]$Path) {
    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $taskRoot $Path }
    $absolute = [IO.Path]::GetFullPath($candidate)
    if (-not $absolute.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must remain inside the project.' }
    $cursor = $absolute
    while ($cursor.Length -ge $taskRoot.Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence paths must not traverse redirects.' }
        if ($cursor -eq $taskRoot) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $absolute
}

function Get-Range($Values) {
    $numbers = @($Values)
    if ($numbers.Count -eq 0) { return $null }
    $measure = $numbers | Measure-Object -Minimum -Maximum -Average
    return [ordered]@{ minimum = $measure.Minimum; maximum = $measure.Maximum; average = [math]::Round($measure.Average, 2) }
}

$taskDirectory = Resolve-ProjectPath $EvidenceDirectory
$taskResult = Get-Content -LiteralPath (Resolve-ProjectPath (Join-Path $taskDirectory 'result.json')) -Raw | ConvertFrom-Json
$taskRecords = @(Get-Content -LiteralPath (Resolve-ProjectPath (Join-Path $taskDirectory 'snapshots.jsonl')) | ForEach-Object { $_ | ConvertFrom-Json })
$taskGood = @($taskRecords | Where-Object { $null -ne $_.PSObject.Properties['sample'] })
if ($taskGood.Count -eq 0) { throw 'The observation contains no successful snapshots.' }
$taskSamples = @($taskGood | ForEach-Object { $_.sample })
$taskChecks = [Collections.Generic.List[string]]::new()
if ($taskResult.durationSeconds -lt $MinimumDurationSeconds) { $taskChecks.Add('The requested observation duration was not reached.') }
if ($taskResult.sampleCount -ne $taskSamples.Count) { $taskChecks.Add('The snapshot file is incomplete or differs from result.json.') }
if (@($taskResult.errors).Count -gt 0) { $taskChecks.Add('The observer reported pipe or snapshot errors.') }
if (@($taskResult.mismatches).Count -gt 0) { $taskChecks.Add('The observer reported card/state differences.') }
$taskFailures = @($taskRecords | Where-Object { $null -ne $_.PSObject.Properties['failure'] })
if ($taskFailures.Count -gt 0) { $taskChecks.Add('The snapshot file contains observer failure records.') }
if ($taskFailures.Count -ne @($taskResult.errors).Count) { $taskChecks.Add('The failure records and result error count differ.') }

$taskRendered = 0
$taskMaxRenderLag = 0.0
$taskLagStart = $null
$taskProgressStart = $taskSamples[0].elapsedSeconds
$taskLastGeneration = $taskSamples[0].generation
$taskMaximumPollingGap = 0.0
$taskSawSuspension = $false
$taskLegacyConnectionText = @{
    Online = @('在线', 'Online'); Recent = @('最近在线', 'Recently online')
    Pending = @('待确认', 'Checking connection'); Offline = @('离线', 'Offline')
}
foreach ($record in $taskGood) {
    $s = $record.status
    if ($s.diagnosticLog -cne $taskResult.diagnosticLog) { $taskChecks.Add('Snapshot diagnostic paths do not identify a single app recording.') }
    if ($s.session.isSuspended) {
        $taskSawSuspension = $true
        $taskProgressStart = $record.sample.elapsedSeconds
    }
    else {
        $taskMaximumPollingGap = [math]::Max($taskMaximumPollingGap, $record.sample.elapsedSeconds - $taskProgressStart)
    }
    if ($record.sample.generation -ne $taskLastGeneration) {
        $taskProgressStart = $record.sample.elapsedSeconds
        $taskLastGeneration = $record.sample.generation
    }
    if ($s.view.version -ne $s.session.state.version) {
        if ($null -eq $taskLagStart) { $taskLagStart = $record.sample.elapsedSeconds }
        $taskMaxRenderLag = [math]::Max($taskMaxRenderLag, $record.sample.elapsedSeconds - $taskLagStart)
        continue
    }
    $taskLagStart = $null
    $taskRendered++
    $devices = @($s.session.state.devices | Where-Object visible)
    $cards = @($s.view.cards)
    # Retain the ability to recheck archived observations of the earlier preview.
    # The new view explicitly supplies its current page and visual battery tone.
    $modern = $null -ne $s.view.PSObject.Properties['page'] -and $s.view.page -cin @('devices', 'settings')
    $connectionText = if ($modern) {
        @{ Online = @(''); Recent = @(''); Pending = @('暂无响应', 'Not responding') }
    } else { $taskLegacyConnectionText }
    $customizations = if ($null -ne $s.PSObject.Properties['settings'] -and $null -ne $s.settings) {
        @($s.settings.deviceCustomizations)
    } else { @() }
    if ($modern -and -not [string]::IsNullOrEmpty($s.view.summary)) {
        $taskChecks.Add('The main window displays the removed global status summary.')
    }
    if ($devices.Count -ne $cards.Count) { $taskChecks.Add('Visible device count differs from card count.'); continue }
    foreach ($device in $devices) {
        $matches = @($cards | Where-Object { $_.id -ceq $device.id })
        $customization = @($customizations | Where-Object { $_.deviceId -ceq $device.id })
        $expectedName = $device.name
        if ($customization.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($customization[0].name)) { $expectedName = $customization[0].name.Trim() }
        $usable = $null -ne $device.batteryPercent -and $device.batteryPercent -ge 0 -and $device.batteryPercent -le 100 -and
            $device.freshness -cin @('Current', 'Recent', 'Retained')
        $battery = if ($usable) { "$($device.batteryPercent)%" } else { '—' }
        if ($matches.Count -ne 1 -or $matches[0].name -cne $expectedName -or $matches[0].battery -cne $battery -or
            -not $connectionText.ContainsKey([string]$device.connection) -or
            $connectionText[[string]$device.connection] -cnotcontains $matches[0].connection) {
            $taskChecks.Add("Card fields differ from device $($device.id).")
            continue
        }
        if ($modern) {
            $card = $matches[0]
            $tone = if (-not $usable) { 'None' } elseif ($device.freshness -ceq 'Retained') { 'Stale' } else { 'Normal' }
            $failed = $device.connection -cin @('Online', 'Recent') -and $tone -cne 'Normal' -and
                @($device.sourceAttempts | Where-Object { $_.status -cin @('Error', 'Timeout') }).Count -gt 0
            $selected = @($device.readings | Where-Object {
                $_.source -ceq $device.batterySource -and $_.component -ceq 'device' -and
                $_.percent -eq $device.batteryPercent -and $_.freshness -ceq $device.freshness
            } | Select-Object -First 1)
            $charging = $tone -ceq 'Normal' -and $selected.Count -eq 1 -and $selected[0].charging -eq $true
            $barValue = if ($usable) { $device.batteryPercent } else { 0 }
            if ($card.tone -cne $tone -or $card.readFailed -ne $failed -or $card.charging -ne $charging -or
                $card.barVisible -ne ($device.connection -cne 'Pending') -or $card.barValue -ne $barValue -or
                -not [string]::IsNullOrEmpty($card.explanation)) {
                $taskChecks.Add("Card visual state differs from device $($device.id).")
            }
        }
    }
}
if ($taskRendered -eq 0) { $taskChecks.Add('No snapshot had a matching rendered state version.') }
if ($ExpectPolling -and $taskMaximumPollingGap -gt $MaximumPollingGapSeconds) { $taskChecks.Add('Observed scan generation progress exceeded the allowed polling gap.') }

$taskBuckets = @()
foreach ($group in ($taskSamples | Group-Object { [int][math]::Floor($_.elapsedSeconds / 300) } | Sort-Object { [int]$_.Name })) {
    $samples = @($group.Group)
    $elapsed = $samples[-1].elapsedSeconds - $samples[0].elapsedSeconds
    $cpu = $samples[-1].cpuMilliseconds - $samples[0].cpuMilliseconds
    $taskBuckets += [ordered]@{
        fromSeconds = [int]$group.Name * 300; sampleCount = $samples.Count
        handles = Get-Range $samples.handles; threads = Get-Range $samples.threads
        privateMiB = Get-Range @($samples | ForEach-Object { [math]::Round($_.privateBytes / 1MB, 2) })
        workingSetMiB = Get-Range @($samples | ForEach-Object { [math]::Round($_.workingSetBytes / 1MB, 2) })
        cpuPercentOfOneCore = if ($elapsed -gt 0) { [math]::Round($cpu / ($elapsed * 10), 3) } else { $null }
    }
}

$taskDrain = $null
if ($RequireDrain) {
    $logPath = Resolve-ProjectPath $taskResult.diagnosticLog
    $events = @(Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json })
    $ownStart = @($events | Where-Object { $_.kind -eq 'start' -and $_.data.processId -eq $taskResult.processId }).Count -gt 0
    $opened = @($events | Where-Object kind -eq 'hid-open' | ForEach-Object { $_.data.channelId })
    $closed = @($events | Where-Object kind -eq 'hid-closed' | ForEach-Object { $_.data.channelId })
    $unclosed = @($opened | Where-Object { $_ -notin $closed })
    $drained = @($events | Where-Object { $_.kind -eq 'session-stopped' -and $_.data.physicallyDrained }).Count -gt 0
    $limited = @($events | Where-Object kind -eq 'capture-limit').Count -gt 0
    $complete = @($events | Where-Object kind -eq 'closed').Count -gt 0
    $taskDrain = [ordered]@{ matchingProcess = $ownStart; physicallyDrained = $drained; openedVendorHidChannels = $opened.Count; closedVendorHidChannels = $closed.Count; unclosedChannels = $unclosed; captureLimited = $limited; recordingClosed = $complete }
    if (-not $ownStart -or -not $drained -or -not $complete -or $limited -or $unclosed.Count -gt 0) { $taskChecks.Add('Complete session and vendor HID drain evidence is missing.') }
}
$taskCompleted = @($taskSamples | Where-Object status -eq 'Completed')
$taskCompletedGenerations = @($taskCompleted | ForEach-Object { $_.generation } | Select-Object -Unique)
if ($taskCompletedGenerations.Count -eq 0) { $taskChecks.Add('No completed scan was observed.') }
$taskSummary = [ordered]@{
    processId = $taskResult.processId; durationSeconds = $taskResult.durationSeconds
    minimumDurationSeconds = $MinimumDurationSeconds; sampleCount = $taskSamples.Count
    matchingRenderSamples = $taskRendered; maximumObservedRenderLagSeconds = $taskMaxRenderLag
    endingViewCaughtUp = $null -eq $taskLagStart; snapshotFailureRecords = $taskFailures.Count
    pollingChecked = [bool]$ExpectPolling; maximumObservedPollingGapSeconds = $taskMaximumPollingGap
    allowedPollingGapSeconds = if ($ExpectPolling) { $MaximumPollingGapSeconds } else { $null }
    suspensionObserved = $taskSawSuspension
    completedGenerations = $taskCompletedGenerations.Count
    observedStatuses = @($taskSamples | Group-Object status | ForEach-Object { [ordered]@{ status = $_.Name; sampleCount = $_.Count } })
    checksPassed = $taskChecks.Count -eq 0; problems = @($taskChecks.ToArray() | Select-Object -Unique)
    fiveMinuteWindows = $taskBuckets; drain = $taskDrain
    interpretation = 'Checks cover sampled behavior and explicitly requested polling/drain evidence, not a performance budget or proof of no leak. A pending final render is reported separately. CPU is process time as percent of one logical core; five-minute windows expose trends for review.'
}
$taskOutput = Resolve-ProjectPath (Join-Path $taskDirectory 'summary.json')
[IO.File]::WriteAllText($taskOutput, ($taskSummary | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
if (-not $taskSummary.checksPassed) { throw "Observation checks failed; inspect $taskOutput" }
[pscustomobject]@{ summary = $taskOutput; checksPassed = $true; durationSeconds = $taskSummary.durationSeconds; completedGenerations = $taskSummary.completedGenerations; matchingRenderSamples = $taskRendered }
