[CmdletBinding()]
param([string]$OutputDirectory = '.artifacts/packages', [switch]$Release)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskOutput = Resolve-PortableProjectPath $OutputDirectory $taskRoot -ArtifactsOnly
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet -PathType Leaf)) { throw 'Run scripts/bootstrap.ps1 before packaging.' }
[xml]$taskVersionFile = Get-Content -LiteralPath (Join-Path $taskRoot 'version.props') -Raw
$taskVersionPrefix = [string]$taskVersionFile.Project.PropertyGroup.VersionPrefix
$taskVersionSuffix = [string]$taskVersionFile.Project.PropertyGroup.VersionSuffix
$taskVersion = Get-PortableVersion $taskVersionPrefix $taskVersionSuffix -Release:$Release
$taskEdition = if ($taskVersionSuffix.Length -eq 0) { '便携正式版' } else { '便携开发预览' }
$taskSource = Get-PortableSourceSnapshot $taskRoot
$taskPackageName = 'BatteryManager-' + $taskVersion + '-win-x64'
$taskRun = Join-Path $taskOutput ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$taskPayload = Join-Path $taskRun $taskPackageName
$null = New-Item -ItemType Directory -Path $taskPayload
$taskBuildStarted = [DateTimeOffset]::UtcNow
$taskInformationVersion = $taskVersion + '+source.' + $taskSource.sha256
Push-Location $taskRoot
try {
    $taskSdkVersion = (& $taskDotnet --version | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The local .NET SDK could not report its version.' }
    & $taskDotnet publish src/BatteryManager.App/BatteryManager.App.csproj -c Release -r win-x64 --self-contained true --nologo -o $taskPayload `
        '-p:DebugSymbols=false' '-p:DebugType=None' '-p:IncludeSourceRevisionInInformationalVersion=false' "-p:InformationalVersion=$taskInformationVersion" 2>&1 |
        Tee-Object -FilePath (Join-Path $taskRun 'publish.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE; the fresh staging directory is retained for diagnosis." }
} finally { Pop-Location }
if ((Get-PortableSourceSnapshot $taskRoot).sha256 -ne $taskSource.sha256) { throw 'Build inputs changed during publishing; rerun after edits finish. No archive was produced.' }
$taskFontLicense = Join-Path $taskPayload 'licenses/Barlow-OFL.txt'
if (-not (Test-Path -LiteralPath $taskFontLicense) -or
    (Get-FileHash -LiteralPath $taskFontLicense).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskRoot 'src/BatteryManager.App/Assets/Fonts/OFL.txt')).Hash) { throw 'The bundled font license is missing or differs from its source.' }
if (@(Get-PortableFiles $taskPayload | Where-Object { [IO.Path]::GetExtension($_) -eq '.ttf' }).Count) { throw 'The tray font must be embedded, not copied as a loose TTF.' }
$null = Copy-PortableDependencyNotices $taskRoot $taskPayload
foreach ($required in @('BatteryManager.App.exe', 'BatteryManager.App.dll', 'BatteryManager.App.pri', 'BatteryManager.App.deps.json', 'BatteryManager.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll')) {
    $file = Join-Path $taskPayload $required
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Required self-contained application file is missing: $required" }
}
$taskActualInformation = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskPayload 'BatteryManager.App.dll')).ProductVersion
if ($taskActualInformation -ne $taskInformationVersion) { throw 'The published app version does not match this build source fingerprint.' }
Write-PortableText (Join-Path $taskPayload 'source-files.sha256') $taskSource.manifest
$taskBuildInfo = [ordered]@{
    schemaVersion = 1; product = 'Battery Manager'; version = $taskVersion; informationalVersion = $taskInformationVersion
    runtimeIdentifier = 'win-x64'; targetPlatform = 'Windows 11 x64'; configuration = 'Release'; selfContained = $true
    buildStartedUtc = $taskBuildStarted.ToString('O'); sdkVersion = $taskSdkVersion
    sourceFingerprint = $taskSource.sha256; sourceManifest = 'source-files.sha256'; sourceFileCount = $taskSource.fileCount
    sourceFingerprintRule = 'SHA256 of the UTF-8 source manifest, without BOM, ordinal path order, lowercase file hashes and LF line endings.'
}
Write-PortableText (Join-Path $taskPayload 'build-info.json') ($taskBuildInfo | ConvertTo-Json -Depth 5)
Write-PortableText (Join-Path $taskPayload '使用说明.txt') @"
Battery Manager $taskVersion — Windows 11 x64 $taskEdition

运行：将整个压缩包解压到有写入权限的文件夹，然后运行 BatteryManager.App.exe。
程序包含 .NET 和 Windows App SDK 运行文件，请保留同目录全部文件。
关闭主窗口后程序继续在托盘运行；通过托盘菜单“退出”结束程序，等待正在进行的硬件读取完成。
本版本不注册开机启动，不需要安装。

更新：先退出旧版本，再把新版本解压到另一个文件夹。
如需保留托盘优先级、名称、类型和主题，在首次启动新版前，将旧目录的 settings.json（如果存在）复制到新目录。
首次明确保存会将 v1/v2 设置升级为 v3，并先保存原字节至 settings.v1.bak 或 settings.v2.bak；已有备份不覆盖、不自动清理。
v3 保存用户确认的设备关联，不保存电量或连接历史。旧版程序不支持 v3，回退时请将对应版本备份另存为旧版目录中的 settings.json。
保留旧文件夹，确认新版正常后再自行处理。压缩包不包含个人设置或设备历史。

版本与来源：build-info.json 保存版本、构建时间、SDK 版本和源码指纹。
source-files.sha256 保存构建源文件摘要；SHA256SUMS.txt 保存包内文件摘要。
licenses 保存字体及依赖包提供的原始许可/声明，dependencies.json 记录对应版本和文件摘要。
这些摘要用于核对文件和定位版本；本程序未进行代码签名。
"@
$taskFiles = @(Get-PortableFiles $taskPayload)
$taskChecksums = foreach ($file in $taskFiles) {
    $relative = $file.Substring($taskPayload.Length + 1).Replace('\', '/')
    Assert-PortablePayloadPath $relative
    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
}
Write-PortableText (Join-Path $taskPayload 'SHA256SUMS.txt') (($taskChecksums -join "`n") + "`n")
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = Join-Path $taskRun ($taskPackageName + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($taskPayload, $taskArchive, [IO.Compression.CompressionLevel]::Optimal, $true)
$taskArchiveHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
Write-PortableText ($taskArchive + '.sha256') ($taskArchiveHash + '  ' + [IO.Path]::GetFileName($taskArchive) + "`n")
$taskVerified = & (Join-Path $PSScriptRoot 'test-portable-package.ps1') -PackagePath $taskArchive
[pscustomobject]@{ version = $taskVersion; sourceFingerprint = $taskSource.sha256; packagePath = $taskArchive; archiveSha256 = $taskArchiveHash; stagedDirectory = $taskPayload; fileCount = $taskVerified.fileCount; verified = $true }
