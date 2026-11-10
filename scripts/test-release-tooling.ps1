# Offline tests: no downloads, compiler execution, registry writes or software installation.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskChecks = 0
function Assert-Fails([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Expected validation to reject this fixture.' }
}
$taskAssets = @(
    [pscustomobject]@{kind='installer';fileName='BatteryManager-0.2.0-win-x64-setup.exe';sha256=('a'*64);size=[long]3},
    [pscustomobject]@{kind='portable';fileName='BatteryManager-0.2.0-win-x64.zip';sha256=('b'*64);size=[long]4}
)
$taskManifest = New-ReleaseManifest '0.2.0' $taskAssets
if ($taskManifest.assets.Count -ne 2 -or $taskManifest.version -cne '0.2.0') { throw 'Valid release fixture failed.' }; $taskChecks++
Assert-Fails { New-ReleaseManifest '0.2.0' @($taskAssets[0]) }; $taskChecks++
Assert-Fails { New-ReleaseManifest '0.2.0' @($taskAssets[0], $taskAssets[0]) }; $taskChecks++
Assert-Fails { New-ReleaseManifest '0.3.0' $taskAssets }; $taskChecks++
Assert-Fails { Assert-ReleaseAssetName '../BatteryManager-0.2.0-win-x64.zip' '0.2.0' 'portable' }; $taskChecks++
$taskAssets[0].size = [long]1GB + 1
Assert-Fails { New-ReleaseManifest '0.2.0' $taskAssets }; $taskChecks++
$taskAssets[0].size = [long]3
$taskAssets[0].sha256 = 'invalid'
Assert-Fails { New-ReleaseManifest '0.2.0' $taskAssets }; $taskChecks++
$taskDirectory = Resolve-PortableProjectPath ('.artifacts/tests/release-tooling/' + [Guid]::NewGuid().ToString('N')) $taskRoot -ArtifactsOnly
$null = New-Item -ItemType Directory -Path $taskDirectory -Force
$taskFixture = Join-Path $taskDirectory 'fixture.bin'
[IO.File]::WriteAllBytes($taskFixture, [byte[]](1,2,3))
$taskHash = (Get-FileHash -LiteralPath $taskFixture -Algorithm SHA256).Hash.ToLowerInvariant()
Write-PortableText ($taskFixture + '.sha256') ($taskHash + '  fixture.bin' + "`n")
if ((Assert-ReleaseSidecar $taskFixture) -cne $taskHash) { throw 'Valid checksum fixture failed.' }; $taskChecks++
[IO.File]::WriteAllBytes($taskFixture, [byte[]](1,2,4))
Assert-Fails { Assert-ReleaseSidecar $taskFixture }; $taskChecks++
foreach ($taskScript in @('package-installer.ps1','prepare-release.ps1','release-common.ps1')) {
    $taskTokens = $null; $taskErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $taskScript), [ref]$taskTokens, [ref]$taskErrors)
    if ($taskErrors.Count) { throw "PowerShell syntax errors in $taskScript" }; $taskChecks++
}
[pscustomobject]@{passed=$taskChecks; compilerExecuted=$false; installed=$false; evidenceDirectory=$taskDirectory}
