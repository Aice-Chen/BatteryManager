$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { & (Join-Path $PSScriptRoot 'bootstrap.ps1') }
$taskOutput = Join-Path $taskRoot '.artifacts/preview'
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $taskDotnet publish src/BatteryManager.App/BatteryManager.App.csproj -c Release -r win-x64 --self-contained true --nologo -o $taskOutput
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    $taskPri = Join-Path $taskOutput 'BatteryManager.App.pri'
    if (-not (Test-Path -LiteralPath $taskPri) -or (Get-Item -LiteralPath $taskPri).Length -eq 0) { throw 'The preview is missing its WinUI application resources.' }
    Write-Output (Join-Path $taskOutput 'BatteryManager.App.exe')
} finally { Pop-Location }
