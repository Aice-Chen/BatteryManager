# Offline publication-plan and fake-gh tests. Fixture installers are inert bytes and are never run or uploaded.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
. (Join-Path $PSScriptRoot 'test-source-binding-fixtures.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskEvidence=Resolve-PortableProjectPath ('.artifacts/tests/github-release-tooling/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')) $taskRoot -ArtifactsOnly
$null=New-Item -ItemType Directory -Path $taskEvidence
$taskRepository=New-SourceBindingFixture (Join-Path $taskEvidence 'repository') $PSScriptRoot
$taskSource=Get-PortableCommittedSourceSnapshot $taskRepository
$taskScript=Join-Path $taskRepository 'scripts/publish-github-release.ps1'
$taskCommit=$taskSource.commit
$taskResults=[Collections.Generic.List[object]]::new()
function Test-PublishCase([string]$Name,[bool]$Accept,[scriptblock]$Action){
    $errorText=$null
    try{$null=& $Action}catch{$errorText=$_.Exception.Message}
    $taskResults.Add([pscustomobject]@{name=$Name;passed=($null -eq $errorText) -eq $Accept;error=$errorText})
}
function Write-PublishJson([string]$Path,[object]$Value){Write-PortableText $Path ($Value|ConvertTo-Json -Depth 10)}
function New-PublishFixture([string]$Version='0.2.0',[string]$SourceCase='valid'){
    $directory=Join-Path $taskRepository ('.artifacts/delivery with spaces '+[Guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Force -Path $directory
    $files=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($name in @('使用说明.txt','BatteryManager.App.exe','BatteryManager.App.dll','BatteryManager.App.pri','BatteryManager.App.deps.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.WindowsAppRuntime.dll','Microsoft.ui.xaml.dll')){$files.Add($name,'Inert fixture; never execute: '+$name)}
    $files.Add('BatteryManager.App.runtimeconfig.json','{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.14"}]}}')
    $source=$taskSource.manifest
    $gitSource=$taskSource.gitManifest
    if($SourceCase -eq 'raw-tampered'){$source=$source -replace '^[0-9a-f]{64}',('f'*64)}
    if($SourceCase -eq 'git-tampered'){$gitSource=$gitSource -replace '^[0-9a-f]{64}',('f'*64)}
    if($SourceCase -eq 'omitted-source'){
        $source=($source.Split("`n")|Where-Object{$_ -notlike '*  src/example.cs'}) -join "`n"
        $gitSource=($gitSource.Split("`n")|Where-Object{$_ -notlike '*  src/example.cs'}) -join "`n"
    }
    $fingerprint=Get-PortableTextHash $source
    $files.Add('source-files.sha256',$source)
    $info=@{schemaVersion=1;product='Battery Manager';version=$Version;runtimeIdentifier='win-x64';selfContained=$true;sourceFingerprint=$fingerprint;sourceFileCount=$(if($SourceCase -eq 'omitted-source'){$taskSource.fileCount-1}else{$taskSource.fileCount});informationalVersion=$Version+'+source.'+$fingerprint}
    if($SourceCase -ne 'legacy'){
        $files.Add('source-git-files.sha256',$gitSource)
        $info.sourceCommit=$taskCommit;$info.sourceGitManifest='source-git-files.sha256';$info.sourceGitFingerprint=Get-PortableTextHash $gitSource
        $info.sourceAutoCrlf=$taskSource.autoCrlf;$info.sourceBindingRule='git-blob-with-utf8-eol-v1'
    }
    if($SourceCase -eq 'wrong-commit'){$info.sourceCommit='b'*40}
    $files.Add('build-info.json',($info|ConvertTo-Json))
    $sums=foreach($file in $files.GetEnumerator()){(Get-PortableTextHash $file.Value)+'  '+$file.Key}
    $files.Add('SHA256SUMS.txt',($sums -join "`n")+"`n")
    $zip=Join-Path $directory "BatteryManager-$Version-win-x64.zip"
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($file in $files.GetEnumerator()){
        $entry=$archive.CreateEntry("BatteryManager-$Version-win-x64/"+$file.Key)
        $stream=$entry.Open()
        try{$bytes=[Text.Encoding]::UTF8.GetBytes($file.Value);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    }}finally{$archive.Dispose()}
    $installer=Join-Path $directory "BatteryManager-$Version-win-x64-setup.exe"
    Write-PortableText $installer 'Inert installer fixture; never execute.'
    $assets=foreach($item in @(@{kind='installer';path=$installer},@{kind='portable';path=$zip})){
        $hash=(Get-FileHash -LiteralPath $item.path).Hash.ToLowerInvariant()
        Write-PortableText ($item.path+'.sha256') ($hash+'  '+[IO.Path]::GetFileName($item.path)+"`n")
        [pscustomobject]@{kind=$item.kind;fileName=[IO.Path]::GetFileName($item.path);sha256=$hash;size=[long](Get-Item -LiteralPath $item.path).Length}
    }
    Write-PublishJson (Join-Path $directory 'update-manifest.json') (New-ReleaseManifest $Version @($assets[0]))
    Write-PortableText (Join-Path $directory 'RELEASE_NOTES.md') "Fixture release notes.`n"
    $sourceHashes=Get-PortableSourceHashes $taskSource.manifest
    Write-PublishJson (Join-Path $directory 'installer-build.json') @{schemaVersion=1;version=$Version;testOnly=$false;installerSha256=$assets[0].sha256;portableSha256=$assets[1].sha256;payloadSourceFingerprint=$fingerprint;installerPath='private local build path';portablePackagePath='private local package path';installerScriptSha256=$sourceHashes['packaging/windows/BatteryManager.iss'];installedMarkerSha256=$sourceHashes['packaging/windows/installed.json']}
    Write-PublishJson (Join-Path $directory 'delivery.json') @{schemaVersion=1;version=$Version;tag=('v'+$Version);repository='Aice-Chen/BatteryManager';prerelease=$Version.Contains('-');sourceFingerprint=$fingerprint;directory=$directory;uploaded=$false}
    $directory
}
function Get-PublishPlan([string]$Directory){& $taskScript -DeliveryDirectory $Directory -TargetCommit $taskCommit -PlanOnly -GhCommand 'must-not-resolve-or-run-in-plan-mode'}

foreach($version in @('0.2.0','0.2.0-preview.2')){
    Test-PublishCase ('plan:'+ $version) $true {
        $directory=New-PublishFixture $version
        Write-PortableText (Join-Path $directory 'private-settings.json') 'Never upload this local extra file.'
        $plan=Get-PublishPlan $directory
        if(-not $plan.draft -or $plan.created -or $plan.published -or $plan.networkAccessed -or $plan.assets.Count -ne 4 -or -not $plan.sourceCommitBindingVerified){throw 'Unexpected plan execution, unverified commit or asset count.'}
        $names=@($plan.assets|ForEach-Object name)
        $expected=@("BatteryManager-$version-win-x64-setup.exe","BatteryManager-$version-win-x64-setup.exe.sha256",'update-manifest.json','RELEASE_NOTES.md')
        if(@($names|Where-Object {$_ -cnotin $expected}).Count -or @($plan.createArguments|Where-Object {$_ -like '*.zip' -or $_ -like '*.zip.sha256'}).Count){throw 'Private payload or metadata entered the upload whitelist.'}
        if($plan.prerelease -ne $version.Contains('-') -or ('--prerelease' -in $plan.createArguments) -ne $plan.prerelease){throw 'Preview channel is inconsistent.'}
        if('--draft' -notin $plan.createArguments -or '--clobber' -in $plan.createArguments -or '--notes-file' -notin $plan.createArguments){throw 'Draft/no-clobber/notes-file arguments are wrong.'}
        foreach($asset in $plan.assets){if(($asset.sha256 -cne (Get-FileHash -LiteralPath $asset.path).Hash.ToLowerInvariant()) -or $asset.size -ne (Get-Item -LiteralPath $asset.path).Length){throw 'An asset was not fingerprinted.'}}
    }
}
Test-PublishCase 'prepare-delivery-keeps-private-payload' $true {
    $sourceDirectory=New-PublishFixture
    $prepared=& (Join-Path $PSScriptRoot 'prepare-release.ps1') -PortablePackagePath (Join-Path $sourceDirectory 'BatteryManager-0.2.0-win-x64.zip') -InstallerPath (Join-Path $sourceDirectory 'BatteryManager-0.2.0-win-x64-setup.exe') -ReleaseNotesPath (Join-Path $sourceDirectory 'RELEASE_NOTES.md') -OutputDirectory (Join-Path $taskRepository '.artifacts/prepared')
    $manifest=Get-Content -LiteralPath (Join-Path $prepared.directory 'update-manifest.json') -Raw|ConvertFrom-Json
    if(@($manifest.assets).Count -ne 1 -or $manifest.assets[0].kind -cne 'installer'){throw 'Preparation exposed a non-installer download.'}
    if(-not (Test-Path -LiteralPath (Join-Path $prepared.directory 'BatteryManager-0.2.0-win-x64.zip'))){throw 'Preparation discarded the private source-bound installer payload.'}
    $plan=Get-PublishPlan $prepared.directory
    if($plan.assets.Count -ne 4 -or -not $plan.sourceCommitBindingVerified){throw 'Prepared delivery lost upload limits or source binding.'}
}
foreach($case in @('wrong-repository','wrong-tag','wrong-channel','already-uploaded','wrong-directory','wrong-source','manifest-version','manifest-extra','manifest-size','manifest-hash','manifest-traversal','legacy-dual-manifest','portable-only-manifest','missing-installer','duplicate-json','test-installer','build-hash','installer-source-hash','installed-marker-hash','missing-installer-source-hash','changed-package','changed-sidecar','empty-notes')){
    Test-PublishCase ('reject:'+ $case) $false {
        $directory=New-PublishFixture
        $deliveryPath=Join-Path $directory 'delivery.json';$delivery=Get-Content -LiteralPath $deliveryPath -Raw|ConvertFrom-Json
        $manifestPath=Join-Path $directory 'update-manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
        $buildPath=Join-Path $directory 'installer-build.json';$build=Get-Content -LiteralPath $buildPath -Raw|ConvertFrom-Json
        switch($case){
            'wrong-repository'{$delivery.repository='elsewhere/BatteryManager';Write-PublishJson $deliveryPath $delivery}
            'wrong-tag'{$delivery.tag='v9.0.0';Write-PublishJson $deliveryPath $delivery}
            'wrong-channel'{$delivery.prerelease=$true;Write-PublishJson $deliveryPath $delivery}
            'already-uploaded'{$delivery.uploaded=$true;Write-PublishJson $deliveryPath $delivery}
            'wrong-directory'{$delivery.directory=Join-Path $directory 'elsewhere';Write-PublishJson $deliveryPath $delivery}
            'wrong-source'{$delivery.sourceFingerprint='b'*64;Write-PublishJson $deliveryPath $delivery}
            'manifest-version'{$manifest.version='0.3.0';Write-PublishJson $manifestPath $manifest}
            'manifest-extra'{$manifest|Add-Member privatePath 'do not leak';Write-PublishJson $manifestPath $manifest}
            'manifest-size'{$manifest.assets[0].size++;Write-PublishJson $manifestPath $manifest}
            'manifest-hash'{$manifest.assets[0].sha256='b'*64;Write-PublishJson $manifestPath $manifest}
            'manifest-traversal'{$manifest.assets[0].fileName='../other.exe';Write-PublishJson $manifestPath $manifest}
            'legacy-dual-manifest'{$manifest.assets+=@{kind='portable';fileName='BatteryManager-0.2.0-win-x64.zip';sha256=$build.portableSha256;size=[long](Get-Item -LiteralPath (Join-Path $directory 'BatteryManager-0.2.0-win-x64.zip')).Length};Write-PublishJson $manifestPath $manifest}
            'portable-only-manifest'{$manifest.assets=@(@{kind='portable';fileName='BatteryManager-0.2.0-win-x64.zip';sha256=$build.portableSha256;size=[long](Get-Item -LiteralPath (Join-Path $directory 'BatteryManager-0.2.0-win-x64.zip')).Length});Write-PublishJson $manifestPath $manifest}
            'missing-installer'{$manifest.assets=@();Write-PublishJson $manifestPath $manifest}
            'duplicate-json'{Write-PortableText $manifestPath '{"schemaVersion":1,"schemaVersion":1,"version":"0.2.0","assets":[]}'}
            'test-installer'{$build.testOnly=$true;Write-PublishJson $buildPath $build}
            'build-hash'{$build.installerSha256='b'*64;Write-PublishJson $buildPath $build}
            'installer-source-hash'{$build.installerScriptSha256='b'*64;Write-PublishJson $buildPath $build}
            'installed-marker-hash'{$build.installedMarkerSha256='b'*64;Write-PublishJson $buildPath $build}
            'missing-installer-source-hash'{$build.PSObject.Properties.Remove('installerScriptSha256');Write-PublishJson $buildPath $build}
            'changed-package'{Write-PortableText (Join-Path $directory 'BatteryManager-0.2.0-win-x64-setup.exe') 'changed bytes'}
            'changed-sidecar'{Write-PortableText (Join-Path $directory 'BatteryManager-0.2.0-win-x64.zip.sha256') 'wrong checksum'}
            'empty-notes'{Write-PortableText (Join-Path $directory 'RELEASE_NOTES.md') ''}
        }
        Get-PublishPlan $directory
    }
}
Test-PublishCase 'short-target-commit' $false { & $taskScript -DeliveryDirectory (New-PublishFixture) -TargetCommit 'abcdef' -PlanOnly }
foreach($sourceCase in @('legacy','raw-tampered','git-tampered','omitted-source','wrong-commit')){
    Test-PublishCase ('commit-binding:'+ $sourceCase) $false { Get-PublishPlan (New-PublishFixture '0.2.0' $sourceCase) }
}
Test-PublishCase 'legacy-integrity-remains-valid' $true {
    $directory=New-PublishFixture '0.1.0' 'legacy'
    $result=& (Join-Path $taskRepository 'scripts/test-portable-package.ps1') -PackagePath (Join-Path $directory 'BatteryManager-0.1.0-win-x64.zip')
    if(-not $result.passed -or $result.sourceCommitBindingVerified){throw 'Legacy integrity was confused with commit provenance.'}
}

$taskFakeDirectory=Join-Path $taskEvidence 'fake-gh'
$null=New-Item -ItemType Directory -Path $taskFakeDirectory
$taskFake=Join-Path $taskFakeDirectory 'fake-gh.ps1'
Write-PortableText $taskFake @'
$ErrorActionPreference='Stop'
$command=@($args)
$state=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'state.json') -Raw|ConvertFrom-Json
[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'calls.jsonl'),(ConvertTo-Json -InputObject $command -Compress)+[Environment]::NewLine)
$global:LASTEXITCODE=0
if($state.mode -eq 'cli-failure'){$global:LASTEXITCODE=1;'fake CLI failure';return}
if($command[0] -eq 'repo'){
    @{nameWithOwner=$(if($state.mode -eq 'wrong-remote'){'other/repo'}else{'Aice-Chen/BatteryManager'});url='https://github.com/Aice-Chen/BatteryManager'}|ConvertTo-Json;return
}
if($command[0] -eq 'api'){
    $endpoint=$command[3]
    if($endpoint -like '*/commits/*'){@{sha=$(if($state.mode -eq 'missing-commit'){'b'*40}else{$state.commit})}|ConvertTo-Json;return}
    if($endpoint -like '*/git/matching-refs/*'){
        if($state.mode -eq 'existing-tag'){'[{"ref":"refs/tags/'+$state.tag+'"}]'}else{'[]'};return
    }
    if($endpoint -like '*/releases?*'){
        if($state.mode -eq 'existing-release'){'[[{"tag_name":"'+$state.tag+'","draft":true}]]'}else{'[[]]'};return
    }
}
if($command[0] -eq 'release' -and $command[1] -eq 'create'){
    if('--draft' -notin $command -or '--clobber' -in $command){throw 'Unsafe mutation command.'}
    $installer=@($state.assets|Where-Object name -like '*-setup.exe')[0]
    $blocked=$false
    try{[IO.File]::WriteAllText($installer.path,'tampering during upload')}catch [IO.IOException]{$blocked=$true}
    if(-not $blocked){throw 'Upload inputs were not locked.'}
    $payload=Join-Path ([IO.Path]::GetDirectoryName($installer.path)) ('BatteryManager-'+$state.version+'-win-x64.zip')
    $blocked=$false
    try{[IO.File]::WriteAllText($payload,'tampering with private payload during upload')}catch [IO.IOException]{$blocked=$true}
    if(-not $blocked){throw 'Private source-binding payload was not locked.'}
    'https://github.com/Aice-Chen/BatteryManager/releases/tag/'+$state.tag;return
}
if($command[0] -eq 'release' -and $command[1] -eq 'view'){
    $assets=@($state.assets|ForEach-Object{[pscustomobject]@{name=$_.name;size=$_.size}})
    if($state.mode -eq 'wrong-size'){$assets[0].size++}
    @{tagName=$state.tag;isDraft=($state.mode -ne 'not-draft');isPrerelease=$state.prerelease;targetCommitish=$state.commit;assets=$assets;url=('https://github.com/Aice-Chen/BatteryManager/releases/tag/'+$state.tag)}|ConvertTo-Json -Depth 5;return
}
throw 'Unexpected fake-gh command.'
'@
foreach($mode in @('success','success-preview','wrong-remote','missing-commit','existing-tag','existing-release','cli-failure','wrong-size','not-draft')){
    Test-PublishCase ('fake-gh:'+ $mode) ($mode -in @('success','success-preview')) {
        $version=if($mode -eq 'success-preview'){'0.2.0-preview.2'}else{'0.2.0'}
        $directory=New-PublishFixture $version
        $plan=Get-PublishPlan $directory
        Write-PublishJson (Join-Path $taskFakeDirectory 'state.json') @{mode=$mode;version=$version;tag=$plan.tag;commit=$taskCommit;prerelease=$plan.prerelease;assets=$plan.assets}
        Write-PortableText (Join-Path $taskFakeDirectory 'calls.jsonl') ''
        $result=& $taskScript -DeliveryDirectory $directory -TargetCommit $taskCommit -GhCommand $taskFake
        if(-not $result.created -or -not $result.draft -or $result.published){throw 'Unexpected draft result.'}
        $calls=@(Get-Content -LiteralPath (Join-Path $taskFakeDirectory 'calls.jsonl')|ForEach-Object{,$(ConvertFrom-Json $_)})
        $create=@($calls|Where-Object{$_[0] -eq 'release' -and $_[1] -eq 'create'})
        if($create.Count -ne 1 -or ($create[0] -join "`0") -cne ($plan.createArguments -join "`0")){throw 'Fake CLI did not receive exactly the planned argument array.'}
    }
    # A negative result alone cannot prove it failed before creating a draft.
    $calls=@(Get-Content -LiteralPath (Join-Path $taskFakeDirectory 'calls.jsonl')|ForEach-Object{,$(ConvertFrom-Json $_)})
    $creates=@($calls|Where-Object{$_[0] -eq 'release' -and $_[1] -eq 'create'})
    $expected=if($mode -in @('success','success-preview','wrong-size','not-draft')){1}else{0}
    $unexpectedMutation=@($calls|Where-Object{$_[0] -eq 'release' -and $_[1] -notin @('create','view')})
    $taskResults.Add([pscustomobject]@{name=('mutation-boundary:'+ $mode);passed=($creates.Count -eq $expected -and $unexpectedMutation.Count -eq 0);error=$null})
}
$taskFailures=@($taskResults|Where-Object{-not $_.passed})
Write-PublishJson (Join-Path $taskEvidence 'result.json') @{passed=$taskFailures.Count -eq 0;caseCount=$taskResults.Count;cases=$taskResults.ToArray();networkAccessed=$false;executablesLaunched=$false;usedFakeGh=$true}
if($taskFailures.Count){throw ('GitHub release tooling failed: '+($taskFailures.name -join ', ')+'. Evidence: '+$taskEvidence)}
[pscustomobject]@{passed=$true;caseCount=$taskResults.Count;evidenceDirectory=$taskEvidence;networkAccessed=$false;executablesLaunched=$false}
