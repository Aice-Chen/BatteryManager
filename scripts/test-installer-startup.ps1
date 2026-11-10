# Actual Inno task selection using inert files and a private HKCU subtree.
# Does not register an uninstaller, shortcut, real startup entry, or launch the app.
[CmdletBinding()]
param([string]$CompilerPath = '.tools/inno-7.1.1/ISCC.exe', [string]$PreparedDirectory, [switch]$Execute)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskBoundary = Join-Path $taskRoot '.artifacts/tests/installer-startup'
$taskScript = Join-Path $taskRoot 'packaging/windows/BatteryManager.iss'
$taskCompiler = Resolve-PortableProjectPath $CompilerPath $taskRoot

function Get-RegistryValueSnapshot([string]$Path, [string]$Name = 'BatteryManager') {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    try {
        if ($null -eq $key -or $Name -notin $key.GetValueNames()) { return $null }
        [ordered]@{kind=$key.GetValueKind($Name).ToString();value=$key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
    } finally { if ($null -ne $key) { $key.Dispose() } }
}
function Get-ProtectedStateHash {
    $values = [ordered]@{}
    foreach ($path in @('Software\Microsoft\Windows\CurrentVersion\Run', 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')) {
        $values[$path] = Get-RegistryValueSnapshot $path
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{99051A29-4531-489B-8808-FB079EA80C2C}_is1')
    try {
        $values['uninstall'] = $null
        if ($null -ne $key) {
            $entries = [ordered]@{}
            foreach ($name in ($key.GetValueNames() | Sort-Object)) { $entries[$name] = $key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            $values['uninstall'] = $entries
        }
    } finally { if ($null -ne $key) { $key.Dispose() } }
    Get-PortableTextHash ($values | ConvertTo-Json -Depth 8 -Compress)
}

if (-not $PreparedDirectory) {
    if ($Execute) { throw 'Prepare first, then execute the reviewed plan.' }
    $id = [Guid]::NewGuid().ToString('N')
    $directory = Resolve-PortableProjectPath (Join-Path $taskBoundary ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$id)) $taskRoot -ArtifactsOnly
    $payload = Join-Path $directory 'payload'
    $output = Join-Path $directory 'compiled'
    $null = New-Item -ItemType Directory -Path $directory,$payload,$output
    Write-PortableText (Join-Path $payload 'BatteryManager.App.exe') 'Inert installer startup fixture; never execute.'
    $arguments = @('/Q','/DStartupTaskTest=1',('/DIsolationTestId='+$id),('/DIsolationRoot='+$directory),('/DPayloadDirectory='+$payload),('/DOutputDirectory='+$output),'/DAppVersion=0.0.1','/DNumericVersion=0.0.1.0',$taskScript)
    & $taskCompiler @arguments 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Startup test installer did not compile.' }
    $exe = Join-Path $output 'BatteryManager-0.0.1-isolation-test-setup.exe'
    # Verify the production branch without generating or running a production installer.
    & $taskCompiler '/Q' '/O-' '/DPublishableBuild=1' ('/DPayloadDirectory='+$payload) ('/DOutputDirectory='+$output) '/DAppVersion=0.0.1' '/DNumericVersion=0.0.1.0' $taskScript 2>&1 |
        Tee-Object -FilePath (Join-Path $directory 'production-compile-no-output.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Production startup branch did not compile.' }
    & $taskCompiler '/Q' '/O-' '/DPublishableBuild=1' '/DStartupTaskTest=1' ('/DPayloadDirectory='+$payload) ('/DOutputDirectory='+$output) '/DAppVersion=0.0.1' '/DNumericVersion=0.0.1.0' $taskScript 2>&1 |
        Tee-Object -FilePath (Join-Path $directory 'rejected-production-test.log') | Out-Host
    if ($LASTEXITCODE -eq 0) { throw 'Production accepted startup test registry paths.' }
    $plan = [ordered]@{schemaVersion=1;testOnly=$true;id=$id;directory=$directory;target=(Join-Path $directory 'install target with spaces');registryRoot=('Software\BatteryManager\InstallerTaskTests\'+$id);installer=$exe;installerSha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant();sourceSha256=(Get-FileHash -LiteralPath $taskScript).Hash.ToLowerInvariant();compileArguments=$arguments;executed=$false}
    Write-PortableText (Join-Path $directory 'plan.json') ($plan | ConvertTo-Json -Depth 6)
    [pscustomobject]$plan
    return
}

$taskEvidence = Resolve-PortableProjectPath $PreparedDirectory $taskRoot -ArtifactsOnly
if (-not $taskEvidence.StartsWith($taskBoundary+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Wrong startup test boundary.' }
$plan = Get-Content -LiteralPath (Join-Path $taskEvidence 'plan.json') -Raw | ConvertFrom-Json
if ($plan.schemaVersion -ne 1 -or -not $plan.testOnly -or $plan.id -cnotmatch '\A[0-9a-f]{32}\z' -or $plan.directory -cne $taskEvidence -or
    $plan.target -cne (Join-Path $taskEvidence 'install target with spaces') -or
    $plan.registryRoot -cne ('Software\BatteryManager\InstallerTaskTests\'+$plan.id) -or
    $plan.installer -cne (Join-Path $taskEvidence 'compiled/BatteryManager-0.0.1-isolation-test-setup.exe') -or
    $plan.installerSha256 -cne (Get-FileHash -LiteralPath $plan.installer).Hash.ToLowerInvariant() -or
    $plan.sourceSha256 -cne (Get-FileHash -LiteralPath $taskScript).Hash.ToLowerInvariant()) { throw 'Test plan, installer, or source changed.' }
if (-not $Execute) { $plan; return }
if (Test-Path -LiteralPath $plan.target) { throw 'The reviewed installation target already exists.' }
$existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($plan.registryRoot)
if ($null -ne $existing) { $existing.Dispose(); throw 'The private test registry subtree already exists.' }
$taskRunKey = $plan.registryRoot+'\Run'
$taskApprovalKey = $plan.registryRoot+'\StartupApproved'
$taskExpectedCommand = '"'+(Join-Path $plan.target 'BatteryManager.App.exe')+'"'
$taskResults = [Collections.Generic.List[object]]::new()
$taskBaseArgs = @('/SP-','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/NOCLOSEAPPLICATIONS','/NOFORCECLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS')

function Set-TestRun([string]$Command) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($taskRunKey)
    try {
        if ($Command) { $key.SetValue('BatteryManager',$Command,[Microsoft.Win32.RegistryValueKind]::String) }
        else { $key.DeleteValue('BatteryManager',$false) }
    } finally { $key.Dispose() }
}
function Invoke-TestProcess([string]$Exe, [string[]]$Arguments) {
    $resolved = Resolve-PortableProjectPath $Exe $taskRoot -ArtifactsOnly
    if (-not $resolved.StartsWith($taskEvidence+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Process escaped the test boundary.' }
    $process = Start-Process -FilePath $resolved -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(30000)) { throw "Installer still running: PID $($process.Id). It was not terminated." }
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "Installer exited with $($process.ExitCode)." }
    } finally { $process.Dispose() }
}
function Test-StartupCase([string]$Name, [string[]]$TaskArguments, [string]$Expected) {
    if ($plan.installerSha256 -cne (Get-FileHash -LiteralPath $plan.installer).Hash.ToLowerInvariant()) { throw 'The reviewed installer changed.' }
    $approvalBefore = Get-RegistryValueSnapshot $taskApprovalKey | ConvertTo-Json -Compress
    $log = Join-Path $taskEvidence ($Name+'.log')
    Invoke-TestProcess $plan.installer ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'),('/LOG="'+$log+'"')) + $TaskArguments)
    $run = Get-RegistryValueSnapshot $taskRunKey
    $approvalAfter = Get-RegistryValueSnapshot $taskApprovalKey | ConvertTo-Json -Compress
    $passed = if ($Expected) { $null -ne $run -and $run.kind -eq 'String' -and $run.value -ceq $Expected } else { $null -eq $run }
    $passed = $passed -and $approvalBefore -ceq $approvalAfter
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\BatteryManager.Isolation.'+$plan.id+'_is1')
    if ($null -ne $key) { $key.Dispose(); throw 'The test installer wrote an uninstall registration.' }
    $taskResults.Add([pscustomobject]@{name=$Name;passed=$passed;run=$run;approvalUnchanged=$approvalBefore -ceq $approvalAfter;log=$log})
    if (-not $passed) { throw "Unexpected startup state in $Name." }
}

$before = Get-ProtectedStateHash
$failure = $null
try {
    Test-StartupCase '01-initial-default-off' @() ''
    Test-StartupCase '02-tasks-explicit-on' @('/TASKS=startup') $taskExpectedCommand
    Set-TestRun ('"'+(Join-Path $taskEvidence 'old portable/BatteryManager.App.exe')+'" --startup')
    Test-StartupCase '03-registered-silent-upgrade' @() $taskExpectedCommand
    Test-StartupCase '04-tasks-explicit-off' @('/TASKS=!startup') ''
    Test-StartupCase '05-mergetasks-explicit-on' @('/MERGETASKS=startup') $taskExpectedCommand
    Set-TestRun '' # Simulate the app disabling its own entry after installation.
    Test-StartupCase '06-app-disabled-stays-off' @() ''
    Set-TestRun $taskExpectedCommand
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($taskApprovalKey)
    try { $key.SetValue('BatteryManager',[byte[]]@(3,0,0,0,1,2,3,4,5,6,7,8),[Microsoft.Win32.RegistryValueKind]::Binary) } finally { $key.Dispose() }
    Test-StartupCase '07-windows-blocked-preserved' @() $taskExpectedCommand
    $unknown = '"C:\Windows\System32\notepad.exe"'
    Set-TestRun $unknown
    Test-StartupCase '08-unknown-run-preserved' @('/TASKS=startup') $unknown
    $uninstaller = @(Get-ChildItem -LiteralPath $plan.target -Filter 'unins*.exe')
    if ($uninstaller.Count -ne 1) { throw 'Expected one isolated uninstaller.' }
    Invoke-TestProcess $uninstaller[0].FullName ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall.log')+'"'))
    if (Test-Path -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')) { throw 'Isolated uninstall left the fixture payload.' }
} catch { $failure = $_.Exception.Message }
$after = Get-ProtectedStateHash
if ($before -cne $after) { $failure = 'Protected production registry state changed during the test.' }
# This exact random subtree was absent before this test. Keep failed runs intact for inspection.
if ($null -eq $failure) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($plan.registryRoot,$false) }
$result = [ordered]@{passed=$null -eq $failure;error=$failure;caseCount=$taskResults.Count;cases=$taskResults.ToArray();protectedStateUnchanged=$before -ceq $after;productionInstallerExecuted=$false;applicationStarted=$false;registryTestRootRemoved=$null -eq $failure}
Write-PortableText (Join-Path $taskEvidence 'result.json') ($result | ConvertTo-Json -Depth 8)
if ($failure) { throw $failure }
[pscustomobject]$result
