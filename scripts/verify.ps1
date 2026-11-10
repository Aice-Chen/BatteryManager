$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { & (Join-Path $PSScriptRoot 'bootstrap.ps1') }
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $taskResults = Join-Path $taskRoot '.artifacts/test-results'
    foreach ($taskTest in @('Core', 'WindowsNative', 'Session', 'Cli')) {
        & $taskDotnet test "tests/BatteryManager.$taskTest.Tests/BatteryManager.$taskTest.Tests.csproj" --nologo --logger 'trx' --results-directory $taskResults
        if ($LASTEXITCODE -ne 0) { throw "$taskTest tests failed with exit code $LASTEXITCODE." }
    }
} finally { Pop-Location }
