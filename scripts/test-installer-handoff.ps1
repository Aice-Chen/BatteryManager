# Controlled process-exit handoff tests. Preparation compiles only; -Execute is a separate reviewed step.
[CmdletBinding()]
param([string]$PreparedDirectory, [switch]$Execute, [string]$CompilerPath='.tools/inno-7.1.1/ISCC.exe')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskBoundary=Join-Path $taskRoot '.artifacts/tests/installer-handoff'
$taskCompiler=Resolve-PortableProjectPath $CompilerPath $taskRoot
$taskScript=Join-Path $taskRoot 'packaging/windows/BatteryManager.iss'
$taskDotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskSteps=[Collections.Generic.List[object]]::new()

if (-not $PreparedDirectory) {
    if ($Execute) { throw 'Prepare and review the isolated plan before executing it.' }
    $id=[Guid]::NewGuid().ToString('N')
    $directory=Resolve-PortableProjectPath (Join-Path $taskBoundary ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$id)) $taskRoot -ArtifactsOnly
    $null=New-Item -ItemType Directory -Path $directory
    $fixture=Join-Path $directory 'fixture-build'
    & $taskDotnet publish (Join-Path $taskRoot 'tools/BatteryManager.InstallerFixture/BatteryManager.InstallerFixture.csproj') -c Release -r win-x64 --self-contained false -o $fixture --nologo 2>&1 |
        Tee-Object -FilePath (Join-Path $directory 'fixture-build.log') | Out-Host
    if($LASTEXITCODE -ne 0){throw 'The process fixture did not build.'}
    $payload=Join-Path $directory 'payload'
    $output=Join-Path $directory 'compiled'
    $null=New-Item -ItemType Directory -Path $payload,$output
    Write-PortableText (Join-Path $payload 'BatteryManager.App.exe') 'Inert upgraded fixture; never execute.'
    Write-PortableText (Join-Path $payload 'version.txt') 'upgraded'
    $arguments=@('/Q',('/DIsolationTestId='+$id),('/DIsolationRoot='+$directory),'/DHandoffWaitMilliseconds=2500',('/DPayloadDirectory='+$payload),('/DOutputDirectory='+$output),'/DAppVersion=0.0.3','/DNumericVersion=0.0.3.0',$taskScript)
    & $taskCompiler @arguments 2>&1 | Tee-Object -FilePath (Join-Path $directory 'compile.log') | Out-Host
    if($LASTEXITCODE -ne 0){throw 'The handoff installer did not compile.'}
    $exe=Join-Path $output 'BatteryManager-0.0.3-isolation-test-setup.exe'
    $plan=[ordered]@{schemaVersion=1;testOnly=$true;id=$id;directory=$directory;target=(Join-Path $directory 'install target with spaces');appMutex=('Local\BatteryManager.App.Test.'+$id);appId=('BatteryManager.Isolation.'+$id);installer=$exe;installerSha256=(Get-FileHash -LiteralPath $exe).Hash;sourceSha256=(Get-FileHash -LiteralPath $taskScript).Hash;fixture=$fixture;fixtureSha256=(Get-FileHash -LiteralPath (Join-Path $fixture 'BatteryManager.App.exe')).Hash;waitMilliseconds=2500;executed=$false}
    Write-PortableText (Join-Path $directory 'plan.json') ($plan|ConvertTo-Json -Depth 5)
    [pscustomobject]$plan
    return
}
$taskEvidence=Resolve-PortableProjectPath $PreparedDirectory $taskRoot -ArtifactsOnly
if(-not $taskEvidence.StartsWith($taskBoundary.Replace('/','\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Handoff test directory is outside its boundary.'}
$plan=Get-Content -LiteralPath (Join-Path $taskEvidence 'plan.json') -Raw|ConvertFrom-Json
if(-not $plan.testOnly -or $plan.id -cnotmatch '^[0-9a-f]{32}$' -or $plan.directory -cne $taskEvidence -or
    $plan.target -cne (Join-Path $taskEvidence 'install target with spaces') -or $plan.fixture -cne (Join-Path $taskEvidence 'fixture-build') -or
    $plan.appMutex -cne ('Local\BatteryManager.App.Test.'+$plan.id) -or $plan.installer -cne (Join-Path $taskEvidence 'compiled/BatteryManager-0.0.3-isolation-test-setup.exe') -or
    $plan.installerSha256 -cne (Get-FileHash -LiteralPath $plan.installer).Hash -or $plan.sourceSha256 -cne (Get-FileHash -LiteralPath $taskScript).Hash -or
    $plan.fixtureSha256 -cne (Get-FileHash -LiteralPath (Join-Path $plan.fixture 'BatteryManager.App.exe')).Hash){throw 'The reviewed handoff plan or its files changed.'}
if(-not $Execute){$plan;return}
if(Test-Path -LiteralPath $plan.target){throw 'The handoff target already exists; use a fresh reviewed plan.'}
$null=New-Item -ItemType Directory -Path $plan.target
$taskTarget=Resolve-PortableProjectPath $plan.target $taskRoot -ArtifactsOnly
Copy-Item -Path (Join-Path $plan.fixture '*') -Destination $taskTarget
Write-PortableText (Join-Path $taskTarget 'version.txt') 'original'
$taskOriginalHash=(Get-FileHash -LiteralPath (Join-Path $taskTarget 'BatteryManager.App.exe')).Hash
$taskArgs=@('/SP-','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/NOCLOSEAPPLICATIONS','/NOFORCECLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',('/DIR="'+$taskTarget+'"'))
$taskFixture=$null
$taskFailure=$null

function Start-HandoffFixture {
    foreach($file in @('fixture.ready','fixture.stop')){
        $path=Resolve-PortableProjectPath (Join-Path $taskTarget $file) $taskRoot -ArtifactsOnly
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
    }
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $taskTarget 'BatteryManager.App.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.Environment['DOTNET_ROOT']=(Join-Path $taskRoot '.tools/dotnet')
    $start.ArgumentList.Add($plan.appMutex)
    $process=[Diagnostics.Process]::Start($start)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.ready'))){
        if($process.HasExited -or $timer.Elapsed.TotalSeconds -gt 15){throw 'The isolated fixture did not become ready.'}
        Start-Sleep -Milliseconds 25
    }
    if((Get-Content -LiteralPath (Join-Path $taskTarget 'fixture.ready') -Raw) -cne [string]$process.Id){throw 'Fixture readiness PID does not match.'}
    $process
}
function Stop-HandoffFixture($Process) {
    if($null -eq $Process){return}
    Write-PortableText (Join-Path $taskTarget 'fixture.stop') 'exit normally'
    if(-not $Process.WaitForExit(15000)){throw "Fixture PID $($Process.Id) did not exit; it was not terminated."}
}
function Start-HandoffInstaller([string]$PidText,[string]$Case) {
    $arguments=$taskArgs+@(('/WAITPID='+$PidText),('/LOG="'+(Join-Path $taskEvidence ($Case+'.log'))+'"'))
    Start-Process -FilePath $plan.installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
}
function Complete-HandoffInstaller($Process,[string]$Case,[bool]$SuccessExpected) {
    if(-not $Process.WaitForExit(15000)){throw "Installer PID $($Process.Id) did not finish; it was not terminated."}
    $Process.Refresh()
    $code=$Process.ExitCode
    $taskSteps.Add([pscustomobject]@{case=$Case;exitCode=$code;expectedSuccess=$SuccessExpected})
    if(($code -eq 0) -ne $SuccessExpected){throw "Unexpected installer result in $Case ($code)."}
}
try {
    Complete-HandoffInstaller (Start-HandoffInstaller '0' 'invalid-pid') 'invalid-pid' $false
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$PID) 'wrong-process-image') 'wrong-process-image' $false
    $taskFixture=Start-HandoffFixture
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$taskFixture.Id) 'timeout-keeps-process') 'timeout-keeps-process' $false
    if($taskFixture.HasExited -or (Get-FileHash -LiteralPath (Join-Path $taskTarget 'BatteryManager.App.exe')).Hash -cne $taskOriginalHash){throw 'Timeout terminated the process or changed its executable.'}
    Stop-HandoffFixture $taskFixture
    $taskFixture.Dispose();$taskFixture=$null
    $taskFixture=Start-HandoffFixture
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'wait-then-upgrade'
    Start-Sleep -Milliseconds 700
    if($installer.HasExited -or $taskFixture.HasExited -or (Get-Content -LiteralPath (Join-Path $taskTarget 'version.txt') -Raw) -cne 'original'){throw 'Installer did not wait before touching files.'}
    Stop-HandoffFixture $taskFixture
    $taskFixture.Dispose();$taskFixture=$null
    Complete-HandoffInstaller $installer 'wait-then-upgrade' $true
    if((Get-Content -LiteralPath (Join-Path $taskTarget 'version.txt') -Raw) -cne 'upgraded'){throw 'Upgrade did not apply after the exact process exited.'}
    Copy-Item -LiteralPath (Join-Path $plan.fixture 'BatteryManager.App.exe') -Destination (Join-Path $taskTarget 'BatteryManager.App.exe')
    $taskFixture=Start-HandoffFixture
    Stop-HandoffFixture $taskFixture
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$taskFixture.Id) 'already-exited') 'already-exited' $true
    $taskFixture.Dispose();$taskFixture=$null
    Write-PortableText (Join-Path $taskTarget 'user-data.txt') 'preserved'
    $uninstaller=(Get-ChildItem -LiteralPath $taskTarget -Filter 'unins*.exe').FullName
    $uninstall=Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $taskEvidence 'uninstall.log')+'"')) -WindowStyle Hidden -PassThru
    Complete-HandoffInstaller $uninstall 'isolated-cleanup' $true
    if(Test-Path -LiteralPath (Join-Path $taskTarget 'BatteryManager.App.exe')){throw 'Isolated installer files remain after cleanup.'}
}catch{$taskFailure=$_.Exception.Message}
finally{if($null -ne $taskFixture){Stop-HandoffFixture $taskFixture;$taskFixture.Dispose()}}
$result=[ordered]@{passed=$null -eq $taskFailure;error=$taskFailure;directory=$taskEvidence;steps=$taskSteps.ToArray();fixtureOnly=$true;productionTimeoutMilliseconds=30000;testTimeoutMilliseconds=2500;applicationHardwareAccess=$false}
Write-PortableText (Join-Path $taskEvidence 'result.json') ($result|ConvertTo-Json -Depth 6)
if($taskFailure){throw $taskFailure}
[pscustomobject]$result
