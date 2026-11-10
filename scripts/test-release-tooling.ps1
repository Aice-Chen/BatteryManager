# Offline validation of release metadata and integrity checks. No executables are launched.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskEvidence = Resolve-PortableProjectPath ('.artifacts/tests/release-tooling/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')) $taskRoot -ArtifactsOnly
$null = New-Item -ItemType Directory -Path $taskEvidence
$taskResults = [Collections.Generic.List[object]]::new()
function Test-ReleaseCase([string]$Name, [bool]$ShouldAccept, [scriptblock]$Action) {
    $failure = $null
    try { $null = & $Action } catch { $failure = $_.Exception.Message }
    $accepted = $null -eq $failure
    $taskResults.Add([pscustomobject]@{name=$Name;passed=$accepted -eq $ShouldAccept;accepted=$accepted;error=$failure})
}
function New-FixtureAssets([string]$Version='0.2.0') {
    ,@([pscustomobject]@{kind='installer';fileName="BatteryManager-$Version-win-x64-setup.exe";sha256=('a'*64);size=[long]123})
}
Test-ReleaseCase 'stable-manifest' $true {
    $manifest = New-ReleaseManifest '0.2.0' (New-FixtureAssets)
    if ($manifest.assets.Count -ne 1 -or $manifest.assets[0].kind -cne 'installer' -or $manifest.version -cne '0.2.0' -or $manifest.schemaVersion -ne 1) { throw 'Manifest content mismatch.' }
}
Test-ReleaseCase 'preview-manifest' $true { New-ReleaseManifest '0.2.0-preview.1' (New-FixtureAssets '0.2.0-preview.1') }
Test-ReleaseCase 'version-leading-zero' $false { New-ReleaseManifest '00.2.0' (New-FixtureAssets '00.2.0') }
Test-ReleaseCase 'mismatched-version-name' $false { New-ReleaseManifest '0.3.0' (New-FixtureAssets) }
Test-ReleaseCase 'test-installer-not-publishable' $false { $a=New-FixtureAssets; $a[0].fileName='BatteryManager-0.2.0-isolation-test-setup.exe'; New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'path-traversal' $false { $a=New-FixtureAssets; $a[0].fileName='../BatteryManager-0.2.0-win-x64-setup.exe'; New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'missing-installer' $false { New-ReleaseManifest '0.2.0' @() }
Test-ReleaseCase 'portable-only-not-publishable' $false {
    New-ReleaseManifest '0.2.0' @([pscustomobject]@{kind='portable';fileName='BatteryManager-0.2.0-win-x64.zip';sha256=('b'*64);size=[long]456})
}
Test-ReleaseCase 'legacy-dual-package-not-publishable' $false {
    $a=New-FixtureAssets
    New-ReleaseManifest '0.2.0' @($a[0],[pscustomobject]@{kind='portable';fileName='BatteryManager-0.2.0-win-x64.zip';sha256=('b'*64);size=[long]456})
}
Test-ReleaseCase 'duplicate-kind' $false { $a=New-FixtureAssets; New-ReleaseManifest '0.2.0' @($a[0],$a[0]) }
Test-ReleaseCase 'unknown-kind' $false { $a=New-FixtureAssets; $a[0].kind='tool'; New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'short-hash' $false { $a=New-FixtureAssets; $a[0].sha256='abc'; New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'invalid-hash-character' $false { $a=New-FixtureAssets; $a[0].sha256=('z'*64); New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'empty-file' $false { $a=New-FixtureAssets; $a[0].size=[long]0; New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'oversized-file' $false { $a=New-FixtureAssets; $a[0].size=[long]1GB+1; New-ReleaseManifest '0.2.0' $a }
Test-ReleaseCase 'size-string' $false { $a=New-FixtureAssets; $a[0].size='123'; New-ReleaseManifest '0.2.0' $a }
$taskFixture = Join-Path $taskEvidence 'inert-asset.txt'
Write-PortableText $taskFixture 'Inert release tooling fixture.'
$taskHash = (Get-FileHash -LiteralPath $taskFixture -Algorithm SHA256).Hash.ToLowerInvariant()
Test-ReleaseCase 'missing-sidecar' $false { Assert-ReleaseSidecar $taskFixture }
Write-PortableText ($taskFixture+'.sha256') ($taskHash+'  inert-asset.txt'+"`n")
Test-ReleaseCase 'matching-sidecar' $true { if ((Assert-ReleaseSidecar $taskFixture) -cne $taskHash) { throw 'Unexpected asset hash.' } }
Write-PortableText $taskFixture 'Changed after the checksum was made.'
Test-ReleaseCase 'tampered-asset' $false { Assert-ReleaseSidecar $taskFixture }
Write-PortableText $taskFixture 'Inert release tooling fixture.'
Write-PortableText ($taskFixture+'.sha256') ($taskHash+'  renamed-asset.txt'+"`n")
Test-ReleaseCase 'sidecar-other-file-name' $false { Assert-ReleaseSidecar $taskFixture }
foreach ($taskFile in @('package-installer.ps1','prepare-release.ps1','release-common.ps1','test-installer-lifecycle.ps1')) {
    Test-ReleaseCase ('syntax:'+ $taskFile) $true {
        $tokens=$null; $errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $taskFile), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw "PowerShell syntax errors in $taskFile" }
    }
}
$taskFailed = @($taskResults | Where-Object { -not $_.passed })
$result = [ordered]@{passed=$taskFailed.Count -eq 0;caseCount=$taskResults.Count;cases=$taskResults.ToArray();evidenceDirectory=$taskEvidence;executablesLaunched=$false}
Write-PortableText (Join-Path $taskEvidence 'result.json') ($result | ConvertTo-Json -Depth 6)
if ($taskFailed.Count) { throw "Release tooling failed: $($taskFailed.name -join ', ')" }
[pscustomobject]@{passed=$true;caseCount=$taskResults.Count;evidenceDirectory=$taskEvidence}
