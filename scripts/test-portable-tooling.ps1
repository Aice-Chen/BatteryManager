# Offline archive-integrity cases. Fixture executables are inert text and are never run.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskEvidence = Resolve-PortableProjectPath ('.artifacts/tests/portable-tooling-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')) $taskRoot -ArtifactsOnly
$null = New-Item -ItemType Directory -Path $taskEvidence
$taskResults = [Collections.Generic.List[object]]::new()

function Write-Fixture([string]$Case) {
    $version = switch ($Case) {
        'stable-valid' { '0.1.0' }
        'version-trailing-hyphen' { '0.1.0-' }
        'version-leading-zero' { '01.1.0' }
        'version-directory-mismatch' { '0.1.0' }
        'version-information-mismatch' { '0.1.0' }
        default { '0.1.0-preview.1' }
    }
    $directoryVersion = if ($Case -eq 'version-directory-mismatch') { '0.1.0-preview.1' } else { $version }
    $informationVersion = if ($Case -eq 'version-information-mismatch') { '0.1.0-preview.1' } else { $version }
    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($name in @('使用说明.txt', 'BatteryManager.App.exe', 'BatteryManager.App.dll', 'BatteryManager.App.pri', 'BatteryManager.App.deps.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll')) { $files.Add($name, 'Inert test fixture: ' + $name) }
    $files.Add('BatteryManager.App.runtimeconfig.json', '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.14"}]}}')
    $sourceManifest = ('a' * 64) + "  src/example.cs`n"
    $sourceCount=1
    if($Case -like 'font-*') {
        $license='Inert Barlow license fixture'
        $sourceManifest+=('b'*64)+"  src/BatteryManager.App/Assets/Fonts/BarlowSemiCondensed-Medium.ttf`n"+(Get-PortableTextHash $license)+"  src/BatteryManager.App/Assets/Fonts/OFL.txt`n"
        $sourceCount=3
        if($Case -ne 'font-missing-license'){$files.Add('licenses/Barlow-OFL.txt',$(if($Case -eq 'font-wrong-license'){'wrong license'}else{$license}))}
        if($Case -eq 'font-loose-ttf'){$files.Add('BarlowSemiCondensed-Medium.ttf','Inert font fixture')}
    }
    $sourceHash = Get-PortableTextHash $sourceManifest
    if ($Case -eq 'wrong-source') { $sourceHash = 'b' * 64 }
    $files.Add('source-files.sha256', $sourceManifest)
    $files.Add('build-info.json', (@{ schemaVersion = 1; product = 'Battery Manager'; version = $version; runtimeIdentifier = 'win-x64'; selfContained = $true; sourceFingerprint = $sourceHash; sourceFileCount = $sourceCount; informationalVersion = $informationVersion + '+source.' + $sourceHash } | ConvertTo-Json))
    if ($Case -eq 'external-runtime') { $files['BatteryManager.App.runtimeconfig.json'] = '{"runtimeOptions":{"includedFrameworks":[],"framework":{"name":"Microsoft.NETCore.App","version":"10.0.14"}}}' }
    if ($Case -eq 'personal-settings') { $files.Add('settings.json', '{"priorities":[]}') }
    if ($Case -eq 'personal-v1-backup') { $files.Add('settings.v1.bak', '{"version":1,"priorityDevices":[]}') }
    if ($Case -eq 'personal-v2-backup') { $files.Add('settings.v2.bak', '{"version":2,"priorityDevices":[]}') }
    if ($Case -eq 'personal-log') { $files.Add('logs/diagnostic-example.jsonl', '{"device":"private"}') }
    $lines = foreach ($file in $files.GetEnumerator()) { (Get-PortableTextHash $file.Value) + '  ' + $file.Key }
    $files.Add('SHA256SUMS.txt', ($lines -join "`n") + "`n")
    if ($Case -eq 'corrupt-content') { $files['BatteryManager.App.dll'] = 'changed after hashing' }
    if ($Case -eq 'missing-file') { $null = $files.Remove('hostfxr.dll') }
    if ($Case -eq 'unlisted-file') { $files.Add('extra.txt', 'not in manifest') }
    if ($Case -eq 'traversal') { $files.Add('../escape.txt', 'must never be extracted') }
    if ($Case -eq 'duplicate-path') { $files.Add('BATTERYMANAGER.APP.EXE', 'duplicate on Windows') }
    $path = Join-Path $taskEvidence ($Case + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files.GetEnumerator()) {
            $entry = $archive.CreateEntry('BatteryManager-' + $directoryVersion + '-win-x64/' + $file.Key)
            if ($Case -eq 'symlink' -and $file.Key -eq 'BatteryManager.App.exe') { $entry.ExternalAttributes = (0xA000 -shl 16) }
            $stream = $entry.Open()
            try { $data = [Text.Encoding]::UTF8.GetBytes($file.Value); $stream.Write($data, 0, $data.Length) }
            finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
    if ($Case -eq 'bad-sidecar') { Write-PortableText ($path + '.sha256') (('0' * 64) + '  ' + [IO.Path]::GetFileName($path) + "`n") }
    return $path
}

foreach ($case in @('valid', 'stable-valid', 'version-trailing-hyphen', 'version-leading-zero', 'version-directory-mismatch', 'version-information-mismatch', 'font-valid', 'font-missing-license', 'font-wrong-license', 'font-loose-ttf', 'corrupt-content', 'missing-file', 'unlisted-file', 'personal-settings', 'personal-v1-backup', 'personal-v2-backup', 'personal-log', 'traversal', 'duplicate-path', 'symlink', 'wrong-source', 'external-runtime', 'bad-sidecar')) {
    $path = Write-Fixture $case
    $accepted = $false; $failure = $null
    try { $result = & (Join-Path $PSScriptRoot 'test-portable-package.ps1') -PackagePath $path; $accepted = $result.passed }
    catch { $failure = $_.Exception.Message }
    $passed = $accepted -eq ($case -in @('valid','stable-valid','font-valid'))
    $taskResults.Add([pscustomobject]@{ case = $case; passed = $passed; accepted = $accepted; error = $failure })
}
foreach ($case in @(
    @{ name = 'stable'; prefix = '0.1.0'; suffix = ''; release = $false; expected = '0.1.0' },
    @{ name = 'release-entry'; prefix = '0.1.0'; suffix = ''; release = $true; expected = '0.1.0' },
    @{ name = 'preview'; prefix = '0.1.0'; suffix = 'preview.17'; release = $false; expected = '0.1.0-preview.17' },
    @{ name = 'release-rejects-preview'; prefix = '0.1.0'; suffix = 'preview.17'; release = $true; expected = $null },
    @{ name = 'prerelease-hyphen'; prefix = '1.2.3'; suffix = 'alpha-1.2'; release = $false; expected = '1.2.3-alpha-1.2' },
    @{ name = 'prefix-leading-zero'; prefix = '01.2.3'; suffix = ''; release = $false; expected = $null },
    @{ name = 'prefix-prerelease'; prefix = '1.2.3-preview'; suffix = ''; release = $false; expected = $null },
    @{ name = 'prefix-too-short'; prefix = '1.2'; suffix = ''; release = $false; expected = $null },
    @{ name = 'empty-identifier'; prefix = '1.2.3'; suffix = 'preview..1'; release = $false; expected = $null },
    @{ name = 'suffix-space'; prefix = '1.2.3'; suffix = ' '; release = $false; expected = $null },
    @{ name = 'suffix-newline'; prefix = '1.2.3'; suffix = "preview.1`n"; release = $false; expected = $null },
    @{ name = 'suffix-build-metadata'; prefix = '1.2.3'; suffix = 'preview+other'; release = $false; expected = $null },
    @{ name = 'suffix-numeric-leading-zero'; prefix = '1.2.3'; suffix = 'preview.01'; release = $false; expected = $null }
)) {
    $version = $null; $failure = $null
    try { $version = Get-PortableVersion $case.prefix $case.suffix -Release:$case.release }
    catch { $failure = $_.Exception.Message }
    $passed = if ($null -eq $case.expected) { $null -ne $failure } else { $null -eq $failure -and $version -ceq $case.expected }
    $taskResults.Add([pscustomobject]@{ case = 'version:' + $case.name; passed = $passed; accepted = $null -eq $failure; error = $failure })
}
foreach ($path in @('.artifacts/../../outside.zip', 'README.md')) {
    $rejected = $false
    try { $null = Resolve-PortableProjectPath $path $taskRoot -ArtifactsOnly } catch { $rejected = $true }
    $taskResults.Add([pscustomobject]@{ case = 'output-boundary:' + $path; passed = $rejected; accepted = -not $rejected; error = $null })
}
$taskFailed = @($taskResults | Where-Object { -not $_.passed })
Write-PortableText (Join-Path $taskEvidence 'result.json') (@{ passed = $taskFailed.Count -eq 0; cases = $taskResults.ToArray(); note = 'Offline synthetic archives only; no application process or hardware access.' } | ConvertTo-Json -Depth 8)
if ($taskFailed.Count) { throw "Portable tooling regression failed: $($taskFailed.case -join ', '). Evidence: $taskEvidence" }
[pscustomobject]@{ passed = $true; caseCount = $taskResults.Count; evidenceDirectory = $taskEvidence }
