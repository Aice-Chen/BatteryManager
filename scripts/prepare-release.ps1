# Assemble a complete, immutable local delivery directory. Uploading is a separate operation.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PortablePackagePath,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$ReleaseNotesPath,
    [string]$OutputDirectory = '.artifacts/releases',
    [switch]$Prerelease
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskPortablePath = Resolve-PortableProjectPath $PortablePackagePath $taskRoot -ArtifactsOnly
$taskInstallerPath = Resolve-PortableProjectPath $InstallerPath $taskRoot -ArtifactsOnly
$taskNotesPath = Resolve-PortableProjectPath $ReleaseNotesPath $taskRoot
$taskBase = Resolve-PortableProjectPath $OutputDirectory $taskRoot -ArtifactsOnly
$taskVerified = & (Join-Path $PSScriptRoot 'test-portable-package.ps1') -PackagePath $taskPortablePath
$taskVersion = $taskVerified.version
if ($taskVersion.Contains('-') -ne [bool]$Prerelease) { throw 'Preview versions require -Prerelease; stable versions must not use it.' }
$taskBuildInfo = Join-Path (Split-Path -Parent $taskInstallerPath) 'installer-build.json'
if (-not (Test-Path -LiteralPath $taskBuildInfo -PathType Leaf)) { throw 'The installer build record is missing.' }
$taskInstallerInfo = Get-Content -LiteralPath $taskBuildInfo -Raw | ConvertFrom-Json
if ($null -eq $taskInstallerInfo.PSObject.Properties['testOnly'] -or $taskInstallerInfo.testOnly -isnot [bool] -or $taskInstallerInfo.testOnly) {
    throw 'Only a verified production installer build record can enter a release.'
}
if ($taskInstallerInfo.version -cne $taskVersion -or $taskInstallerInfo.portableSha256 -cne $taskVerified.archiveSha256 -or
    $taskInstallerInfo.payloadSourceFingerprint -cne $taskVerified.sourceFingerprint) { throw 'Installer and portable package were not built from the same payload.' }
$taskAssets = @()
foreach ($item in @(@{kind='installer';path=$taskInstallerPath}, @{kind='portable';path=$taskPortablePath})) {
    $taskHash = Assert-ReleaseSidecar $item.path
    $taskAssets += [pscustomobject]@{kind=$item.kind;fileName=[IO.Path]::GetFileName($item.path);sha256=$taskHash;size=[long](Get-Item -LiteralPath $item.path).Length}
    if ($item.kind -eq 'installer' -and $taskInstallerInfo.installerSha256 -cne $taskHash) { throw 'Installer differs from its build record.' }
}
$taskManifest = New-ReleaseManifest $taskVersion $taskAssets
if (-not (Test-Path -LiteralPath $taskNotesPath -PathType Leaf)) { throw 'Release notes are required.' }
$taskOutput = Join-Path $taskBase ($taskVersion + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskOutput
foreach ($path in @($taskPortablePath, $taskInstallerPath)) {
    Copy-Item -LiteralPath $path -Destination $taskOutput
    Copy-Item -LiteralPath ($path + '.sha256') -Destination $taskOutput
}
Copy-Item -LiteralPath $taskNotesPath -Destination (Join-Path $taskOutput 'release-notes.md')
Copy-Item -LiteralPath $taskBuildInfo -Destination (Join-Path $taskOutput 'installer-build.json')
Write-PortableText (Join-Path $taskOutput 'update-manifest.json') ($taskManifest | ConvertTo-Json -Depth 5)
$taskDelivery = [ordered]@{schemaVersion=1;version=$taskVersion;tag=('v'+$taskVersion);repository='Aice-Chen/BatteryManager';prerelease=[bool]$Prerelease;sourceFingerprint=$taskVerified.sourceFingerprint;directory=$taskOutput;uploaded=$false}
Write-PortableText (Join-Path $taskOutput 'delivery.json') ($taskDelivery | ConvertTo-Json -Depth 5)
[pscustomobject]$taskDelivery
