# Publication metadata helpers; no network access or changes to installed software.
function Assert-ReleaseAssetName([string]$Name, [string]$Version, [ValidateSet('installer', 'portable')][string]$Kind) {
    if (-not (Test-PortableVersion $Version)) { throw 'Invalid release version.' }
    $expected = if ($Kind -eq 'installer') { "BatteryManager-$Version-win-x64-setup.exe" } else { "BatteryManager-$Version-win-x64.zip" }
    if ($Name -cne $expected) { throw "The $Kind asset name does not match version $Version." }
}

function New-ReleaseManifest([string]$Version, [object[]]$Assets) {
    if (-not (Test-PortableVersion $Version)) { throw 'Invalid release version.' }
    $kinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $items = foreach ($asset in $Assets) {
        if ($asset.kind -cnotin @('installer', 'portable') -or -not $kinds.Add($asset.kind)) { throw 'Duplicate or unknown release asset kind.' }
        Assert-ReleaseAssetName $asset.fileName $Version $asset.kind
        if ($asset.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or $asset.size -isnot [long] -or $asset.size -le 0 -or $asset.size -gt 1GB) {
            throw 'Invalid release asset size or SHA256.'
        }
        [ordered]@{ kind = $asset.kind; fileName = $asset.fileName; sha256 = $asset.sha256; size = $asset.size }
    }
    if ($kinds.Count -ne 2) { throw 'A release needs one portable package and one installer.' }
    [ordered]@{ schemaVersion = 1; version = $Version; assets = @($items) }
}

function Assert-ReleaseSidecar([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Release asset does not exist.' }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = $Path + '.sha256'
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf) -or
        (Get-Content -LiteralPath $sidecar -Raw).TrimEnd("`r", "`n") -cne ($hash + '  ' + [IO.Path]::GetFileName($Path))) {
        throw 'Release asset sidecar is missing or does not match.'
    }
    $hash
}
