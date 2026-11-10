# Shared by the build and offline verification scripts. Does not launch the app.
function Test-PortableVersion([string]$Version) {
    if ($Version -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') { return $false }
    $separator = $Version.IndexOf('-')
    if ($separator -ge 0) {
        foreach ($identifier in $Version.Substring($separator + 1).Split('.')) {
            if ($identifier -cmatch '\A0[0-9]+\z') { return $false }
        }
    }
    return $true
}

function Get-PortableVersion([string]$Prefix, [AllowEmptyString()][string]$Suffix, [switch]$Release) {
    if ($Prefix -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z') {
        throw 'VersionPrefix must contain exactly three non-negative version numbers without leading zeroes.'
    }
    $version = if ($Suffix.Length -eq 0) { $Prefix } else { $Prefix + '-' + $Suffix }
    if (-not (Test-PortableVersion $version)) { throw 'VersionSuffix must be empty for a stable release, or a valid portable prerelease suffix.' }
    if ($Release -and $Suffix.Length -ne 0) { throw 'A release package requires an empty VersionSuffix in version.props.' }
    return $version
}

function Resolve-PortableProjectPath([string]$Path, [string]$ProjectRoot, [switch]$ArtifactsOnly) {
    $absolute = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $ProjectRoot $Path }))
    $boundary = if ($ArtifactsOnly) { Join-Path $ProjectRoot '.artifacts' } else { $ProjectRoot }
    if (-not $absolute.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path must be within $boundary : $absolute" }
    $cursor = $absolute
    while ($cursor.Length -ge $ProjectRoot.Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path is not allowed: $cursor" }
        if ($cursor.Equals($ProjectRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $absolute
}

function Test-PortableRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or $Path.StartsWith('/')) { return $false }
    foreach ($part in $Path.Split('/')) {
        if (-not $part -or $part -in @('.', '..') -or $part -match '[<>:"|?*\x00-\x1F]' -or
            $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { return $false }
    }
    return $true
}

function Get-PortableFiles([string]$Directory, [string[]]$ExcludedDirectories = @()) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    $files = [Collections.Generic.List[string]]::new()
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Redirected content is not allowed: $($item.FullName)" }
            if ($item.PSIsContainer) {
                if ($item.Name -notin $ExcludedDirectories) { $pending.Push($item.FullName) }
            } else { $files.Add($item.FullName) }
        }
    }
    $files.Sort([StringComparer]::Ordinal)
    return $files.ToArray()
}

function Get-PortableTextHash([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '').ToLowerInvariant()) }
    finally { $sha.Dispose() }
}

function Get-PortableSourceSnapshot([string]$ProjectRoot) {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($rootFile in @('Directory.Build.props', 'version.props', 'global.json', 'BatteryManager.slnx')) { $paths.Add((Join-Path $ProjectRoot $rootFile)) }
    foreach ($directory in @('src', 'scripts', 'packaging')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $directory))) { continue }
        foreach ($path in Get-PortableFiles (Join-Path $ProjectRoot $directory) @('bin', 'obj')) { $paths.Add($path) }
    }
    $paths.Sort([StringComparer]::Ordinal)
    $lines = foreach ($path in $paths) {
        $null = Resolve-PortableProjectPath $path $ProjectRoot
        $relative = $path.Substring($ProjectRoot.Length + 1).Replace('\', '/')
        if (-not (Test-PortableRelativePath $relative)) { throw "Non-portable source path: $relative" }
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
    }
    $manifest = ($lines -join "`n") + "`n"
    return [pscustomobject]@{ manifest = $manifest; sha256 = Get-PortableTextHash $manifest; fileCount = $paths.Count }
}

