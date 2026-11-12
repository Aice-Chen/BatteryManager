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
    & $taskDotnet publish (Join-Path $taskRoot 'tools/BatteryManager.InstallerFixture/BatteryManager.InstallerFixture.csproj') -c Release -r win-x64 --self-contained false -o $fixture --nologo ('-p:BaseIntermediateOutputPath='+(Join-Path $directory 'fixture-obj/')) ('-p:MSBuildProjectExtensionsPath='+(Join-Path $directory 'fixture-obj/')) ('-p:BaseOutputPath='+(Join-Path $directory 'fixture-bin/')) 2>&1 |
        Tee-Object -FilePath (Join-Path $directory 'fixture-build.log') | Out-Host
    if($LASTEXITCODE -ne 0){throw 'The process fixture did not build.'}
    $payload=Join-Path $directory 'payload'
    $output=Join-Path $directory 'compiled'
    $null=New-Item -ItemType Directory -Path $payload,$output
    Copy-Item -Path (Join-Path $fixture '*') -Destination $payload
    Write-PortableText (Join-Path $payload 'version.txt') 'upgraded'
    $manifest = @(Get-ChildItem -LiteralPath $payload -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name })
    Write-PortableText (Join-Path $payload 'SHA256SUMS.txt') (($manifest -join "`n")+"`n")
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
$manifest = @(Get-ChildItem -LiteralPath $taskTarget -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name })
Write-PortableText (Join-Path $taskTarget 'SHA256SUMS.txt') (($manifest -join "`n")+"`n")
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
function Start-HandoffInstaller([string]$PidText,[string]$Case,[string[]]$Extra=@()) {
    $arguments=$taskArgs+@(('/WAITPID='+$PidText),('/LOG="'+(Join-Path $taskEvidence ($Case+'.log'))+'"'))+$Extra
    Start-Process -FilePath $plan.installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
}
function Complete-HandoffInstaller($Process,[string]$Case,[bool]$SuccessExpected) {
    if(-not $Process.WaitForExit(15000)){throw "Installer PID $($Process.Id) did not finish; it was not terminated."}
    $Process.Refresh()
    $code=$Process.ExitCode
    $Process.Dispose()
    $taskSteps.Add([pscustomobject]@{case=$Case;exitCode=$code;expectedSuccess=$SuccessExpected})
    if(($code -eq 0) -ne $SuccessExpected){throw "Unexpected installer result in $Case ($code)."}
}
function New-ReadyHandshake {
    $token=[Guid]::NewGuid().ToString('N')
    $events=@{token=$token}
    foreach($name in @('Accepted','Ready','Cancel')) {
        $created=$false
        $events[$name]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,('Local\BatteryManager.AutoUpdate.'+$token+'.'+$name),[ref]$created)
        if(-not $created){throw 'Handshake event name was already occupied.'}
    }
    $events
}
function Wait-HandshakeAccepted($Handshake,$Installer) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not $Handshake.Accepted.WaitOne(25)) {
        if($Installer.HasExited -or $timer.Elapsed.TotalSeconds -gt 10){throw 'Installer did not accept the live process handshake.'}
    }
}
function Close-ReadyHandshake($Handshake) {
    if($null -eq $Handshake){return}
    $null=$Handshake.Cancel.Set()
    foreach($name in @('Accepted','Ready','Cancel')){$Handshake[$name].Dispose()}
}
$handshake=$null
try {
    $oldDotnetRoot = $env:DOTNET_ROOT
    $env:DOTNET_ROOT = Join-Path $taskRoot '.tools/dotnet'
    Complete-HandoffInstaller (Start-HandoffInstaller '0' 'invalid-pid') 'invalid-pid' $false
    Complete-HandoffInstaller (Start-HandoffInstaller '999999999' 'invalid-autoupdate' @('/AUTOUPDATE=2')) 'invalid-autoupdate' $false
    $invalid = Start-Process -FilePath $plan.installer -ArgumentList ($taskArgs+@('/AUTOUPDATE=1',('/LOG="'+(Join-Path $taskEvidence 'autoupdate-without-handoff.log')+'"'))) -WindowStyle Hidden -PassThru
    Complete-HandoffInstaller $invalid 'autoupdate-without-handoff' $false
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$PID) 'wrong-process-image') 'wrong-process-image' $false
    $taskFixture=Start-HandoffFixture
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$taskFixture.Id) 'missing-handshake' @('/AUTOUPDATE=1')) 'missing-handshake' $false
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$taskFixture.Id) 'missing-handshake-events' @('/AUTOUPDATE=1',('/WAITTOKEN='+[Guid]::NewGuid().ToString('N')))) 'missing-handshake-events' $false
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$taskFixture.Id) 'invalid-handshake-token' @('/AUTOUPDATE=1','/WAITTOKEN=not-a-token')) 'invalid-handshake-token' $false
    $handshake=New-ReadyHandshake
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'cancel-before-ready' @('/AUTOUPDATE=1',('/WAITTOKEN='+$handshake.token))
    Wait-HandshakeAccepted $handshake $installer
    $null=$handshake.Cancel.Set()
    Complete-HandoffInstaller $installer 'cancel-before-ready' $false
    Close-ReadyHandshake $handshake; $handshake=$null
    $handshake=New-ReadyHandshake
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'exit-before-ready-rejected' @('/AUTOUPDATE=1',('/WAITTOKEN='+$handshake.token))
    Wait-HandshakeAccepted $handshake $installer
    Stop-HandoffFixture $taskFixture
    $taskFixture.Dispose();$taskFixture=$null
    Complete-HandoffInstaller $installer 'exit-before-ready-rejected' $false
    Close-ReadyHandshake $handshake; $handshake=$null
    if (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.launches')) { throw 'Incomplete handshake incorrectly restored or restarted the fixture.' }
    $taskFixture=Start-HandoffFixture
    $handshake=New-ReadyHandshake
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'timeout-keeps-process' @('/AUTOUPDATE=1',('/WAITTOKEN='+$handshake.token))
    Wait-HandshakeAccepted $handshake $installer
    $null=$handshake.Ready.Set()
    Complete-HandoffInstaller $installer 'timeout-keeps-process' $false
    Close-ReadyHandshake $handshake; $handshake=$null
    if (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.launches')) { throw 'Failed installer restarted the fixture.' }
    if($taskFixture.HasExited -or (Get-FileHash -LiteralPath (Join-Path $taskTarget 'BatteryManager.App.exe')).Hash -cne $taskOriginalHash){throw 'Timeout terminated the process or changed its executable.'}
    Stop-HandoffFixture $taskFixture
    $taskFixture.Dispose();$taskFixture=$null
    # The unchanged 0.3.0 original rejects --background; recovery must use its supported arguments.
    Write-PortableText (Join-Path $taskTarget 'fixture.legacy-arguments') '0.3.0 rejects --background'
    # A foreign lock introduced after acceptance must restart the unchanged original after refusal.
    $taskFixture=Start-HandoffFixture
    $handshake=New-ReadyHandshake
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'late-lock-restores-original' @('/AUTOUPDATE=1',('/WAITTOKEN='+$handshake.token))
    Wait-HandshakeAccepted $handshake $installer
    $lock=[IO.File]::Open((Join-Path $taskTarget 'version.txt'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $null=$handshake.Ready.Set()
        Stop-HandoffFixture $taskFixture
        $taskFixture.Dispose();$taskFixture=$null
        Complete-HandoffInstaller $installer 'late-lock-restores-original' $false
        $timer=[Diagnostics.Stopwatch]::StartNew()
        while(-not (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.launches')) -and
            -not (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.rejected-background')) -and $timer.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 25}
        if(Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.rejected-background')){throw 'Preflight restarted the older original with unsupported --background.'}
        if((Get-Content -LiteralPath (Join-Path $taskTarget 'fixture.launches') -Raw) -cne ("original"+[Environment]::NewLine) -or
            (Get-FileHash -LiteralPath (Join-Path $taskTarget 'BatteryManager.App.exe')).Hash -cne $taskOriginalHash){throw 'Late refusal did not restore exactly one unchanged original.'}
    } finally {$lock.Dispose();Close-ReadyHandshake $handshake;$handshake=$null}
    Remove-Item -LiteralPath (Join-Path $taskTarget 'fixture.legacy-arguments')
    Remove-Item -LiteralPath (Join-Path $taskTarget 'fixture.launches')
    $taskFixture=Start-HandoffFixture
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'wait-then-upgrade'
    Start-Sleep -Milliseconds 700
    if($installer.HasExited -or $taskFixture.HasExited -or (Get-Content -LiteralPath (Join-Path $taskTarget 'version.txt') -Raw) -cne 'original'){throw 'Installer did not wait before touching files.'}
    Stop-HandoffFixture $taskFixture
    $taskFixture.Dispose();$taskFixture=$null
    Complete-HandoffInstaller $installer 'wait-then-upgrade' $true
    if (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.launches')) { throw 'Ordinary silent install unexpectedly restarted the fixture.' }
    if((Get-Content -LiteralPath (Join-Path $taskTarget 'version.txt') -Raw) -cne 'upgraded'){throw 'Upgrade did not apply after the exact process exited.'}
    Copy-Item -LiteralPath (Join-Path $plan.fixture 'BatteryManager.App.exe') -Destination (Join-Path $taskTarget 'BatteryManager.App.exe')
    $taskFixture=Start-HandoffFixture
    Stop-HandoffFixture $taskFixture
    Complete-HandoffInstaller (Start-HandoffInstaller ([string]$taskFixture.Id) 'already-exited') 'already-exited' $true
    $taskFixture.Dispose();$taskFixture=$null
    $taskFixture=Start-HandoffFixture
    $handshake=New-ReadyHandshake
    $installer=Start-HandoffInstaller ([string]$taskFixture.Id) 'autoupdate-restarts-after-exit' @('/AUTOUPDATE=1',('/WAITTOKEN='+$handshake.token))
    Wait-HandshakeAccepted $handshake $installer
    Start-Sleep -Milliseconds 3100 # Preparation is allowed to exceed the isolated exit timeout.
    if($installer.HasExited){throw 'Installer counted preparation time against the process-exit timeout.'}
    if (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.launches')) { throw 'Auto-update restarted before the previous process exited.' }
    $null=$handshake.Ready.Set()
    Stop-HandoffFixture $taskFixture
    $taskFixture.Dispose();$taskFixture=$null
    Complete-HandoffInstaller $installer 'autoupdate-restarts-after-exit' $true
    Close-ReadyHandshake $handshake; $handshake=$null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $taskTarget 'fixture.launches')) -and $timer.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 25 }
    if ((Get-Content -LiteralPath (Join-Path $taskTarget 'fixture.launches') -Raw) -cne ("upgraded --background"+[Environment]::NewLine)) { throw 'Auto-update did not start exactly one updated fixture in the background.' }
    Write-PortableText (Join-Path $taskTarget 'user-data.txt') 'preserved'
    $uninstaller=(Get-ChildItem -LiteralPath $taskTarget -Filter 'unins*.exe').FullName
    $uninstall=Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $taskEvidence 'uninstall.log')+'"')) -WindowStyle Hidden -PassThru
    Complete-HandoffInstaller $uninstall 'isolated-cleanup' $true
    if(Test-Path -LiteralPath (Join-Path $taskTarget 'BatteryManager.App.exe')){throw 'Isolated installer files remain after cleanup.'}
}catch{$taskFailure=$_.Exception.Message}
finally{Close-ReadyHandshake $handshake; if($null -ne $taskFixture){Stop-HandoffFixture $taskFixture;$taskFixture.Dispose()}; $env:DOTNET_ROOT=$oldDotnetRoot}
$result=[ordered]@{passed=$null -eq $taskFailure;error=$taskFailure;directory=$taskEvidence;steps=$taskSteps.ToArray();fixtureOnly=$true;productionTimeoutMilliseconds=30000;testTimeoutMilliseconds=2500;applicationHardwareAccess=$false}
Write-PortableText (Join-Path $taskEvidence 'result.json') ($result|ConvertTo-Json -Depth 6)
if($taskFailure){throw $taskFailure}
[pscustomobject]$result
