[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$PipeSuffix,
    [Parameter(Mandatory)][int]$AppProcessId,
    [ValidateRange(1, 3600)][int]$DurationSeconds = 300,
    [ValidateRange(1, 30)][int]$SampleSeconds = 2,
    [string]$ArtifactsDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = '.artifacts/hardware/app-watch-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') }
$taskOutput = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ArtifactsDirectory)) { $ArtifactsDirectory } else { Join-Path $taskRoot $ArtifactsDirectory }))
if (-not $taskOutput.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must remain in the project.' }
$taskCursor = $taskOutput
while ($taskCursor.Length -gt $taskRoot.Length) {
    if ((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence must not traverse a redirected path.' }
    $taskCursor = [IO.Path]::GetDirectoryName($taskCursor)
}
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a new evidence directory.' }
$taskProcess = Get-Process -Id $AppProcessId
$taskStarted = $taskProcess.StartTime.ToUniversalTime()
$taskExe = (Resolve-Path -LiteralPath (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')).Path
if ($taskProcess.Path -ne $taskExe) { throw 'The process is not this project preview.' }
$null = [IO.Directory]::CreateDirectory($taskOutput)
$taskWriter = [IO.StreamWriter]::new((Join-Path $taskOutput 'snapshots.jsonl'), $false, [Text.UTF8Encoding]::new($false))
$taskWriter.AutoFlush = $true
$taskSamples = [Collections.Generic.List[object]]::new()
$taskErrors = [Collections.Generic.List[object]]::new()
$taskMismatch = [Collections.Generic.List[string]]::new()
$taskTimer = [Diagnostics.Stopwatch]::StartNew()
$taskObservedAt = [DateTimeOffset]::UtcNow
$taskDiagnostic = $null
try {
    while ($taskTimer.Elapsed.TotalSeconds -lt $DurationSeconds -and -not (Test-Path -LiteralPath (Join-Path $taskOutput 'stop.flag'))) {
        $taskProcess.Refresh()
        if ($taskProcess.HasExited -or $taskProcess.StartTime.ToUniversalTime() -ne $taskStarted) { throw 'The watched process exited or changed.' }
        try {
            $reply = & (Join-Path $PSScriptRoot 'app-test-client.ps1') -PipeSuffix $PipeSuffix -Command status -ConnectTimeoutMilliseconds 1000 -ResponseTimeoutMilliseconds 5000
            $s = $reply.status
            $taskDiagnostic = $s.diagnosticLog
            $rendered = $s.view.version -eq $s.session.state.version
            if ($rendered) {
                $devices = @($s.session.state.devices | Where-Object { $_.visible })
                $cards = @($s.view.cards)
                if ($devices.Count -ne $cards.Count) { $taskMismatch.Add('Visible count differs from card count.') }
                foreach ($device in $devices) {
                    $matches = @($cards | Where-Object { $_.id -ceq $device.id })
                    $battery = if ($null -eq $device.batteryPercent) { '—' } else { "$($device.batteryPercent)%" }
                    if ($matches.Count -ne 1 -or $matches[0].battery -cne $battery) { $taskMismatch.Add("Card mismatch: $($device.id)") }
                }
            }
            $sample = [pscustomobject]@{
                at = [DateTimeOffset]::UtcNow; elapsedSeconds = $taskTimer.Elapsed.TotalSeconds
                handles = $taskProcess.HandleCount; threads = $taskProcess.Threads.Count
                privateBytes = $taskProcess.PrivateMemorySize64; workingSetBytes = $taskProcess.WorkingSet64
                cpuMilliseconds = $taskProcess.TotalProcessorTime.TotalMilliseconds
                generation = $s.session.generation; status = $s.session.status
                outstanding = $s.session.outstandingOperations; rendered = $rendered; power = $s.power
            }
            $taskSamples.Add($sample)
            $taskWriter.WriteLine((@{ sample = $sample; status = $s } | ConvertTo-Json -Depth 100 -Compress))
        }
        catch {
            $problem = @{ at = [DateTimeOffset]::UtcNow; error = $_.Exception.Message }
            $taskErrors.Add($problem)
            $taskWriter.WriteLine((@{ failure = $problem } | ConvertTo-Json -Compress))
        }
        Start-Sleep -Seconds $SampleSeconds
    }
}
finally {
    $taskWriter.Dispose()
    $taskResult = [ordered]@{
        processId = $AppProcessId; durationSeconds = $taskTimer.Elapsed.TotalSeconds
        requestedDurationSeconds = $DurationSeconds; sampleSeconds = $SampleSeconds
        processStartedAt = $taskStarted; observationStartedAt = $taskObservedAt; finishedAt = [DateTimeOffset]::UtcNow
        logicalProcessorCount = [Environment]::ProcessorCount
        sampleCount = $taskSamples.Count; errors = @($taskErrors.ToArray()); mismatches = @($taskMismatch.ToArray())
        diagnosticLog = $taskDiagnostic; samples = @($taskSamples.ToArray())
        note = 'Read-only observation of the existing app; no second hardware collection host. Duration limits the stability conclusion.'
    }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'result.json'), ($taskResult | ConvertTo-Json -Depth 20))
    $taskProcess.Dispose()
}
[pscustomobject]@{ evidenceDirectory = $taskOutput; sampleCount = $taskSamples.Count; errors = $taskErrors.Count; mismatches = $taskMismatch.Count }
