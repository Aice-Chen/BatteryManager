# No network, compilation, or executable fixtures; each case gets an independent temporary Git repository.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
. (Join-Path $PSScriptRoot 'test-source-binding-fixtures.ps1')
$taskEvidence=Resolve-PortableProjectPath ('.artifacts/tests/source-binding/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')) $taskRoot -ArtifactsOnly
$null=New-Item -ItemType Directory -Path $taskEvidence
$taskResults=[Collections.Generic.List[object]]::new()
function Test-SourceCase([string]$Name,[bool]$Accept,[scriptblock]$Action) {
    $repository=New-SourceBindingFixture (Join-Path $taskEvidence $Name) $PSScriptRoot
    $source=Get-PortableCommittedSourceSnapshot $repository
    $failure=$null
    try { $null=& $Action $repository $source } catch { $failure=$_.Exception.Message }
    $expectedFailure=switch -Regex ($Name) {
        '^(modified-tracked-source|untracked-source|staged-source)$' { 'clean committed worktree' }
        '^(assume-unchanged-source|raw-hash-tampered|binary-eol-mutation)$' { 'Build input differs' }
        '^(ignored-source|missing-source|manifest-source-omitted)$' { 'does not cover exactly' }
        '^(wrong-existing-commit|git-hash-tampered)$' { 'canonical source manifest differs' }
        '^head-changed-' { 'source commit changed' }
        '^(custom-filter|encoding-transform)-refused$' { 'Unsupported Git content transformation' }
        '^local-attribute-' { 'info/attributes overrides' }
        '^installer-input-changed-' { 'Installer sources differ' }
    }
    $passed=if($Accept){$null -eq $failure}else{$null -ne $failure -and $expectedFailure -and $failure.Contains($expectedFailure)}
    $taskResults.Add([pscustomobject]@{name=$Name;passed=[bool]$passed;error=$failure})
}
Test-SourceCase 'clean-commit' $true { param($r,$s)
    $bound=Assert-PortableSourceBinding $r $s.commit $s.manifest $s.gitManifest $s.autoCrlf
    if($bound.fileCount -ne $s.fileCount -or $bound.records['src/binary.bin'].hash -cne (Get-FileHash -LiteralPath (Join-Path $r 'src/binary.bin')).Hash.ToLowerInvariant()){throw 'Binary file was not bound unchanged.'}
}
Test-SourceCase 'autocrlf-checkout' $true { param($r,$s)
    foreach($name in @('src/example.cs','src/explicit-crlf.cs')) { $path=Join-Path $r $name; Write-PortableText $path ([IO.File]::ReadAllText($path).Replace("`n","`r`n")) }
    $after=Get-PortableCommittedSourceSnapshot $r $s.commit
    if($after.gitSha256 -cne $s.gitSha256 -or $after.sha256 -ceq $s.sha256){throw 'Raw checkout and canonical Git fingerprints were conflated.'}
}
Test-SourceCase 'tracked-new-source' $true { param($r,$s)
    Write-PortableText (Join-Path $r 'scripts/new-source.ps1') "new committed source`n"
    $null=Invoke-PortableGitText $r @('add','scripts/new-source.ps1')
    $null=Invoke-PortableGitText $r @('-c','core.hooksPath=NUL','commit','--quiet','-m','New source fixture')
    $after=Get-PortableCommittedSourceSnapshot $r
    if($after.fileCount -ne $s.fileCount+1 -or $after.gitSha256 -ceq $s.gitSha256){throw 'New tracked source was omitted.'}
}
Test-SourceCase 'ignored-build-outputs' $true { param($r,$s)
    $null=New-Item -ItemType Directory -Path (Join-Path $r 'src/bin'),(Join-Path $r 'scripts/obj')
    Write-PortableText (Join-Path $r 'src/bin/generated.cs') 'not a source input'
    Write-PortableText (Join-Path $r 'scripts/obj/cache') 'not a source input'
    $after=Get-PortableCommittedSourceSnapshot $r
    if($after.sha256 -cne $s.sha256 -or $after.gitSha256 -cne $s.gitSha256){throw 'Ignored outputs changed source provenance.'}
}
Test-SourceCase 'modified-tracked-source' $false { param($r,$s)
    Write-PortableText (Join-Path $r 'src/example.cs') 'changed'
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'assume-unchanged-source' $false { param($r,$s)
    $null=Invoke-PortableGitText $r @('update-index','--assume-unchanged','src/example.cs')
    Write-PortableText (Join-Path $r 'src/example.cs') 'changed despite clean status'
    if((Invoke-PortableGitText $r @('status','--porcelain')).Length){throw 'fixture must hide its change from status'}
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'untracked-source' $false { param($r,$s)
    Write-PortableText (Join-Path $r 'src/new-source.cs') 'untracked input'
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'ignored-source' $false { param($r,$s)
    Write-PortableText (Join-Path $r 'src/ignored-source.cs') 'ignored input'
    if((Invoke-PortableGitText $r @('status','--porcelain')).Length){throw 'fixture must be ignored'}
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'missing-source' $false { param($r,$s)
    $null=Invoke-PortableGitText $r @('update-index','--assume-unchanged','src/example.cs')
    Remove-Item -LiteralPath (Join-Path $r 'src/example.cs')
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'staged-source' $false { param($r,$s)
    Write-PortableText (Join-Path $r 'src/example.cs') 'staged change'
    $null=Invoke-PortableGitText $r @('add','src/example.cs')
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'wrong-existing-commit' $false { param($r,$s)
    Write-PortableText (Join-Path $r 'src/example.cs') 'another valid commit'
    $null=Invoke-PortableGitText $r @('add','src/example.cs')
    $null=Invoke-PortableGitText $r @('-c','core.hooksPath=NUL','commit','--quiet','-m','Another fixture commit')
    $other=(Invoke-PortableGitText $r @('rev-parse','HEAD')).Trim()
    Assert-PortableSourceBinding $r $other $s.manifest $s.gitManifest $s.autoCrlf
}
Test-SourceCase 'head-changed-during-build' $false { param($r,$s)
    $null=Invoke-PortableGitText $r @('-c','core.hooksPath=NUL','commit','--allow-empty','--quiet','-m','Different commit, same source')
    Get-PortableCommittedSourceSnapshot $r $s.commit
}
Test-SourceCase 'raw-hash-tampered' $false { param($r,$s)
    Assert-PortableSourceBinding $r $s.commit ($s.manifest -replace '^[0-9a-f]{64}',('f'*64)) $s.gitManifest $s.autoCrlf
}
Test-SourceCase 'git-hash-tampered' $false { param($r,$s)
    Assert-PortableSourceBinding $r $s.commit $s.manifest ($s.gitManifest -replace '^[0-9a-f]{64}',('f'*64)) $s.autoCrlf
}
Test-SourceCase 'manifest-source-omitted' $false { param($r,$s)
    Assert-PortableSourceBinding $r $s.commit (($s.manifest.Split("`n")|Where-Object{$_ -notlike '*  src/example.cs'}) -join "`n") $s.gitManifest $s.autoCrlf
}
Test-SourceCase 'binary-eol-mutation' $false { param($r,$s)
    $changed=[byte[]]@(0,13,10,13,255,42)
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($changed)).ToLowerInvariant()
    Assert-PortableSourceBinding $r $s.commit ($s.manifest -replace '[0-9a-f]{64}  src/binary.bin',($hash+'  src/binary.bin')) $s.gitManifest $s.autoCrlf
}
Test-SourceCase 'custom-filter-refused' $false { param($r,$s)
    Write-PortableText (Join-Path $r '.gitattributes') "src/example.cs filter=fixture`n"
    $null=Invoke-PortableGitText $r @('add','.gitattributes')
    $null=Invoke-PortableGitText $r @('-c','core.hooksPath=NUL','commit','--quiet','-m','Unsupported filter fixture')
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'installer-inputs-match' $true { param($r,$s)
    $hashes=Get-PortableSourceHashes $s.manifest
    Assert-PortableInstallerSourceHashes $hashes['packaging/windows/BatteryManager.iss'] $hashes['packaging/windows/installed.json'] `
        (Get-FileHash -LiteralPath (Join-Path $r 'packaging/windows/BatteryManager.iss')).Hash.ToLowerInvariant() (Get-FileHash -LiteralPath (Join-Path $r 'packaging/windows/installed.json')).Hash.ToLowerInvariant()
}
foreach($changed in @('BatteryManager.iss','installed.json')) {
    Test-SourceCase ('installer-input-changed-'+$changed) $false { param($r,$s)
        $hashes=Get-PortableSourceHashes $s.manifest
        Write-PortableText (Join-Path $r ('packaging/windows/'+$changed)) 'Changed after ZIP packaging'
        Assert-PortableInstallerSourceHashes $hashes['packaging/windows/BatteryManager.iss'] $hashes['packaging/windows/installed.json'] `
            (Get-FileHash -LiteralPath (Join-Path $r 'packaging/windows/BatteryManager.iss')).Hash.ToLowerInvariant() (Get-FileHash -LiteralPath (Join-Path $r 'packaging/windows/installed.json')).Hash.ToLowerInvariant()
    }
}
Test-SourceCase 'explicit-crlf-with-autocrlf-disabled' $true { param($r,$s)
    $null=Invoke-PortableGitText $r @('config','core.autocrlf','false')
    $path=Join-Path $r 'src/explicit-crlf.cs'
    Write-PortableText $path ([IO.File]::ReadAllText($path).Replace("`n","`r`n"))
    $after=Get-PortableCommittedSourceSnapshot $r
    if($after.autoCrlf -cne 'false' -or $after.gitSha256 -cne $s.gitSha256){throw 'Committed text/eol attributes were not honored.'}
}
Test-SourceCase 'encoding-transform-refused' $false { param($r,$s)
    Write-PortableText (Join-Path $r '.gitattributes') "src/example.cs working-tree-encoding=UTF-8`n"
    $null=Invoke-PortableGitText $r @('add','.gitattributes')
    $null=Invoke-PortableGitText $r @('-c','core.hooksPath=NUL','commit','--quiet','-m','Unsupported encoding fixture')
    Get-PortableCommittedSourceSnapshot $r
}
Test-SourceCase 'local-attribute-override-refused' $false { param($r,$s)
    Write-PortableText (Join-Path $r '.git/info/attributes') "src/example.cs text`n"
    Assert-PortableSourceBinding $r $s.commit $s.manifest $s.gitManifest $s.autoCrlf
}
$failed=@($taskResults|Where-Object{-not $_.passed})
Write-PortableText (Join-Path $taskEvidence 'result.json') (@{passed=$failed.Count -eq 0;caseCount=$taskResults.Count;cases=$taskResults.ToArray();networkAccessed=$false;compiled=$false}|ConvertTo-Json -Depth 8)
if($failed.Count){throw ('Source binding tests failed: '+($failed.name -join ', ')+'. Evidence: '+$taskEvidence)}
[pscustomobject]@{passed=$true;caseCount=$taskResults.Count;evidenceDirectory=$taskEvidence}
