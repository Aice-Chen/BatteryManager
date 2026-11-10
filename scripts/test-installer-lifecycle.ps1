# Builds isolated installers from inert fixture files. Installation requires explicit -Execute.
# No app launch, startup entry, shortcut, production AppId or uninstall registry entry is compiled in.
[CmdletBinding()]
param(
    [string]$CompilerPath = '.tools/inno-7.1.1/ISCC.exe',
    [string]$PreparedDirectory,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskBoundary = Join-Path $taskRoot '.artifacts/tests/installer-lifecycle'
$taskScript = Join-Path $taskRoot 'packaging/windows/BatteryManager.iss'
$taskCompiler = Resolve-PortableProjectPath $CompilerPath $taskRoot
$taskResults = [Collections.Generic.List[object]]::new()

function Read-ProtectedInstallerState {
    $state = [ordered]@{}
    foreach ($entry in @(
        @{key='Software\Microsoft\Windows\CurrentVersion\Run';name='BatteryManager'},
        @{key='Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';name='BatteryManager'}
    )) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($entry.key)
        try {
            $state[$entry.key] = if ($null -eq $key -or $entry.name -notin $key.GetValueNames()) { $null } else {
                @{kind=$key.GetValueKind($entry.name).ToString();value=$key.GetValue($entry.name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
            }
        } finally { if ($null -ne $key) { $key.Dispose() } }
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{99051A29-4531-489B-8808-FB079EA80C2C}_is1')
    try {
        $state['productionUninstall'] = if ($null -eq $key) { $null } else {
            $values = [ordered]@{}
            foreach ($name in ($key.GetValueNames() | Sort-Object)) { $values[$name] = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            $values
        }
    } finally { if ($null -ne $key) { $key.Dispose() } }
    Get-PortableTextHash ($state | ConvertTo-Json -Depth 8 -Compress)
}

function Invoke-IsolatedInstaller([string]$Exe, [string[]]$Arguments, [string]$Case) {
    $resolved = Resolve-PortableProjectPath $Exe $taskRoot -ArtifactsOnly
    if (-not $resolved.StartsWith($taskEvidence + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test process must remain inside the reviewed evidence directory.' }
    $process = Start-Process -FilePath $resolved -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    # Keep the caller responsive. Never terminate an installer in the middle of file operations.
    if (-not $process.WaitForExit(60000)) { throw "Installer test $Case is still running (PID $($process.Id)); inspect it before continuing." }
    $process.Refresh()
    $code = $process.ExitCode
    $taskResults.Add([pscustomobject]@{case=$Case;exe=$resolved;arguments=$Arguments;exitCode=$code})
    $code
}

if (-not $PreparedDirectory) {
    if ($Execute) { throw 'Prepare and review a test directory first, then pass it with -PreparedDirectory -Execute.' }
    if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) { throw 'A verified local compiler is required. This script never downloads tools.' }
    $taskId = [Guid]::NewGuid().ToString('N')
    $taskEvidence = Resolve-PortableProjectPath (Join-Path $taskBoundary ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskId)) $taskRoot -ArtifactsOnly
    $null = New-Item -ItemType Directory -Path $taskEvidence
    $taskInstallers = @()
    foreach ($revision in 1,2) {
        $payload = Join-Path $taskEvidence "payload-v$revision"
        $output = Join-Path $taskEvidence "compiled-v$revision"
        $null = New-Item -ItemType Directory -Path $payload,$output
        Write-PortableText (Join-Path $payload 'BatteryManager.App.exe') "Inert installer fixture, never executable. Revision $revision."
        Write-PortableText (Join-Path $payload 'version.txt') "$revision"
        $version = "0.0.$revision"
        $arguments = @('/Q', ('/DIsolationTestId=' + $taskId), ('/DIsolationRoot=' + $taskEvidence), ('/DPayloadDirectory=' + $payload), ('/DOutputDirectory=' + $output), ('/DAppVersion=' + $version), ('/DNumericVersion=' + $version + '.0'), $taskScript)
        & $taskCompiler @arguments 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Isolated installer v$revision did not compile." }
        $exe = Join-Path $output ("BatteryManager-$version-isolation-test-setup.exe")
        $taskInstallers += [pscustomobject]@{revision=$revision;path=$exe;sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();compileArguments=$arguments}
    }
    $negative = Join-Path $taskEvidence 'rejected-publishable-test'
    $null = New-Item -ItemType Directory -Path $negative
    & $taskCompiler '/Q' '/DPublishableBuild=1' ('/DIsolationTestId='+$taskId) ('/DIsolationRoot='+$taskEvidence) ('/DPayloadDirectory='+$payload) ('/DOutputDirectory='+$negative) '/DAppVersion=0.0.2' '/DNumericVersion=0.0.2.0' $taskScript 2>&1 |
        Tee-Object -FilePath (Join-Path $negative 'compile.log') | Out-Host
    if ($LASTEXITCODE -eq 0 -or @(Get-ChildItem -LiteralPath $negative -Filter '*.exe').Count) { throw 'A test installer was incorrectly accepted as publishable.' }
    $plan = [ordered]@{schemaVersion=1;testOnly=$true;id=$taskId;directory=$taskEvidence;target=(Join-Path $taskEvidence 'install target with spaces');appMutex=('Local\BatteryManager.App.Test.'+$taskId);appId=('BatteryManager.Isolation.'+$taskId);setupMutex=('Local\BatteryManager.Setup.Test.'+$taskId);sourceSha256=(Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash.ToLowerInvariant();installers=$taskInstallers;executed=$false;publishableTestRejected=$true}
    Write-PortableText (Join-Path $taskEvidence 'plan.json') ($plan | ConvertTo-Json -Depth 8)
    [pscustomobject]$plan
    return
}

$taskEvidence = Resolve-PortableProjectPath $PreparedDirectory $taskRoot -ArtifactsOnly
if (-not $taskEvidence.StartsWith($taskBoundary.Replace('/', '\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Prepared installer test directory is outside its dedicated boundary.' }
$plan = Get-Content -LiteralPath (Join-Path $taskEvidence 'plan.json') -Raw | ConvertFrom-Json
if (-not $plan.testOnly -or $plan.schemaVersion -ne 1 -or $plan.id -cnotmatch '^[0-9a-f]{32}$' -or
    $plan.directory -cne $taskEvidence -or $plan.target -cne (Join-Path $taskEvidence 'install target with spaces') -or
    $plan.appId -cne ('BatteryManager.Isolation.' + $plan.id) -or $plan.appMutex -cne ('Local\BatteryManager.App.Test.' + $plan.id) -or
    $plan.sourceSha256 -cne (Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'The prepared test plan is invalid or installer source changed.' }
foreach ($installer in $plan.installers) {
    $null = Resolve-PortableProjectPath $installer.path $taskRoot -ArtifactsOnly
    if (-not $installer.path.StartsWith($taskEvidence+'\', [StringComparison]::OrdinalIgnoreCase) -or
        $installer.sha256 -cne (Get-FileHash -LiteralPath $installer.path -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'An isolated installer changed after preparation.' }
}
if (-not $Execute) { $plan; return }
if (Test-Path -LiteralPath $plan.target) { throw 'The test install directory already exists; review it before starting another lifecycle run.' }
$taskBefore = Read-ProtectedInstallerState
$taskUserData = Join-Path $taskEvidence 'user-data'
$null = New-Item -ItemType Directory -Path $taskUserData
Write-PortableText (Join-Path $taskUserData 'settings.json') '{"fixture":"preserve-user-data"}'
$taskSettingsHash = (Get-FileHash -LiteralPath (Join-Path $taskUserData 'settings.json')).Hash
$taskBaseArgs = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/NOCLOSEAPPLICATIONS', '/NOFORCECLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS')
$taskFailure = $null
try {
    $mutex = [Threading.Mutex]::new($false, $plan.appMutex)
    try {
        $code = Invoke-IsolatedInstaller $plan.installers[0].path ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence 'blocked.log')+'"'))) 'running-app-blocks-install'
        if ($code -eq 0 -or (Test-Path -LiteralPath $plan.target)) { throw 'The application mutex did not prevent installation.' }
    } finally { $mutex.Dispose() }
    $wrong = Join-Path $taskEvidence 'wrong-target'
    $code = Invoke-IsolatedInstaller $plan.installers[0].path ($taskBaseArgs + @(('/DIR="'+$wrong+'"'), ('/LOG="'+(Join-Path $taskEvidence 'wrong-target.log')+'"'))) 'wrong-directory-rejected'
    if ($code -eq 0 -or (Test-Path -LiteralPath $wrong)) { throw 'The isolated installer accepted a directory outside its compiled target.' }
    foreach ($installer in $plan.installers) {
        $code = Invoke-IsolatedInstaller $installer.path ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence ('install-v'+$installer.revision+'.log'))+'"'))) ('install-v'+$installer.revision)
        if ($code -ne 0 -or (Get-Content -LiteralPath (Join-Path $plan.target 'version.txt') -Raw) -cne [string]$installer.revision -or
            -not (Test-Path -LiteralPath (Join-Path $plan.target 'installed.json'))) { throw "Installer revision $($installer.revision) did not apply correctly." }
        if (@(Get-ChildItem -LiteralPath $plan.target -Filter 'unins*.exe').Count -ne 1) { throw 'An upgrade failed to reuse the same uninstaller.' }
        $testKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$plan.appId+'_is1')
        if ($null -ne $testKey) { $testKey.Dispose(); throw 'The isolated installer unexpectedly registered an uninstall entry.' }
        if ($installer.revision -eq 1) { Write-PortableText (Join-Path $plan.target 'user-created.txt') 'Preserve untracked user data.' }
    }
    $uninstaller = (Get-ChildItem -LiteralPath $plan.target -Filter 'unins*.exe').FullName
    $code = Invoke-IsolatedInstaller $uninstaller ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall.log')+'"')) 'uninstall-preserves-data'
    if ($code -ne 0 -or (Test-Path -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')) -or
        (Get-Content -LiteralPath (Join-Path $plan.target 'user-created.txt') -Raw) -cne 'Preserve untracked user data.' -or
        (Get-FileHash -LiteralPath (Join-Path $taskUserData 'settings.json')).Hash -cne $taskSettingsHash) { throw 'Uninstall did not remove application files and preserve user data as expected.' }
} catch { $taskFailure = $_.Exception.Message }
$taskAfter = Read-ProtectedInstallerState
if ($taskBefore -cne $taskAfter) { $taskFailure = 'The protected startup or production uninstall state changed during testing.' }
$result = [ordered]@{passed=$null -eq $taskFailure;error=$taskFailure;directory=$taskEvidence;steps=$taskResults.ToArray();protectedStateUnchanged=$taskBefore -ceq $taskAfter;applicationStarted=$false;fixturePayload=$true;productionInstallTested=$false}
Write-PortableText (Join-Path $taskEvidence 'result.json') ($result | ConvertTo-Json -Depth 8)
if ($taskFailure) { throw $taskFailure }
[pscustomobject]$result
