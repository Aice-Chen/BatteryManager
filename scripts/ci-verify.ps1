# CI-only verification entry point. SDK acquisition belongs to setup-dotnet.
# Test processes use the existing fake transports; the WinUI app is only built.
[CmdletBinding()]
param(
    [ValidateSet('All', 'CodeTests', 'Tooling', 'Build')]
    [string]$Stage = 'All',
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskEvidence = Join-Path $taskRoot ('.artifacts/ci/' + $Stage + '/' +
    (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskEvidence
$taskChecks = [Collections.Generic.List[object]]::new()
$taskSdkVersion = $null
$taskStarted = [DateTimeOffset]::UtcNow
$taskOldHost = $env:DOTNET_HOST_PATH
$taskOldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT

function Invoke-CiCheck([string]$Name, [scriptblock]$Action) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    Write-Host "::group::$Name"
    try {
        & $Action 2>&1 | Tee-Object -FilePath (Join-Path $taskEvidence ($Name + '.log')) | Out-Host
    }
    catch {
        $failure = $_.Exception.Message
        $failure | Tee-Object -FilePath (Join-Path $taskEvidence ($Name + '.log')) -Append | Out-Host
    }
    finally {
        $clock.Stop()
        $taskChecks.Add([pscustomobject]@{
            name = $Name
            passed = $null -eq $failure
            elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
            error = $failure
        })
        Write-Host '::endgroup::'
    }
}

Push-Location $taskRoot
try {
    # No bootstrap script or SDK download fallback is invoked here.
    $taskDotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    $env:DOTNET_HOST_PATH = $taskDotnet
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $taskSdkVersion = (& $taskDotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Cannot select the global.json SDK: exit $LASTEXITCODE." }

    if ($Stage -in @('All', 'CodeTests')) {
        foreach ($taskProject in @('Core', 'Application', 'WindowsNative', 'App.Logic', 'Session', 'Cli')) {
            Invoke-CiCheck ('test-' + $taskProject) {
                & $taskDotnet test "tests/BatteryManager.$taskProject.Tests/BatteryManager.$taskProject.Tests.csproj" `
                    --configuration Release --nologo --logger "trx;LogFileName=$taskProject.trx" `
                    --results-directory (Join-Path $taskEvidence 'test-results')
                if ($LASTEXITCODE -ne 0) { throw "$taskProject tests failed with exit code $LASTEXITCODE." }
            }
        }
    }

    if ($Stage -in @('All', 'Tooling')) {
        foreach ($taskTool in @('portable', 'release', 'source-binding', 'github-release')) {
            Invoke-CiCheck ('tooling-' + $taskTool) {
                # These scripts use inert archive bytes and a fake GitHub CLI.
                & (Join-Path $PSScriptRoot "test-$taskTool-tooling.ps1") | ConvertTo-Json -Depth 10
            }
        }
    }

    if ($Stage -in @('All', 'Build')) {
        Invoke-CiCheck 'build-App-Release' {
            & $taskDotnet build src/BatteryManager.App/BatteryManager.App.csproj `
                --configuration Release --runtime win-x64 --nologo
            if ($LASTEXITCODE -ne 0) { throw "Application build failed with exit code $LASTEXITCODE." }
        }
    }
}
finally {
    $taskFinished = [DateTimeOffset]::UtcNow
    [ordered]@{
        stage = $Stage
        sdkVersion = $taskSdkVersion
        startedUtc = $taskStarted.ToString('o')
        finishedUtc = $taskFinished.ToString('o')
        checks = $taskChecks.ToArray()
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskEvidence 'summary.json') -Encoding utf8
    $env:DOTNET_HOST_PATH = $taskOldHost
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $taskOldTelemetry
    Pop-Location
    Write-Host "CI evidence: $taskEvidence"
}

if (@($taskChecks | Where-Object { -not $_.passed }).Count -ne 0) {
    throw "One or more CI checks failed. See $taskEvidence"
}