function Assert-PortablePayloadPath([string]$RelativePath) {
    if (-not (Test-PortableRelativePath $RelativePath)) { throw "Non-portable payload path: $RelativePath" }
    if ($RelativePath -match '(?i)(^|/)(logs|settings(?:\.[^/]*)?\.json|settings\.json[^/]*|settings\.v\d+\.bak|diagnostic-[^/]*|last-error\.txt|TestResults|tests|\.artifacts|\.git|bin|obj)(/|$)' -or
        $RelativePath -match '(?i)\.(pdb|user|trx)$') { throw "Local settings, diagnostics, or build-only files cannot be shipped: $RelativePath" }
}

function Write-PortableText([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

# Retain original notices from the exact restored packages used by this publish. No network fetches or rewritten terms.
function Copy-PortableDependencyNotices([string]$ProjectRoot, [string]$PayloadDirectory) {
    $assets = Get-Content -LiteralPath (Join-Path $ProjectRoot 'src/BatteryManager.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    $deps = Get-Content -LiteralPath (Join-Path $PayloadDirectory 'BatteryManager.App.deps.json') -Raw | ConvertFrom-Json
    $packages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'package') { $null = $packages.Add($library.Name) }
        if ($library.Value.type -eq 'runtimepack') { $null = $packages.Add(($library.Name -replace '^runtimepack\.', '')) }
    }
    # Some native Windows App SDK files are binplaced by targets and are absent from the managed deps list.
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Name -match '^Microsoft\.WindowsAppSDK(?:\.[^/]+)?/') { $null = $packages.Add($library.Name) }
    }
    $records = foreach ($package in ($packages | Sort-Object)) {
        $parts = $package.Split('/')
        if ($parts.Count -ne 2 -or -not (Test-PortableRelativePath $package)) { throw "Invalid restored package identity: $package" }
        $directory = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $package.ToLowerInvariant()
            if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
        }
        if (-not $directory) { throw "The exact restored dependency package could not be located: $package" }
        $nuspec = @(Get-ChildItem -LiteralPath $directory -File -Filter '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "The restored package does not have one provenance manifest: $package" }
        [xml]$metadata = Get-Content -LiteralPath $nuspec[0].FullName -Raw
        $licenseNode = $metadata.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')
        $urlNode = $metadata.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="licenseUrl"]')
        $license = if ($null -ne $licenseNode) { $licenseNode.InnerText } else { $null }
        $licenseUrl = if ($null -ne $urlNode) { $urlNode.InnerText } else { $null }
        $notices = @(Get-ChildItem -LiteralPath $directory -File | Where-Object Name -Match '^(?i:license|notice|third[-_]?party[-_]?notices)(?:\.[^.]*)?$')
        if (-not $notices.Count -and -not $license -and -not $licenseUrl) { throw "No original notice or license metadata was found for dependency: $package" }
        $destination = Join-Path $PayloadDirectory ('licenses/' + $package)
        $null = New-Item -ItemType Directory -Force -Path $destination
        # The original nuspec preserves the license URL/expression when the package supplies no local terms file.
        $copied = foreach ($file in @($nuspec[0]) + $notices) {
            $target = Join-Path $destination $file.Name
            Copy-Item -LiteralPath $file.FullName -Destination $target
            $hash = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
            if ((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -cne $hash) { throw "Dependency notice copy mismatch: $package/$($file.Name)" }
            [pscustomobject]@{ path = 'licenses/' + $package + '/' + $file.Name; sha256 = $hash }
        }
        [pscustomobject]@{ package = $package; licenseMetadata = $license; licenseUrl = $licenseUrl; hasLocalTermsOrNotices = $notices.Count -gt 0; files = @($copied) }
    }
    $index = [ordered]@{
        description = 'Original notices and NuGet manifests from the exact published managed/runtime dependencies and restored native Windows App SDK packages. License URLs are preserved as supplied; remote terms were not downloaded.'
        packages = @($records)
    }
    Write-PortableText (Join-Path $PayloadDirectory 'licenses/dependencies.json') ($index | ConvertTo-Json -Depth 8)
    return $index
}
