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
    foreach ($rootFile in @('Directory.Build.props', 'version.props', 'global.json', 'BatteryManager.slnx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $rootFile) -PathType Leaf)) { throw "Required source input is missing: $rootFile" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $ProjectRoot -File -Force) {
        if (Test-PortableSourcePath $file.Name) { $paths.Add($file.FullName) }
    }
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

# This is the explicit build-input scope; tests, documentation, and ignored build outputs are not source inputs.
function Test-PortableSourcePath([string]$Path) {
    if (-not $Path.Contains('/')) {
        return $Path -match '(?i)\.(props|targets|slnx)$' -or $Path -in @('global.json', '.gitattributes', '.editorconfig', 'NuGet.Config', 'LICENSE')
    }
    return $Path -cmatch '^(src|scripts|packaging)/' -and $Path -notmatch '(?i)(^|/)(bin|obj)/'
}

# Read git bytes through redirected streams, never through PowerShell's text pipeline (binary blobs and NUL paths).
function Invoke-PortableGitBytes([string]$ProjectRoot, [string[]]$Arguments, [string]$InputText = '', [switch]$AllowMissing) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.WorkingDirectory = $ProjectRoot
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($name in @('GIT_DIR','GIT_WORK_TREE','GIT_INDEX_FILE','GIT_OBJECT_DIRECTORY','GIT_ALTERNATE_OBJECT_DIRECTORIES','GIT_COMMON_DIR')) { $null = $start.Environment.Remove($name) }
    $start.Environment['GIT_NO_REPLACE_OBJECTS'] = '1'; $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'; $start.Environment['GIT_ATTR_NOSYSTEM'] = '1'
    foreach ($argument in @('-c','core.attributesFile=') + $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $output = [IO.MemoryStream]::new()
    try {
        $null = $process.Start()
        $copy = $process.StandardOutput.BaseStream.CopyToAsync($output)
        $errorRead = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText); $process.StandardInput.Close()
        $process.WaitForExit(); $null = $copy.GetAwaiter().GetResult(); $errorText = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -and -not ($AllowMissing -and $process.ExitCode -eq 1)) { throw "Git source verification failed: $errorText" }
        return ,$output.ToArray()
    } finally { $output.Dispose(); $process.Dispose() }
}
function Invoke-PortableGitText([string]$ProjectRoot, [string[]]$Arguments, [string]$InputText = '', [switch]$AllowMissing) {
    return [Text.UTF8Encoding]::new($false, $true).GetString((Invoke-PortableGitBytes $ProjectRoot $Arguments $InputText -AllowMissing:$AllowMissing))
}
function Assert-PortableGitRepository([string]$ProjectRoot) {
    $top = (Invoke-PortableGitText $ProjectRoot @('rev-parse','--show-toplevel')).Trim()
    if (-not [IO.Path]::GetFullPath($top).TrimEnd('\','/').Equals($ProjectRoot.TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Source verification must use the repository root.' }
    $infoAttributes = (Invoke-PortableGitText $ProjectRoot @('rev-parse','--git-path','info/attributes')).Trim()
    if (-not [IO.Path]::IsPathRooted($infoAttributes)) { $infoAttributes = Join-Path $ProjectRoot $infoAttributes }
    if ((Test-Path -LiteralPath $infoAttributes) -and (Get-Item -LiteralPath $infoAttributes).Length) { throw 'Local info/attributes overrides are not supported for release provenance.' }
}
function Get-PortableSourceHashes([string]$Manifest) {
    $hashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $casePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $Manifest.Split("`n")) {
        if (-not $line) { continue }
        if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$' -or -not (Test-PortableRelativePath $Matches[2]) -or -not $casePaths.Add($Matches[2])) { throw 'Invalid or duplicate source manifest entry.' }
        $hashes.Add($Matches[2], $Matches[1])
    }
    return ,$hashes
}
function Get-PortableGitSource([string]$ProjectRoot, [string]$Commit, [string]$AutoCrlf) {
    if ($Commit -cnotmatch '\A[0-9a-f]{40}\z' -or $AutoCrlf -cnotin @('true','false','input')) { throw 'Invalid source commit or checkout conversion policy.' }
    Assert-PortableGitRepository $ProjectRoot
    if ((Invoke-PortableGitText $ProjectRoot @('rev-parse','--verify',($Commit+'^{commit}'))).Trim() -cne $Commit) { throw 'The exact source commit is unavailable.' }
    $records = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in (Invoke-PortableGitText $ProjectRoot @('ls-tree','-r','-z','--full-tree',$Commit)).Split([char]0)) {
        if (-not $entry) { continue }
        if ($entry -cnotmatch '^([0-7]{6}) (blob|commit) ([0-9a-f]{40})\t(.+)$') { throw 'Unsupported Git source tree entry.' }
        $mode=$Matches[1]; $kind=$Matches[2]; $objectId=$Matches[3]; $path=$Matches[4]
        if (-not (Test-PortableSourcePath $path)) { continue }
        if (-not (Test-PortableRelativePath $path) -or -not $seen.Add($path) -or $kind -cne 'blob' -or $mode -notin @('100644','100755')) { throw 'Source paths must be unique portable regular files.' }
        $records.Add($path, [pscustomobject]@{path=$path;objectId=$objectId;hash=$null;crlfHash=$null})
    }
    foreach ($required in @('Directory.Build.props','version.props','global.json','BatteryManager.slnx')) { if (-not $records.ContainsKey($required)) { throw "Commit omits required source input: $required" } }
    $paths = [string[]]@($records.Keys); [Array]::Sort($paths, [StringComparer]::Ordinal)
    if ($paths.Count -gt 10000) { throw 'Source tree exceeds the supported file count.' }
    $attributes = Get-PortableGitAttributes $ProjectRoot $paths $Commit
    $objects = ($paths | ForEach-Object { $records[$_].objectId }) -join "`n"
    $bytes = Invoke-PortableGitBytes $ProjectRoot @('cat-file','--batch') ($objects+"`n")
    if ($bytes.Length -gt 128MB) { throw 'Source tree exceeds the supported byte count.' }
    $offset=0; $sha=[Security.Cryptography.SHA256]::Create()
    try {
        foreach ($path in $paths) {
            $end=$offset; while ($end -lt $bytes.Length -and $bytes[$end] -ne 10) { $end++ }
            $header=[Text.Encoding]::ASCII.GetString($bytes,$offset,$end-$offset)
            if ($header -cnotmatch '^([0-9a-f]{40}) blob ([0-9]+)$' -or $Matches[1] -cne $records[$path].objectId) { throw 'Git returned a different source object.' }
            $length=[int]::Parse($Matches[2]); $offset=$end+1
            if ($length -gt 16MB -or $offset+$length -ge $bytes.Length -or $bytes[$offset+$length] -ne 10) { throw 'Invalid or oversized Git source blob.' }
            $records[$path].hash=([BitConverter]::ToString($sha.ComputeHash($bytes,$offset,$length))).Replace('-','').ToLowerInvariant()
            $attr=$attributes[$path]
            $allowText=$attr.text -cne 'unset' -and ($attr.text -in @('set','auto') -or $attr.eol -in @('lf','crlf') -or $AutoCrlf -in @('true','input'))
            if ($allowText) {
                # Deliberately narrow EOL allowance: valid UTF-8 without CR, NUL or other binary controls.
                # Encoding, ident, and custom clean/smudge transformations are rejected separately.
                try { $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes,$offset,$length) } catch [Text.DecoderFallbackException] { $text=$null }
                if ($null -ne $text -and $text -notmatch '[\x00-\x08\x0B-\x1F\x7F]') { $records[$path].crlfHash=Get-PortableTextHash ($text.Replace("`n","`r`n")) }
            }
            $offset+=$length+1
        }
    } finally { $sha.Dispose() }
    if ($offset -ne $bytes.Length) { throw 'Unexpected trailing Git source objects.' }
    $manifest=(($paths | ForEach-Object { $records[$_].hash+'  '+$_ }) -join "`n")+"`n"
    return [pscustomobject]@{commit=$Commit;autoCrlf=$AutoCrlf;manifest=$manifest;sha256=Get-PortableTextHash $manifest;fileCount=$paths.Count;records=$records;paths=$paths}
}
function Get-PortableGitAttributes([string]$ProjectRoot, [string[]]$Paths, [string]$Commit = '') {
    $arguments=@('check-attr','-z','--stdin')
    if ($Commit) { $arguments+=('--source='+$Commit) }
    $arguments+=@('text','eol','filter','working-tree-encoding','ident')
    $values=(Invoke-PortableGitText $ProjectRoot $arguments (($Paths -join [char]0)+[char]0)).Split([char]0)
    $result=@{}
    for ($i=0; $i -lt $values.Length-1; $i+=3) {
        if (-not $result.ContainsKey($values[$i])) { $result[$values[$i]]=@{} }
        $result[$values[$i]][$values[$i+1]]=$values[$i+2]
        if ($values[$i+1] -in @('filter','working-tree-encoding','ident') -and $values[$i+2] -notin @('unspecified','unset')) { throw "Unsupported Git content transformation on $($values[$i])." }
    }
    return $result
}
function Assert-PortableSourceBinding([string]$ProjectRoot, [string]$Commit, [string]$Manifest, [string]$GitManifest, [string]$AutoCrlf) {
    $git=Get-PortableGitSource $ProjectRoot $Commit $AutoCrlf
    if ($GitManifest -cne $git.manifest) { throw 'The packaged canonical source manifest differs from the target commit.' }
    $raw=Get-PortableSourceHashes $Manifest
    if ($raw.Count -ne $git.fileCount) { throw 'The build input manifest does not cover exactly the target commit source files.' }
    foreach ($path in $git.paths) {
        $record=$git.records[$path]
        if (-not $raw.ContainsKey($path) -or ($raw[$path] -cne $record.hash -and $raw[$path] -cne $record.crlfHash)) { throw "Build input differs from the target commit: $path" }
    }
    return $git
}
function Assert-PortableInstallerSourceHashes([string]$ExpectedScript, [string]$ExpectedMarker, [string]$ActualScript, [string]$ActualMarker) {
    if ($ExpectedScript -cnotmatch '\A[0-9a-f]{64}\z' -or $ExpectedMarker -cnotmatch '\A[0-9a-f]{64}\z' -or
        $ActualScript -cne $ExpectedScript -or $ActualMarker -cne $ExpectedMarker) { throw 'Installer sources differ from the source manifest embedded in the portable payload.' }
}
function Get-PortableCommittedSourceSnapshot([string]$ProjectRoot, [string]$ExpectedCommit = '') {
    Assert-PortableGitRepository $ProjectRoot
    # Diff content rather than status's cached stat flags: a CRLF checkout can leave a clean file marked dirty until an index refresh.
    if ((Invoke-PortableGitText $ProjectRoot @('diff','--raw','--no-ext-diff','--no-textconv','--ignore-submodules=none','--')).Length -or
        (Invoke-PortableGitText $ProjectRoot @('diff','--cached','--raw','--no-ext-diff','--no-textconv','--ignore-submodules=none','--')).Length -or
        (Invoke-PortableGitText $ProjectRoot @('ls-files','--others','--exclude-standard','-z')).Length) { throw 'Packaging requires a clean committed worktree, including new source files.' }
    $commit=(Invoke-PortableGitText $ProjectRoot @('rev-parse','--verify','HEAD')).Trim()
    if ($ExpectedCommit -and $commit -cne $ExpectedCommit) { throw 'The source commit changed during packaging.' }
    $autoCrlf=(Invoke-PortableGitText $ProjectRoot @('config','--get','core.autocrlf') -AllowMissing).Trim().ToLowerInvariant()
    if (-not $autoCrlf) { $autoCrlf='false' }
    $raw=Get-PortableSourceSnapshot $ProjectRoot
    $git=Get-PortableGitSource $ProjectRoot $commit $autoCrlf
    $null=Assert-PortableSourceBinding $ProjectRoot $commit $raw.manifest $git.manifest $autoCrlf
    $null=Get-PortableGitAttributes $ProjectRoot $git.paths # Reject local transforms before hash-object can run a filter.
    $actual=(Invoke-PortableGitText $ProjectRoot @('-c',('core.autocrlf='+$autoCrlf),'hash-object','--stdin-paths') (($git.paths -join "`n")+"`n")).TrimEnd("`n").Split("`n")
    if ($actual.Count -ne $git.paths.Count) { throw 'Git did not hash every build input.' }
    for ($i=0; $i -lt $actual.Count; $i++) { if ($actual[$i] -cne $git.records[$git.paths[$i]].objectId) { throw "Working source differs from its committed Git object: $($git.paths[$i])" } }
    return [pscustomobject]@{manifest=$raw.manifest;sha256=$raw.sha256;fileCount=$raw.fileCount;commit=$commit;gitManifest=$git.manifest;gitSha256=$git.sha256;autoCrlf=$autoCrlf}
}

function Assert-PortablePayloadPath([string]$RelativePath) {
    if (-not (Test-PortableRelativePath $RelativePath)) { throw "Non-portable payload path: $RelativePath" }
    if ($RelativePath -match '(?i)(^|/)(logs|updates|installed\.json|management\.json[^/]*|settings(?:\.[^/]*)?\.json|settings\.json[^/]*|settings\.v\d+\.bak|diagnostic-[^/]*|last-error\.txt|TestResults|tests|\.artifacts|\.git|bin|obj)(/|$)' -or
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
