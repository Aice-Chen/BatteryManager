# Validate a prepared delivery and create a GitHub draft. Publication remains a separate reviewed action.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DeliveryDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$TargetCommit,
    [switch]$PlanOnly,
    [string]$GhCommand = 'gh'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'release-common.ps1')
$taskRepository = 'Aice-Chen/BatteryManager'
$taskRemote = 'github.com/' + $taskRepository
$taskCommit = $TargetCommit.ToLowerInvariant()
$taskDirectory = Resolve-PortableProjectPath $DeliveryDirectory $taskRoot -ArtifactsOnly
if (-not (Test-Path -LiteralPath $taskDirectory -PathType Container)) { throw 'Prepared delivery directory does not exist.' }

function Read-PublicationMetadata([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 1MB) { throw 'Release metadata is missing or oversized.' }
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
    $json = [Text.Json.JsonDocument]::Parse($text)
    try { Assert-UniqueJsonFields $json.RootElement } finally { $json.Dispose() }
    $text | ConvertFrom-Json
}
function Assert-UniqueJsonFields([Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Duplicate JSON property in release metadata.' }
            Assert-UniqueJsonFields $property.Value
        }
    } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-UniqueJsonFields $item }
    }
}
function Assert-PublicationFields([object]$Value, [string[]]$Names) {
    $actual = @($Value.PSObject.Properties | ForEach-Object Name)
    if ($actual.Count -ne $Names.Count -or @($actual | Where-Object { $_ -cnotin $Names }).Count) { throw 'Unexpected fields in publication metadata.' }
}
function Assert-PublicationSchema([object]$Value) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -ne 1) { throw 'Unsupported publication metadata schema.' }
}
function Invoke-PublicationGh([string[]]$Arguments) {
    $global:LASTEXITCODE = 0
    $output = @(& $taskGh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ('GitHub CLI failed; the script did not request any overwrite or cleanup. ' + ($output -join "`n")) }
    $output -join "`n"
}

$taskDeliveryPath = Resolve-PortableProjectPath (Join-Path $taskDirectory 'delivery.json') $taskRoot -ArtifactsOnly
$taskInitial = Read-PublicationMetadata $taskDeliveryPath
if (-not (Test-PortableVersion $taskInitial.version)) { throw 'The delivery version is invalid.' }
$taskVersion = [string]$taskInitial.version
$taskNames = @("BatteryManager-$taskVersion-win-x64-setup.exe", "BatteryManager-$taskVersion-win-x64.zip",
    "BatteryManager-$taskVersion-win-x64-setup.exe.sha256", "BatteryManager-$taskVersion-win-x64.zip.sha256",
    'update-manifest.json', 'release-notes.md')
