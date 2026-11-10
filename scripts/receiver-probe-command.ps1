[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('mark','probe','quit')][string]$Action,
    [Parameter(Mandatory)][string]$Phase,
    [ValidateSet('all','mchose','lofree')][string]$Target = 'all',
    [string]$SessionDirectory,
    [string]$Note = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$probeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $SessionDirectory) { $SessionDirectory = (Get-Content -LiteralPath (Join-Path $probeRoot '.artifacts/receiver-state-current.path') -Raw).Trim() }
$probeDirectory = [IO.Path]::GetFullPath($SessionDirectory)
if (-not $probeDirectory.StartsWith((Join-Path $probeRoot '.artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Capture directory must be inside this project .artifacts directory.'
}
$probeEventsFile = Join-Path $probeDirectory 'events.jsonl'
$probeRequestFile = Join-Path $probeDirectory 'request.json'
function Read-ProbeEvents {
    @(Get-Content -LiteralPath $probeEventsFile | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { } # A concurrently written final line may not yet be complete.
    })
}
$probeEvents = Read-ProbeEvents
if (@($probeEvents | Where-Object kind -eq 'complete').Count -gt 0) { throw 'Capture has already completed.' }
$probeStart = $probeEvents | Where-Object kind -eq 'start' | Select-Object -First 1
if ($null -eq $probeStart) { throw 'Capture has no start record.' }
$probeExpected = @($probeStart.data.targets | ForEach-Object name | Where-Object { $Target -eq 'all' -or $_.StartsWith($Target + '-') })
if ($probeExpected.Count -eq 0) { throw 'No capture targets match the request.' }
$probeId = 1
if (Test-Path -LiteralPath $probeRequestFile) {
    $probePrior = Get-Content -LiteralPath $probeRequestFile -Raw | ConvertFrom-Json
    $probeId = [long]$probePrior.id + 1
    $probePriorTargets = @($probeStart.data.targets | ForEach-Object name | Where-Object { $probePrior.target -eq 'all' -or $_.StartsWith($probePrior.target + '-') })
    foreach ($probePriorTarget in $probePriorTargets) {
        $probeExpectedKind = if ($probePrior.action -eq 'probe' -and $probePriorTarget.EndsWith('-query')) { 'probe-complete' } else { 'mark' }
        if (@($probeEvents | Where-Object { $_.requestId -eq $probePrior.id -and $_.target -eq $probePriorTarget -and $_.kind -eq $probeExpectedKind }).Count -eq 0) {
            throw "Previous request is still pending for $probePriorTarget; do not overwrite it."
        }
    }
}
$probeRequest = [ordered]@{ id=$probeId; action=$Action; target=$Target; phase=$Phase }
$probeTempFile = Join-Path $probeDirectory 'request.tmp'
[IO.File]::WriteAllText($probeTempFile, ($probeRequest | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $probeTempFile -Destination $probeRequestFile -Force
[ordered]@{at=[DateTimeOffset]::UtcNow;request=$probeRequest;note=$Note;timing='Command issue time; physical user action may precede this time.'} | ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath (Join-Path $probeDirectory 'actions.jsonl')
$probeWait = [Diagnostics.Stopwatch]::StartNew()
while ($probeWait.Elapsed -lt [TimeSpan]::FromSeconds(20)) {
    $probeEvents = Read-ProbeEvents
    if (@($probeEvents | Where-Object kind -eq 'error').Count -gt 0) { throw 'Capture reported an error; inspect its events.' }
    if ($Action -eq 'quit') {
        $probeDone = $probeEvents | Where-Object kind -eq 'complete' | Select-Object -Last 1
        if ($null -ne $probeDone) {
            if (-not $probeDone.data.physicallyDrained -or $probeDone.data.failed -ne 0) { throw 'Capture did not finish cleanly.' }
            return [pscustomobject]@{id=$probeId;action=$Action;phase=$Phase;physicallyDrained=$true;eventsFile=$probeEventsFile}
        }
    } else {
        $probeAcknowledged = @($probeExpected | Where-Object {
            $probeTargetName = $_
            $probeExpectedKind = if ($Action -eq 'probe' -and $probeTargetName.EndsWith('-query')) { 'probe-complete' } else { 'mark' }
            @($probeEvents | Where-Object { $_.requestId -eq $probeId -and $_.target -eq $probeTargetName -and $_.kind -eq $probeExpectedKind }).Count -gt 0
        })
        if ($probeAcknowledged.Count -eq $probeExpected.Count) {
            return [pscustomobject]@{id=$probeId;action=$Action;phase=$Phase;acknowledged=$probeAcknowledged;eventsFile=$probeEventsFile}
        }
    }
    Start-Sleep -Milliseconds 250
}
throw 'Capture acknowledgement timed out. Do not submit another request until its state is inspected.'
