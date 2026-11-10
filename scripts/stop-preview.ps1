[CmdletBinding()]
param(
    [string]$PreviewExe = '.artifacts/preview/BatteryManager.App.exe',
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$taskCandidate = if ([IO.Path]::IsPathRooted($PreviewExe)) { $PreviewExe } else { Join-Path $taskRoot $PreviewExe }
$taskExe = [IO.Path]::GetFullPath($taskCandidate)
if (-not $taskExe.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The preview must be inside this project.' }
$taskCursor = $taskExe
while ($taskCursor.Length -gt $taskRoot.Length) {
    if ((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The preview path must not traverse a redirected path.' }
    $taskCursor = [IO.Path]::GetDirectoryName($taskCursor)
}
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw "Preview executable not found: $taskExe" }
$taskTargets = @(Get-Process -Name 'BatteryManager.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $taskExe })
if ($taskTargets.Count -eq 0) { [pscustomobject]@{ outcome = 'NotRunning'; processId = $null }; return }
if ($taskTargets.Count -ne 1) { throw 'More than one matching preview is running; no exit request was sent.' }
$taskTarget = $taskTargets[0]
$taskHelper = $null
try {
    $taskHelper = Start-Process -FilePath $taskExe -ArgumentList @('--quit', [string]$taskTarget.Id) -WorkingDirectory ([IO.Path]::GetDirectoryName($taskExe)) -WindowStyle Hidden -PassThru
    if (-not $taskHelper.WaitForExit(10000)) { throw 'The exit request helper has not finished. No process was forcibly stopped.' }
    if ($taskHelper.ExitCode -ne 0) { throw 'The preview did not accept the exit request. No process was forcibly stopped.' }
    # Posting a request is not completion: wait for the real app to drain and leave.
    if (-not $taskTarget.WaitForExit($TimeoutSeconds * 1000)) { throw 'The preview is still draining its operations. Wait and retry; it has not been forcibly stopped.' }
    [pscustomobject]@{ outcome = 'Exited'; processId = $taskTarget.Id }
}
finally {
    if ($null -ne $taskHelper) { $taskHelper.Dispose() }
    $taskTarget.Dispose()
}
