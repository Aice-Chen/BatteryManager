$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { & (Join-Path $PSScriptRoot 'publish-preview.ps1') }
Start-Process -FilePath $taskExe -WorkingDirectory (Split-Path -Parent $taskExe) -WindowStyle Hidden
