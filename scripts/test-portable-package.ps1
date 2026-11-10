[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [switch]$Extract
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskArchivePath = Resolve-PortableProjectPath $PackagePath $taskRoot -ArtifactsOnly
if (-not (Test-Path -LiteralPath $taskArchivePath -PathType Leaf)) { throw 'Package archive not found.' }
$taskArchiveHash = (Get-FileHash -LiteralPath $taskArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (Test-Path -LiteralPath ($taskArchivePath + '.sha256')) {
    $sidecar = Resolve-PortableProjectPath ($taskArchivePath + '.sha256') $taskRoot -ArtifactsOnly
    $expectedSidecar = $taskArchiveHash + '  ' + [IO.Path]::GetFileName($taskArchivePath)
    if ((Get-Content -LiteralPath $sidecar -Raw).TrimEnd("`r", "`n") -cne $expectedSidecar) { throw 'The archive does not match its SHA256 sidecar.' }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskArchivePath)
$taskExtracted = $null
function Read-PackageText($Entry) {
    if ($Entry.Length -gt 5MB) { throw 'Package metadata exceeds its size limit.' }
    $reader = [IO.StreamReader]::new($Entry.Open(), [Text.UTF8Encoding]::new($false, $true), $true)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    if ($taskArchive.Entries.Count -eq 0 -or $taskArchive.Entries.Count -gt 10000) { throw 'The package file count is invalid.' }
    $taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskFiles = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskPackageRoot = $null
    $taskTotalBytes = 0L
    foreach ($entry in $taskArchive.Entries) {
        $path = $entry.FullName.TrimEnd('/')
        if (-not (Test-PortableRelativePath $path) -or -not $taskSeen.Add($path)) { throw "Unsafe or duplicate package path: $($entry.FullName)" }
        if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Redirected archive entries are not allowed.' }
        $parts = $path.Split('/')
        if ($null -eq $taskPackageRoot) { $taskPackageRoot = $parts[0] }
        if ($parts[0] -cne $taskPackageRoot) { throw 'The archive must contain exactly one application directory.' }
        if ($entry.FullName.EndsWith('/')) {
            if ($entry.Length -ne 0) { throw 'An archive directory contains file data.' }
            continue
        }
        if ($parts.Count -lt 2) { throw 'Package files must be inside the application directory.' }
        $relative = $path.Substring($taskPackageRoot.Length + 1)
        Assert-PortablePayloadPath $relative
        $taskFiles.Add($relative, $entry)
        $taskTotalBytes += $entry.Length
        if ($taskTotalBytes -gt 1GB) { throw 'The expanded package exceeds the verification size limit.' }
    }
    foreach ($required in @('build-info.json', 'source-files.sha256', 'SHA256SUMS.txt', '使用说明.txt', 'BatteryManager.App.exe', 'BatteryManager.App.dll', 'BatteryManager.App.pri', 'BatteryManager.App.deps.json', 'BatteryManager.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll')) {
        if (-not $taskFiles.ContainsKey($required) -or $taskFiles[$required].Length -eq 0) { throw "A required package file is missing: $required" }
    }
    $taskChecksums = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in (Read-PackageText $taskFiles['SHA256SUMS.txt']).Split("`n")) {
        if (-not $line) { continue }
        if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid file checksum entry.' }
        $hash = $Matches[1]; $relative = $Matches[2]
        Assert-PortablePayloadPath $relative
        if ($relative -ieq 'SHA256SUMS.txt' -or $taskChecksums.ContainsKey($relative)) { throw 'Duplicate or self-referential checksum entry.' }
        $taskChecksums.Add($relative, $hash)
    }
    if ($taskChecksums.Count -ne $taskFiles.Count - 1) { throw 'The checksum manifest does not cover exactly the package contents.' }
    foreach ($record in $taskChecksums.GetEnumerator()) {
        if (-not $taskFiles.ContainsKey($record.Key)) { throw "Checksum refers to a missing file: $($record.Key)" }
        $stream = $taskFiles[$record.Key].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($actual -cne $record.Value) { throw "Checksum mismatch: $($record.Key)" }
    }
    $taskInfo = Read-PackageText $taskFiles['build-info.json'] | ConvertFrom-Json
    if ($taskInfo.schemaVersion -ne 1 -or $taskInfo.product -cne 'Battery Manager' -or $taskInfo.runtimeIdentifier -cne 'win-x64' -or $taskInfo.selfContained -ne $true -or
        -not (Test-PortableVersion $taskInfo.version) -or $taskInfo.sourceFingerprint -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid package build metadata.' }
    if ($taskPackageRoot -cne ('BatteryManager-' + $taskInfo.version + '-win-x64') -or
        $taskInfo.informationalVersion -cne ($taskInfo.version + '+source.' + $taskInfo.sourceFingerprint)) { throw 'Package directory, app version, and source fingerprint disagree.' }
    $taskSourceManifest = Read-PackageText $taskFiles['source-files.sha256']
    if ((Get-PortableTextHash $taskSourceManifest) -cne $taskInfo.sourceFingerprint) { throw 'The source manifest fingerprint is incorrect.' }
    $taskSourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskSourceHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $taskSourceManifest.Split("`n")) {
        if (-not $line) { continue }
        if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$' -or -not (Test-PortableRelativePath $Matches[2]) -or -not $taskSourcePaths.Add($Matches[2])) { throw 'Invalid source manifest path or hash.' }
        $taskSourceHashes.Add($Matches[2],$Matches[1])
    }
    if ($taskSourcePaths.Count -ne $taskInfo.sourceFileCount) { throw 'The source manifest file count is incorrect.' }
    if ($taskSourcePaths.Contains('src/BatteryManager.App/Assets/Fonts/BarlowSemiCondensed-Medium.ttf')) {
        if (-not $taskFiles.ContainsKey('licenses/Barlow-OFL.txt') -or
            -not $taskSourceHashes.ContainsKey('src/BatteryManager.App/Assets/Fonts/OFL.txt') -or
            $taskChecksums['licenses/Barlow-OFL.txt'] -cne $taskSourceHashes['src/BatteryManager.App/Assets/Fonts/OFL.txt']) { throw 'The embedded font license is missing or does not match its source.' }
        if (@($taskFiles.Keys | Where-Object { $_ -match '(?i)\.ttf$' }).Count) { throw 'The embedded tray font was also shipped as a loose TTF.' }
    }
    $taskRuntime = Read-PackageText $taskFiles['BatteryManager.App.runtimeconfig.json'] | ConvertFrom-Json
    if (@($taskRuntime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').Count -eq 0 -or
        $null -ne $taskRuntime.runtimeOptions.PSObject.Properties['framework'] -or $null -ne $taskRuntime.runtimeOptions.PSObject.Properties['frameworks']) { throw 'The app does not declare its included .NET runtime.' }
    if ($Extract) {
        $taskDestination = Resolve-PortableProjectPath ('.artifacts/package-checks/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')) $taskRoot -ArtifactsOnly
        $null = New-Item -ItemType Directory -Path $taskDestination
        [IO.Compression.ZipFile]::ExtractToDirectory($taskArchivePath, $taskDestination)
        $taskExtracted = Join-Path $taskDestination $taskPackageRoot
        $actualInformation = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskExtracted 'BatteryManager.App.dll')).ProductVersion
        if ($actualInformation -cne $taskInfo.informationalVersion) { throw 'The extracted application version does not match the package metadata.' }
    }
    [pscustomobject]@{
        passed = $true; version = $taskInfo.version; sourceFingerprint = $taskInfo.sourceFingerprint
        packagePath = $taskArchivePath; archiveSha256 = $taskArchiveHash; fileCount = $taskFiles.Count; expandedBytes = $taskTotalBytes
        extractedDirectory = $taskExtracted; previewExe = $(if ($taskExtracted) { Join-Path $taskExtracted 'BatteryManager.App.exe' } else { $null })
        appStarted = $false
    }
} finally { $taskArchive.Dispose() }
