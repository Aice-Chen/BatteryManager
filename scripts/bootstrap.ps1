$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSdkRoot = Join-Path $taskRoot '.tools/dotnet'
$taskArchive = Join-Path $taskRoot '.tools/dotnet-sdk-10.0.403-win-x64.zip'
$taskSdkVersion = '10.0.403'
if (Test-Path -LiteralPath (Join-Path $taskSdkRoot 'sdk/10.0.403')) { Write-Output (Join-Path $taskSdkRoot 'dotnet.exe'); exit 0 }
New-Item -ItemType Directory -Path $taskSdkRoot -Force | Out-Null
$taskMetadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$taskSdk = $taskMetadata.releases | ForEach-Object { $_.sdks } | Where-Object version -EQ $taskSdkVersion | Select-Object -First 1
$taskFile = $taskSdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $taskFile -or $taskFile.url -notlike 'https://builds.dotnet.microsoft.com/*') { throw 'Official SDK archive not found.' }
if (-not (Test-Path -LiteralPath $taskArchive)) { Invoke-WebRequest -Uri $taskFile.url -OutFile $taskArchive }
if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA512).Hash -ne $taskFile.hash) { throw 'SDK SHA512 mismatch.' }
Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskSdkRoot -Force
Write-Output (Join-Path $taskSdkRoot 'dotnet.exe')
