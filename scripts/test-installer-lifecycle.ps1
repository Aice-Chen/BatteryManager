# Builds isolated installers from inert fixture files. Installation requires explicit -Execute.
# Uses only inert payloads, a private registry subtree and a shortcut below the compiled test root.
# No production AppId, startup keys, Start menu entry or uninstall registry entry is compiled in.
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
    $process.Dispose()
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
    foreach ($revision in 1,2,3) {
        $payload = Join-Path $taskEvidence "payload-v$revision"
        $output = Join-Path $taskEvidence "compiled-v$revision"
        $null = New-Item -ItemType Directory -Path $payload,$output
        Write-PortableText (Join-Path $payload 'BatteryManager.App.exe') "Inert installer fixture, never executable. Revision $revision."
        Write-PortableText (Join-Path $payload 'version.txt') "$revision"
        $null = New-Item -ItemType Directory -Path (Join-Path $payload "revision-$revision/nested")
        Write-PortableText (Join-Path $payload "revision-$revision/nested/only.dll") "Revision $revision owned dependency."
        if ($revision -eq 1) {
            $null = New-Item -ItemType Directory -Path (Join-Path $payload 'obsolete/nested')
            Write-PortableText (Join-Path $payload 'obsolete/nested/old.dll') 'Old app-owned dependency.'
        }
        $manifest = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/')
        })
        Write-PortableText (Join-Path $payload 'SHA256SUMS.txt') (($manifest -join "`n")+"`n")
        $version = "0.0.$revision"
        $arguments = @('/Q', '/DStartupTaskTest=1', '/DCleanupPauseTest=1', ('/DIsolationTestId=' + $taskId), ('/DIsolationRoot=' + $taskEvidence), ('/DPayloadDirectory=' + $payload), ('/DOutputDirectory=' + $output), ('/DAppVersion=' + $version), ('/DNumericVersion=' + $version + '.0'), $taskScript)
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
foreach ($relative in @('settings.v1.bak','logs/session.log','updates/old/installer.exe','updates/partial/download.partial','management.json')) {
    $path = Join-Path $taskUserData $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force
    Write-PortableText $path 'App-owned fixture data.'
}
$taskSettingsHash = (Get-FileHash -LiteralPath (Join-Path $taskUserData 'settings.json')).Hash
$taskBaseArgs = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NOFORCECLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS')
$taskFailure = $null
$behaviorFailures = [Collections.Generic.List[string]]::new()
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
        if (-not (Test-Path -LiteralPath (Join-Path $taskEvidence 'test-start-menu/Battery Manager.lnk'))) { throw 'Isolated install did not create its private shortcut.' }
        for ($previous = 1; $previous -lt $installer.revision; $previous++) {
            if (Test-Path -LiteralPath (Join-Path $plan.target "revision-$previous")) { throw 'An upgrade retained a previous revision dependency.' }
        }
        $testKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$plan.appId+'_is1')
        if ($null -ne $testKey) { $testKey.Dispose(); throw 'The isolated installer unexpectedly registered an uninstall entry.' }
        if ($installer.revision -eq 1) {
            Write-PortableText (Join-Path $plan.target 'user-created.txt') 'Preserve untracked user data.'
            $manifestPath = Join-Path $plan.target 'SHA256SUMS.txt'
            $validManifest = [IO.File]::ReadAllText($manifestPath)
            $outside = Join-Path $taskEvidence 'outside'
            $null = New-Item -ItemType Directory -Path $outside
            Write-PortableText (Join-Path $outside 'protected.dll') 'Outside the installation.'
            $outsideHash = (Get-FileHash -LiteralPath (Join-Path $outside 'protected.dll')).Hash
            foreach ($bad in @('malformed', ('0'*64+'  ../outside/protected.dll'), ('0'*64+'  C:/outside.dll'), ('0'*64+'  obsolete/../user-created.txt'), ('0'*64+'  version.txt:stream'), ('0'*64+'  link/protected.dll'))) {
                $linked = $bad.Contains('link/')
                if ($linked) { $null = New-Item -ItemType Junction -Path (Join-Path $plan.target 'link') -Target $outside }
                try {
                    Write-PortableText $manifestPath ($validManifest+$bad+"`n")
                    $case = 'reject-manifest-'+$taskResults.Count
                    $code = Invoke-IsolatedInstaller $plan.installers[1].path ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence ($case+'.log'))+'"'))) $case
                    if ($code -eq 0 -or (Get-Content -LiteralPath (Join-Path $plan.target 'version.txt') -Raw) -cne '1') { $behaviorFailures.Add('Unsafe manifest was accepted: '+$bad) }
                    if ((Get-FileHash -LiteralPath (Join-Path $outside 'protected.dll')).Hash -cne $outsideHash) { throw 'A manifest changed an outside file.' }
                } finally {
                    if ($linked) { [IO.Directory]::Delete((Join-Path $plan.target 'link')) }
                    Write-PortableText $manifestPath $validManifest
                }
            }
            $originalExeHash = (Get-FileHash -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')).Hash
            $originalManifestHash = (Get-FileHash -LiteralPath $manifestPath).Hash
            $lockedVersion = [IO.File]::Open((Join-Path $plan.target 'version.txt'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                $code = Invoke-IsolatedInstaller $plan.installers[1].path ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence 'locked-incoming.log')+'"'))) 'locked-incoming-keeps-original-payload'
                if ($code -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $plan.target 'obsolete/nested/old.dll')) -or
                    (Get-FileHash -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')).Hash -cne $originalExeHash -or
                    (Get-FileHash -LiteralPath $manifestPath).Hash -cne $originalManifestHash -or
                    (Get-FileHash -LiteralPath (Join-Path $taskUserData 'settings.json')).Hash -cne $taskSettingsHash -or
                    (Get-Content -LiteralPath (Join-Path $plan.target 'version.txt') -Raw) -cne '1') { throw 'Failed extraction damaged the previous payload or reported success.' }
            } finally { $lockedVersion.Dispose() }
            $locked = [IO.File]::Open((Join-Path $plan.target 'obsolete/nested/old.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                $code = Invoke-IsolatedInstaller $plan.installers[1].path ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence 'locked-obsolete.log')+'"'))) 'locked-obsolete-refused'
                if ($code -eq 0 -or (Get-FileHash -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')).Hash -cne $originalExeHash) { $behaviorFailures.Add('Locked obsolete dependency did not refuse before extraction.') }
            } finally { $locked.Dispose() }
            $ready = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,('Local\BatteryManager.CleanupTest.'+$plan.id+'.Ready'))
            $continue = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,('Local\BatteryManager.CleanupTest.'+$plan.id+'.Continue'))
            $locked = $null
            try {
                $arguments = $taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence 'postwrite-cleanup-failure.log')+'"'))
                $process = Start-Process -FilePath $plan.installers[1].path -ArgumentList $arguments -WindowStyle Hidden -PassThru
                if (-not $ready.WaitOne(15000)) { throw 'The isolated installer did not reach postwrite cleanup.' }
                $locked = [IO.File]::Open((Join-Path $plan.target 'obsolete/nested/old.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                $null = $continue.Set()
                if (-not $process.WaitForExit(15000)) { throw 'The isolated cleanup installer did not finish.' }
                $process.Refresh()
                $taskResults.Add([pscustomobject]@{case='postwrite-cleanup-failure';exitCode=$process.ExitCode})
                if ($process.ExitCode -ne 20 -or -not (Test-Path -LiteralPath (Join-Path $plan.target '.battery-manager-cleanup.sha256')) -or
                    (Get-Content -LiteralPath (Join-Path $plan.target 'version.txt') -Raw) -cne '2') { throw 'Postwrite cleanup failure lost its retry journal or reported success.' }
            } finally { $null=$continue.Set(); if($locked){$locked.Dispose()};$ready.Dispose();$continue.Dispose() }
            # Model interruption after a complete journal write, before its atomic rename.
            Copy-Item -LiteralPath (Join-Path $plan.target '.battery-manager-cleanup.sha256') -Destination (Join-Path $plan.target '.battery-manager-cleanup.sha256.tmp')
        }
        if ($installer.revision -eq 2) {
            Write-PortableText (Join-Path $plan.target '.battery-manager-cleanup.sha256.tmp') 'Interrupted partial journal bytes.'
            $partialJournalHash = (Get-FileHash -LiteralPath (Join-Path $plan.target '.battery-manager-cleanup.sha256.tmp')).Hash
        }
    }
    $recoveredJournals = @(Get-ChildItem -LiteralPath (Join-Path $taskUserData 'installer-recovery') -File -ErrorAction SilentlyContinue)
    if ($recoveredJournals.Count -ne 1 -or (Get-FileHash -LiteralPath $recoveredJournals[0].FullName).Hash -cne $partialJournalHash) { throw 'Partial journal recovery did not preserve the original bytes.' }
    if (Test-Path -LiteralPath (Join-Path $plan.target 'obsolete')) { $behaviorFailures.Add('Upgrade retained obsolete manifest-owned directory.') }
    if (Test-Path -LiteralPath (Join-Path $plan.target '.battery-manager-cleanup.sha256')) { $behaviorFailures.Add('Successful retry retained cleanup journal.') }
    if (Test-Path -LiteralPath (Join-Path $plan.target '.battery-manager-cleanup.sha256.tmp')) { $behaviorFailures.Add('Successful retry retained temporary cleanup journal.') }
    if ((Get-FileHash -LiteralPath (Join-Path $taskUserData 'settings.json')).Hash -cne $taskSettingsHash) { throw 'Upgrade changed app settings.' }
    $uninstaller = (Get-ChildItem -LiteralPath $plan.target -Filter 'unins*.exe').FullName
    $mutex = [Threading.Mutex]::new($false, $plan.appMutex)
    try {
        $code = Invoke-IsolatedInstaller $uninstaller ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall-running.log')+'"')) 'running-app-blocks-uninstall'
        if ($code -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $taskUserData 'settings.json'))) { throw 'Running app did not block uninstall.' }
    } finally { $mutex.Dispose() }
    $null = New-Item -ItemType Junction -Path (Join-Path $taskUserData 'unsafe') -Target $outside
    try {
        $code = Invoke-IsolatedInstaller $uninstaller ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall-reparse.log')+'"')) 'reparse-data-blocks-uninstall'
        if ($code -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe'))) { $behaviorFailures.Add('Uninstall accepted a reparse data directory.') }
        if ((Get-FileHash -LiteralPath (Join-Path $outside 'protected.dll')).Hash -cne $outsideHash) { throw 'Uninstall changed an outside file.' }
    } finally { [IO.Directory]::Delete((Join-Path $taskUserData 'unsafe')) }
    $registryRoot = 'Software\BatteryManager\InstallerTaskTests\'+$plan.id
    $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryRoot+'\Run')
    try { $run.SetValue('BatteryManager','"'+(Join-Path $plan.target 'BatteryManager.App.exe')+'"') } finally { $run.Dispose() }
    $approval = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryRoot+'\StartupApproved')
    try { $approval.SetValue('BatteryManager',[byte[]]::new(12),[Microsoft.Win32.RegistryValueKind]::Binary) } finally { $approval.Dispose() }
    Write-PortableText (Join-Path $plan.target '.battery-manager-cleanup.sha256.tmp') 'Truncated journal before direct uninstall.'
    $code = Invoke-IsolatedInstaller $uninstaller ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall.log')+'"')) 'uninstall-removes-app-data'
    if ($code -ne 0 -or (Test-Path -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')) -or
        (Get-Content -LiteralPath (Join-Path $plan.target 'user-created.txt') -Raw) -cne 'Preserve untracked user data.') { throw 'Uninstall did not remove application files and preserve unrelated files as expected.' }
    if (Test-Path -LiteralPath $taskUserData) { $behaviorFailures.Add('Uninstall retained app-owned settings/data.') }
    if (Test-Path -LiteralPath (Join-Path $taskEvidence 'test-start-menu/Battery Manager.lnk')) { $behaviorFailures.Add('Uninstall retained its shortcut.') }
    $settle = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = @(Get-ChildItem -LiteralPath $plan.target -Recurse -File)
        if ($remaining.Count -eq 1 -and $remaining[0].Name -ceq 'user-created.txt') { break }
        Start-Sleep -Milliseconds 50
    } while ($settle.Elapsed.TotalSeconds -lt 10)
    if ($remaining.Count -ne 1 -or $remaining[0].Name -cne 'user-created.txt') { $behaviorFailures.Add('One uninstall after multiple upgrades retained app-owned payload.') }
    foreach ($subkey in @('Run','StartupApproved')) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryRoot+'\'+$subkey)
        try { if ($null -ne $key -and 'BatteryManager' -in $key.GetValueNames()) { $behaviorFailures.Add('Uninstall retained owned '+$subkey) } } finally { if ($key) { $key.Dispose() } }
    }
    # A stale uninstaller must not erase the shared data or startup of a later installation.
    $code = Invoke-IsolatedInstaller $plan.installers[1].path ($taskBaseArgs + @(('/DIR="'+$plan.target+'"'), ('/LOG="'+(Join-Path $taskEvidence 'install-for-other-copy.log')+'"'))) 'install-for-other-copy'
    if ($code -ne 0) { throw 'Could not prepare another-installation protection test.' }
    $null = New-Item -ItemType Directory -Path $taskUserData -Force
    Write-PortableText (Join-Path $taskUserData 'settings.json') 'Other installed copy settings.'
    $otherTarget = Join-Path $taskEvidence 'other-install'
    $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryRoot+'\Run')
    try { $run.SetValue('BatteryManager','"'+(Join-Path $otherTarget 'BatteryManager.App.exe')+'"') } finally { $run.Dispose() }
    $approval = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryRoot+'\StartupApproved')
    try { $approval.SetValue('BatteryManager',[byte[]]::new(12),[Microsoft.Win32.RegistryValueKind]::Binary) } finally { $approval.Dispose() }
    $registration = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryRoot+'\Uninstall')
    try { $registration.SetValue('InstallLocation',$otherTarget) } finally { $registration.Dispose() }
    $uninstaller = (Get-ChildItem -LiteralPath $plan.target -Filter 'unins*.exe').FullName
    $code = Invoke-IsolatedInstaller $uninstaller ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall-other-copy.log')+'"')) 'uninstall-preserves-other-copy'
    if ($code -eq 0 -or -not (Test-Path -LiteralPath (Join-Path $plan.target 'BatteryManager.App.exe')) -or (Get-Content -LiteralPath (Join-Path $taskUserData 'settings.json') -Raw) -cne 'Other installed copy settings.') { throw 'Stale uninstall did not refuse another registered installation.' }
    foreach ($subkey in @('Run','StartupApproved')) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryRoot+'\'+$subkey)
        try { if ($null -eq $key -or 'BatteryManager' -notin $key.GetValueNames()) { throw ('Uninstall removed another copy '+$subkey) } } finally { if ($key) { $key.Dispose() } }
    }
    # Missing Run is the normal app-disabled state; this exact registered install still owns its approval residue.
    $registration = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryRoot+'\Uninstall')
    try { $registration.SetValue('InstallLocation',$plan.target) } finally { $registration.Dispose() }
    $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryRoot+'\Run',$true)
    try { $run.DeleteValue('BatteryManager') } finally { $run.Dispose() }
    $code = Invoke-IsolatedInstaller $uninstaller ($taskBaseArgs + @('/LOG="'+(Join-Path $taskEvidence 'uninstall-disabled-startup.log')+'"')) 'uninstall-clears-disabled-approval'
    $approval = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryRoot+'\StartupApproved')
    try { if ($code -ne 0 -or ($null -ne $approval -and 'BatteryManager' -in $approval.GetValueNames()) -or (Test-Path -LiteralPath $taskUserData)) { throw 'Disabled startup approval or userdata remained.' } } finally { if ($approval) { $approval.Dispose() } }
    if ($behaviorFailures.Count) { throw ($behaviorFailures -join ' ') }
} catch { $taskFailure = $_.Exception.Message }
$taskAfter = Read-ProtectedInstallerState
if ($taskBefore -cne $taskAfter) { $taskFailure = 'The protected startup or production uninstall state changed during testing.' }
$result = [ordered]@{passed=$null -eq $taskFailure;error=$taskFailure;behaviorFailures=$behaviorFailures.ToArray();directory=$taskEvidence;steps=$taskResults.ToArray();protectedStateUnchanged=$taskBefore -ceq $taskAfter;applicationStarted=$false;fixturePayload=$true;productionInstallTested=$false}
Write-PortableText (Join-Path $taskEvidence 'result.json') ($result | ConvertTo-Json -Depth 8)
if ($taskFailure) { throw $taskFailure }
[pscustomobject]$result
