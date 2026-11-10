# Shared inert repositories for offline provenance tests. Never uses the project's index or commits.
function New-SourceBindingFixture([string]$Directory, [string]$ScriptsDirectory) {
    $null=New-Item -ItemType Directory -Path $Directory,(Join-Path $Directory 'src'),(Join-Path $Directory 'scripts'),(Join-Path $Directory 'packaging/windows')
    foreach($name in @('Directory.Build.props','version.props','global.json','BatteryManager.slnx')) { Write-PortableText (Join-Path $Directory $name) "fixture input $name`n" }
    Write-PortableText (Join-Path $Directory '.gitignore') ".artifacts/`n**/bin/`n**/obj/`nsrc/ignored-source.cs`n"
    Write-PortableText (Join-Path $Directory '.gitattributes') "src/explicit-crlf.cs text eol=crlf`nsrc/binary.bin -text`n"
    Write-PortableText (Join-Path $Directory 'src/example.cs') "first line`nsecond line`n"
    Write-PortableText (Join-Path $Directory 'src/explicit-crlf.cs') "text with explicit CRLF checkout`n"
    Write-PortableText (Join-Path $Directory 'src/中文 name.cs') "Unicode path`n"
    [IO.File]::WriteAllBytes((Join-Path $Directory 'src/binary.bin'), [byte[]]@(0,10,13,255,42))
    Write-PortableText (Join-Path $Directory 'packaging/windows/BatteryManager.iss') "; inert fixture, never compile`n"
    Write-PortableText (Join-Path $Directory 'packaging/windows/installed.json') '{"schemaVersion":1,"product":"BatteryManager","scope":"currentUser"}'
    foreach($name in @('portable-common.ps1','release-common.ps1','test-portable-package.ps1','publish-github-release.ps1')) {
        Copy-Item -LiteralPath (Join-Path $ScriptsDirectory $name) -Destination (Join-Path $Directory ('scripts/'+$name))
    }
    $null=Invoke-PortableGitText $Directory @('init','--quiet')
    $null=Invoke-PortableGitText $Directory @('config','core.autocrlf','true')
    $null=Invoke-PortableGitText $Directory @('config','user.name','Offline provenance fixture')
    $null=Invoke-PortableGitText $Directory @('config','user.email','fixture@example.invalid')
    $null=Invoke-PortableGitText $Directory @('config','commit.gpgsign','false')
    $null=Invoke-PortableGitText $Directory @('add','--all')
    $null=Invoke-PortableGitText $Directory @('-c','core.hooksPath=NUL','commit','--quiet','-m','Inert offline fixture')
    return $Directory
}
