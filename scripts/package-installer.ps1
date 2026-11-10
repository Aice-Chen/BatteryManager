[CmdletBinding()]
param(
    [string]$PortablePackagePath,
    [string]$OutputDirectory = '.artifacts/installers',
    [string]$CompilerPath = '.tools/inno-7.1.1/ISCC.exe',
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskCompiler = Resolve-PortableProjectPath $CompilerPath $taskRoot
if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) { throw 'No trusted local Inno compiler is available. Compiler acquisition is paused after a security-product alert. Supply an already verified project-local compiler with -CompilerPath; this script does not download or install tools.' }
$taskOutput = Resolve-PortableProjectPath $OutputDirectory $taskRoot -ArtifactsOnly
if (-not $PortablePackagePath) {
    $taskPortable = & (Join-Path $PSScriptRoot 'package-preview.ps1') -Release:$Release
    $PortablePackagePath = $taskPortable.packagePath
}
$taskPortablePath = Resolve-PortableProjectPath $PortablePackagePath $taskRoot -ArtifactsOnly
# The verifier rejects extra files, personal data, path traversal, links and mismatched hashes before extraction.
$taskVerified = & (Join-Path $PSScriptRoot 'test-portable-package.ps1') -PackagePath $taskPortablePath -Extract
$taskVersion = $taskVerified.version
if ($Release -and $taskVersion.Contains('-')) { throw 'A stable installer cannot use a preview payload.' }
Assert-ReleaseAssetName ([IO.Path]::GetFileName($taskPortablePath)) $taskVersion 'portable'
$taskRun = Join-Path $taskOutput ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskRun
$taskScript = Join-Path $taskRoot 'packaging/windows/BatteryManager.iss'
$taskMarker = Join-Path $taskRoot 'packaging/windows/installed.json'
$taskScriptHash = (Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash
$taskMarkerHash = (Get-FileHash -LiteralPath $taskMarker -Algorithm SHA256).Hash
$taskNumericVersion = $taskVersion.Split('-', 2)[0] + '.0'
$taskArguments = @('/Q', ('/DPayloadDirectory=' + $taskVerified.extractedDirectory), ('/DOutputDirectory=' + $taskRun), ('/DAppVersion=' + $taskVersion), ('/DNumericVersion=' + $taskNumericVersion), $taskScript)
& $taskCompiler @taskArguments 2>&1 | Tee-Object -FilePath (Join-Path $taskRun 'compile.log') | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Inno compiler failed with exit code $LASTEXITCODE." }
if ((Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash -cne $taskScriptHash -or
    (Get-FileHash -LiteralPath $taskMarker -Algorithm SHA256).Hash -cne $taskMarkerHash) { throw 'Installer sources changed during compilation; discard this output and rebuild.' }
$taskInstaller = Join-Path $taskRun ("BatteryManager-$taskVersion-win-x64-setup.exe")
if (-not (Test-Path -LiteralPath $taskInstaller -PathType Leaf) -or (Get-Item -LiteralPath $taskInstaller).Length -le 0) { throw 'The compiler did not produce an installer.' }
$taskHash = (Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
Write-PortableText ($taskInstaller + '.sha256') ($taskHash + '  ' + [IO.Path]::GetFileName($taskInstaller) + "`n")
$taskInfo = [ordered]@{
    schemaVersion = 1; version = $taskVersion; installerPath = $taskInstaller; installerSha256 = $taskHash
    portablePackagePath = $taskPortablePath; portableSha256 = $taskVerified.archiveSha256
    payloadSourceFingerprint = $taskVerified.sourceFingerprint
    installerScriptSha256 = $taskScriptHash.ToLowerInvariant(); installedMarkerSha256 = $taskMarkerHash.ToLowerInvariant()
    compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskCompiler).ProductVersion
    installed = $false; signed = $false
}
Write-PortableText (Join-Path $taskRun 'installer-build.json') ($taskInfo | ConvertTo-Json -Depth 5)
[pscustomobject]$taskInfo
