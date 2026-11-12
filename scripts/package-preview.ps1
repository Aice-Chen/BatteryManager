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
$taskEdition = if ($taskVersionSuffix.Length -eq 0) { '安装版' } else { '安装版开发预览' }
$taskSource = Get-PortableCommittedSourceSnapshot $taskRoot
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
$taskSourceAfter = Get-PortableCommittedSourceSnapshot $taskRoot $taskSource.commit
if ($taskSourceAfter.sha256 -cne $taskSource.sha256 -or $taskSourceAfter.gitSha256 -cne $taskSource.gitSha256 -or $taskSourceAfter.autoCrlf -cne $taskSource.autoCrlf) { throw 'Build inputs changed during publishing; rerun after edits finish. No archive was produced.' }
$taskFontLicense = Join-Path $taskPayload 'licenses/Barlow-OFL.txt'
$taskProjectLicense = Join-Path $taskPayload 'LICENSE.txt'
if (-not (Test-Path -LiteralPath $taskProjectLicense -PathType Leaf) -or
    (Get-FileHash -LiteralPath $taskProjectLicense).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskRoot 'LICENSE')).Hash) { throw 'The project license is missing or differs from its source.' }
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
Write-PortableText (Join-Path $taskPayload 'source-git-files.sha256') $taskSource.gitManifest
$taskBuildInfo = [ordered]@{
    schemaVersion = 1; product = 'Battery Manager'; version = $taskVersion; informationalVersion = $taskInformationVersion
    runtimeIdentifier = 'win-x64'; targetPlatform = 'Windows 11 x64'; configuration = 'Release'; selfContained = $true
    buildStartedUtc = $taskBuildStarted.ToString('O'); sdkVersion = $taskSdkVersion
    sourceFingerprint = $taskSource.sha256; sourceManifest = 'source-files.sha256'; sourceFileCount = $taskSource.fileCount
    sourceCommit = $taskSource.commit; sourceGitManifest = 'source-git-files.sha256'; sourceGitFingerprint = $taskSource.gitSha256
    sourceAutoCrlf = $taskSource.autoCrlf; sourceBindingRule = 'git-blob-with-utf8-eol-v1'
    sourceFingerprintRule = 'SHA256 of the UTF-8 source manifest, without BOM, ordinal path order, lowercase file hashes and LF line endings.'
}
Write-PortableText (Join-Path $taskPayload 'build-info.json') ($taskBuildInfo | ConvertTo-Json -Depth 5)
Write-PortableText (Join-Path $taskPayload '使用说明.txt') @"
Battery Manager $taskVersion — Windows 11 x64 $taskEdition

安装：运行 BatteryManager 安装器并选择安装位置；以后可从开始菜单启动。
程序包含 .NET 和 Windows App SDK 运行文件，请保留同目录全部文件。
关闭主窗口后程序继续在托盘运行；通过托盘菜单“退出”结束程序，等待正在进行的硬件读取完成。
自启动：可在设置页登记“登录 Windows 时启动”，登录后只在托盘运行；若 Windows 禁用了该项，请在系统“应用 → 启动”中开启。

更新：自动更新默认开启，可在设置页关闭；原有关闭状态继续保留。发现 GitHub 正式新版后自动下载校验、保存设置、正常退出、安装并重新启动，升级沿用原安装位置。也可手动检查、下载并确认安装。
配置：托盘优先级、名称、类型、主题和设备关联保存在当前用户 LocalAppData 下的 BatteryManager/settings.json；更换安装位置继续沿用现有设置。
management.json 保存自动更新开关和检查时间。启动30秒后判断检查资格，此后每小时判断一次；两次自动尝试至少间隔24小时，失败也计入间隔。安装提交前可取消自动任务。
首次明确保存会将 v1/v2 设置升级为 v3，并先保存原字节至 settings.v1.bak 或 settings.v2.bak；已有备份不覆盖、不自动清理。
v3 保存用户确认的设备关联，不保存电量或连接历史。升级保留个人设置并清理新版不再使用的程序文件。卸载删除个人设置、备份、日志、更新缓存和本安装拥有的启动登记；用户自行放入安装目录的其他文件保留。不提供便携版或配置导入功能。
构建过程使用的内部 ZIP 仅用于安装器负载与来源校验，发行附件只提供安装器。

版本与来源：build-info.json 保存版本、构建时间、SDK 版本、源码指纹和完整 Git 提交。
source-files.sha256 保存实际构建文件摘要；source-git-files.sha256 保存该提交的规范文件摘要；SHA256SUMS.txt 保存包内文件摘要。
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
$taskVerified = & (Join-Path $PSScriptRoot 'test-portable-package.ps1') -PackagePath $taskArchive -TargetCommit $taskSource.commit
[pscustomobject]@{ version = $taskVersion; sourceFingerprint = $taskSource.sha256; sourceCommit = $taskSource.commit; sourceCommitBindingVerified = $taskVerified.sourceCommitBindingVerified; packagePath = $taskArchive; archiveSha256 = $taskArchiveHash; stagedDirectory = $taskPayload; fileCount = $taskVerified.fileCount; verified = $true }
