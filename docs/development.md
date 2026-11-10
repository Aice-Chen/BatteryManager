# 开发维护说明

## 模块边界

- Core 保存设备证据、身份、连接与电量规则，不依赖 Windows 或 WinUI。
- WindowsNative 依赖 Core，负责 Windows API、原生通信与已验证协议。读取器显式提交连接方式；仅有连接方式元数据不能让设备变为在线。
- Application 依赖 Core，包含可脱离窗口测试的设置规则、配置存储、数据位置与更新服务。
- App 组装上述模块并负责 WinUI、托盘和 Windows 生命周期。Cli 复用正式采集与状态链。
- Application.Tests 测设置及更新纯逻辑；App.Logic.Tests 测无窗口的应用场景、诊断和布局计算；WindowsNative.Tests 专注读取链路。

新增设备时，先核实接口与协议范围，增加对应解析/读取测试，再通过 DeviceSources 注册。共享 HID 扫描只管理枚举与生命周期；各协议的型号判断、状态含义与命令顺序由各自读取器负责。

## Git 与版本

源码仓库：https://github.com/Aice-Chen/BatteryManager 。`main` 保存已核对基线，开发分支使用 `codex/` 前缀。按可独立审查的功能提交；通过相关测试后再合并。每个交付版本从 `version.props` 取版本号，发布标签使用 `v` 加完整版本。

公开 Git 只保存源码、测试、维护工具及面向使用者的文档。本机协作历史、配置、硬件证据、下载工具、编译产物和测试输出留在被忽略的目录；不将个人日志复制进发布包。

## 验证入口

```powershell
./scripts/verify.ps1
./scripts/test-release-tooling.ps1
./.tools/dotnet/dotnet.exe build src/BatteryManager.App/BatteryManager.App.csproj -c Release
```

`verify.ps1` 顺序运行 Core、Application、WindowsNative、App.Logic、Session、Cli 测试。发布工具检查使用本地假数据，不下载、执行或安装软件。

涉及设备状态或保存流程的修改，继续使用已有应用测试场景验收。原生截图与真机物理操作只在相关变更确实需要时安排。

## 输出组织

| 位置 | 内容 |
|---|---|
| `.artifacts/test-results` | 代码测试结果 |
| `.artifacts/tests/<主题>` | 定向检查与小型测试样本 |
| `.artifacts/packages` | 干净构建的便携包及其构建目录 |
| `.artifacts/installers` | 安装器构建及编译日志 |
| `.artifacts/releases` | 已组装的版本交付目录 |
| `.artifacts/evidence/<版本或阶段>` | 验收摘要与证据入口 |
| `.artifacts/hardware` | 原始硬件证据 |
| `.artifacts/archive` | 已归档历史证据 |
| `docs/releases/<版本>` | 发布说明与变更记录 |

一次性调查结果留在对应输出目录；需要长期复用的命令才进入 scripts 或 tools。已有正式交付目录不覆盖；新的候选交付用独立目录。清理按明确目录和验证后的边界执行，保留当前发行物及必要回退版本。

## 安装与更新

`packaging/windows/BatteryManager.iss` 构建当前用户安装器，允许选择安装目录，升级复用原目录。`installed.json` 标识安装版；安装版用户数据使用 LocalApplicationData 下的 BatteryManager，便携版仍使用程序目录。卸载保留独立用户数据目录和用户自行添加的文件。

编译入口为 `scripts/package-installer.ps1`，接收通过校验的便携包和项目内已有的可信 Inno Setup 编译器，不负责下载或安装工具。本地验证使用 7.1.1；从官方发行取得的工具先核对发布摘要及 Authenticode，再放入 `.tools/inno-7.1.1`。编译器的有效签名不等于本项目生成的安装包已签名。

`scripts/prepare-release.ps1` 校验安装包与便携包版本、来源指纹和摘要一致，再生成独立交付目录与 `update-manifest.json`。当前仅准备本地材料，不执行上传。

更新客户端仅发现公开仓库的正式发布，忽略草稿和预览。先核对仓库、标签、版本、架构、文件名及大小，再下载到临时文件；实际长度与 SHA256 全部一致才保留完成文件。取消和失败不替换已有完成文件。HTTPS 与仓库发布权限构成当前来源信任边界，摘要不能代替发布者独立数字签名。

`UpdateCoordinator` 处理检查、下载、取消和保存排空，AppManagementController 管理 UI 与定时入口。启动 30 秒后按检查资格执行，随后每小时判断一次；两次自动尝试至少间隔 24 小时，失败也计入间隔。当前进程使用单调时间，跨重启用保存的 UTC 时间。自动检查默认开启，可在设置关闭；不会自动下载或安装。

安装前重新校验文件并持有只读句柄，等待已接受设置保存及原生 I/O 排空。安装器通过 `/WAITPID` 与 `/DIR` 核实原程序的完整路径，持有进程句柄等待真正退出后才安装；超过 30 秒拒绝继续，不强杀进程。普通安装仍要求先退出当前应用。

自启使用当前用户 Run 的 BatteryManager 项，写入完整且带引号的程序路径。StartupApproved 仅读取；Windows 禁用或未知状态单独显示，不绕过系统设置。用户在程序中明确开启时才修复已识别的旧路径；不覆盖同名但无法识别的命令。

## 版本交付顺序

1. 修改 `version.props`，在 `docs/releases/<版本>` 写使用者能看到的变更和已知限制。
2. 运行代码与受影响的应用场景。安装/更新改动额外运行隔离生命周期及进程交接测试。
3. 使用 `package-preview.ps1` 或正式入口 `package-release.ps1` 构建便携 ZIP；修改任何源输入后重新构建。
4. 把该 ZIP 交给 `package-installer.ps1 -PortablePackagePath <路径>`，确保安装器与便携包来自同一负载。
5. 使用 `prepare-release.ps1` 生成独立交付目录；预览必须带 `-Prerelease`。本地的 `installer-build.json`、`delivery.json` 含构建路径，不作为公开附件。
6. 先运行 `publish-github-release.ps1 -DeliveryDirectory <目录> -TargetCommit <40位提交> -PlanOnly` 审核六个公开附件及目标提交。去掉 `-PlanOnly` 才调用 GitHub CLI，且只创建草稿，拒绝覆盖已有标签或发布。正式发布标签使用 `v<版本>`；正式更新客户端忽略预览与草稿。发布后保留该版本的 ZIP、安装器、摘要、manifest 和验证入口。

发布脚本核实目标提交存在、包与来源指纹一致，并回读草稿附件名称和长度；当前不会将包内每个源码摘要与该 Git 提交逐一比对。应从已审核的干净工作树构建，并人工核对目标提交。`test-github-release-tooling.ps1` 使用本地 CLI 替身验证失败、覆盖保护和白名单，不访问 GitHub。

应用管理交互测试入口为 `test-app-management.ps1 -PreviewExe <项目内隔离程序>`，替换网络、注册表、硬件及安装动作，仍运行正式协调和控件。`test-installer-lifecycle.ps1` 与 `test-installer-handoff.ps1` 先生成计划，复核后对该计划传 `-Execute -PreparedDirectory <目录>`；独立 AppId/互斥体、禁用系统注册和程序启动，使用假负载或最小进程组件。测试版安装器不能进入发布链。

隔离测试不代表日常配置迁移、真实 Windows 登录或线上新旧正式版升级已验收。GitHub Actions 当前尚未启用；本地脚本是现行验证入口。