$taskLeases = [Collections.Generic.List[IO.FileStream]]::new()
try {
    # Lock every reviewed input while hashing and, when requested, while gh reads the assets.
    # Local build/delivery records are read here, but are never added to the upload argument list.
    foreach ($name in @($taskNames) + @('delivery.json', 'installer-build.json')) {
        $path = Resolve-PortableProjectPath (Join-Path $taskDirectory $name) $taskRoot -ArtifactsOnly
        if ($path.Contains('#')) { throw 'A release path cannot contain the GitHub asset-label separator #.' }
        $taskLeases.Add([IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    }
    $taskDelivery = Read-PublicationMetadata $taskDeliveryPath
    Assert-PublicationFields $taskDelivery @('schemaVersion','version','tag','repository','prerelease','sourceFingerprint','directory','uploaded')
    Assert-PublicationSchema $taskDelivery.schemaVersion
    if ($taskDelivery.version -cne $taskVersion -or $taskDelivery.tag -cne ('v' + $taskVersion) -or
        $taskDelivery.repository -cne $taskRepository -or $taskDelivery.prerelease -isnot [bool] -or
        $taskDelivery.prerelease -ne $taskVersion.Contains('-') -or $taskDelivery.uploaded -isnot [bool] -or $taskDelivery.uploaded -or
        $taskDelivery.sourceFingerprint -cnotmatch '\A[0-9a-f]{64}\z' -or
        -not [string]::Equals([IO.Path]::GetFullPath($taskDelivery.directory), $taskDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Delivery repository, version, channel, directory or publication state is inconsistent.'
    }
    $taskManifest = Read-PublicationMetadata (Join-Path $taskDirectory 'update-manifest.json')
    Assert-PublicationFields $taskManifest @('schemaVersion','version','assets')
    Assert-PublicationSchema $taskManifest.schemaVersion
    if ($taskManifest.version -cne $taskVersion) { throw 'Update manifest and delivery versions differ.' }
    foreach ($asset in $taskManifest.assets) { Assert-PublicationFields $asset @('kind','fileName','sha256','size') }
    $null = New-ReleaseManifest $taskVersion @($taskManifest.assets)
    foreach ($asset in $taskManifest.assets) {
        $path = Join-Path $taskDirectory $asset.fileName
        if ((Assert-ReleaseSidecar $path) -cne $asset.sha256 -or (Get-Item -LiteralPath $path).Length -ne $asset.size) {
            throw 'A release asset differs from its update manifest.'
        }
    }
    $taskPortable = & (Join-Path $PSScriptRoot 'test-portable-package.ps1') -PackagePath (Join-Path $taskDirectory "BatteryManager-$taskVersion-win-x64.zip")
    if (-not $taskPortable.passed -or $taskPortable.version -cne $taskVersion -or $taskPortable.sourceFingerprint -cne $taskDelivery.sourceFingerprint) {
        throw 'Portable payload and delivery provenance differ.'
    }
    $taskBuild = Read-PublicationMetadata (Join-Path $taskDirectory 'installer-build.json')
    Assert-PublicationSchema $taskBuild.schemaVersion
    $taskInstaller = @($taskManifest.assets | Where-Object kind -CEQ 'installer')[0]
    if ($taskBuild.testOnly -isnot [bool] -or $taskBuild.testOnly -or $taskBuild.version -cne $taskVersion -or
        $taskBuild.installerSha256 -cne $taskInstaller.sha256 -or $taskBuild.portableSha256 -cne $taskPortable.archiveSha256 -or
        $taskBuild.payloadSourceFingerprint -cne $taskDelivery.sourceFingerprint) {
        throw 'Installer build record does not match this production delivery.'
    }
    $taskAssets = foreach ($name in $taskNames) {
        $path = Join-Path $taskDirectory $name
        $length = [long](Get-Item -LiteralPath $path).Length
        if ($length -le 0 -or (($name -in @('update-manifest.json','release-notes.md') -or $name.EndsWith('.sha256')) -and $length -gt 1MB)) {
            throw 'An upload asset is empty or oversized.'
        }
        [pscustomobject]@{name=$name;path=$path;size=$length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    $taskCreate = @('release','create',$taskDelivery.tag,'--repo',$taskRemote,'--target',$taskCommit,
        '--title',('Battery Manager '+$taskVersion),'--notes-file',(Join-Path $taskDirectory 'release-notes.md'),'--draft','--latest=false')
    if ($taskDelivery.prerelease) { $taskCreate += '--prerelease' }
    $taskCreate += @($taskAssets | ForEach-Object path)
    $taskPlan = [ordered]@{
        schemaVersion=1;repository=$taskRepository;version=$taskVersion;tag=$taskDelivery.tag;targetCommit=$taskCommit
        draft=$true;prerelease=$taskDelivery.prerelease;assets=@($taskAssets);createArguments=$taskCreate
        sourceFingerprint=$taskDelivery.sourceFingerprint;sourceCommitBindingVerified=$false
        planOnly=[bool]$PlanOnly;networkAccessed=$false;created=$false;published=$false
    }
    if ($PlanOnly) { [pscustomobject]$taskPlan; return }

    $taskGh = (Get-Command $GhCommand -CommandType Application,ExternalScript -ErrorAction Stop).Source
    $taskRepo = (Invoke-PublicationGh @('repo','view',$taskRemote,'--json','nameWithOwner,url')) | ConvertFrom-Json
    if ($taskRepo.nameWithOwner -cne $taskRepository -or $taskRepo.url -cne ('https://github.com/'+$taskRepository)) { throw 'GitHub resolved a different repository.' }
    $taskRemoteCommit = (Invoke-PublicationGh @('api','--hostname','github.com',"repos/$taskRepository/commits/$taskCommit")) | ConvertFrom-Json
    if ($taskRemoteCommit.sha -cne $taskCommit) { throw 'The exact target commit was not found in the destination repository.' }
    $taskRefs = @((Invoke-PublicationGh @('api','--hostname','github.com',"repos/$taskRepository/git/matching-refs/tags/$($taskDelivery.tag)")) | ConvertFrom-Json)
    if (@($taskRefs | Where-Object ref -CEQ ('refs/tags/'+$taskDelivery.tag)).Count) { throw 'The release tag already exists; this command never replaces tags.' }
    $taskPages = (Invoke-PublicationGh @('api','--hostname','github.com',"repos/$taskRepository/releases?per_page=100",'--paginate','--slurp')) | ConvertFrom-Json
    foreach ($page in $taskPages) {
        foreach ($release in $page) {
            if ($release.tag_name -ceq $taskDelivery.tag) { throw 'A release or draft for this tag already exists; this command never overwrites it.' }
        }
    }
    $null = Invoke-PublicationGh $taskCreate
    $taskRemoteRelease = (Invoke-PublicationGh @('release','view',$taskDelivery.tag,'--repo',$taskRemote,'--json','tagName,isDraft,isPrerelease,targetCommitish,assets,url')) | ConvertFrom-Json
    if ($taskRemoteRelease.tagName -cne $taskDelivery.tag -or $taskRemoteRelease.isDraft -isnot [bool] -or -not $taskRemoteRelease.isDraft -or
        $taskRemoteRelease.isPrerelease -isnot [bool] -or $taskRemoteRelease.isPrerelease -ne $taskDelivery.prerelease -or
        $taskRemoteRelease.targetCommitish -cne $taskCommit -or @($taskRemoteRelease.assets).Count -ne $taskNames.Count) {
        throw 'The uploaded draft metadata differs from the reviewed plan. It was left for inspection; no repair or publication was attempted.'
    }
    foreach ($asset in $taskAssets) {
        $remoteAssets = @($taskRemoteRelease.assets | Where-Object name -CEQ $asset.name)
        if ($remoteAssets.Count -ne 1 -or $remoteAssets[0].size -ne $asset.size) {
            throw 'The uploaded draft asset names or lengths differ from the plan. The draft was left for inspection.'
        }
    }
    $taskPlan.networkAccessed=$true; $taskPlan.created=$true
    $taskPlan['url']=$taskRemoteRelease.url
    [pscustomobject]$taskPlan
} finally {
    foreach ($lease in $taskLeases) { $lease.Dispose() }
}
