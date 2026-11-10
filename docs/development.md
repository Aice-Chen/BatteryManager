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
./scripts/ci-verify.ps1 -Stage All -DotnetPath ./.tools/dotnet/dotnet.exe
```

`verify.ps1` 顺序运行 Core、Application、WindowsNative、App.Logic、Session、Cli 测试。发布工具检查使用本地假数据，不下载、执行或安装软件。

涉及设备状态或保存流程的修改，继续使用已有应用测试场景验收。原生截图与真机物理操作只在相关变更确实需要时安排。

## 输出组织

| 位置 | 内容 |
|---|---|
| `.artifacts/test-results` | 代码测试结果 |
| `.artifacts/ci/<阶段>/<运行>` | 本机或 GitHub CI 的测试、构建日志与摘要 |
| `.artifacts/tests/<主题>` | 定向检查与小型测试样本 |
| `.artifacts/packages` | 干净构建的便携包及其构建目录 |
| `.artifacts/installers` | 安装器构建及编译日志 |
| `.artifacts/releases` | 已组装的版本交付目录 |
| `.artifacts/evidence/<版本或阶段>` | 验收摘要与证据入口 |
| `.artifacts/installed/BatteryManager` | 本机已登记的安装版目录；运行目录，不随测试输出清理 |
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
3. 提交已审核的源代码与版本号，在干净工作树使用 `package-preview.ps1` 或正式入口 `package-release.ps1` 构建便携 ZIP；修改任何源输入后重新构建。打包前后必须保持同一提交和输入文件。
4. 把该 ZIP 交给 `package-installer.ps1 -PortablePackagePath <路径>`，确保安装器与便携包来自同一负载。
5. 使用 `prepare-release.ps1` 生成独立交付目录；预览必须带 `-Prerelease`。本地的 `installer-build.json`、`delivery.json` 含构建路径，不作为公开附件。
6. 先运行 `publish-github-release.ps1 -DeliveryDirectory <目录> -TargetCommit <40位提交> -PlanOnly` 审核六个公开附件及目标提交。去掉 `-PlanOnly` 才调用 GitHub CLI，且只创建草稿，拒绝覆盖已有标签或发布。正式发布标签使用 `v<版本>`；正式更新客户端忽略预览与草稿。发布后保留该版本的 ZIP、安装器、摘要、manifest 和验证入口。

新包同时记录实际构建字节的 `source-files.sha256` 和 Git 规范字节的 `source-git-files.sha256`。发布校验逐项比较文件集合、摘要与指定的完整提交，只容许明确限定的文本换行转换；额外、缺失或不同内容的源码不能通过。旧包仍可做完整性检查，但缺少提交绑定信息的旧包不能走新的发布入口。`test-github-release-tooling.ps1` 使用本地 CLI 替身验证失败、覆盖保护和白名单，不访问 GitHub。

应用管理交互测试入口为 `test-app-management.ps1 -PreviewExe <项目内隔离程序>`，替换网络、注册表、硬件及安装动作，仍运行正式协调和控件。`test-installer-lifecycle.ps1` 与 `test-installer-handoff.ps1` 先生成计划，复核后对该计划传 `-Execute -PreparedDirectory <目录>`；独立 AppId/互斥体、禁用系统注册和程序启动，使用假负载或最小进程组件。测试版安装器不能进入发布链。

`test-installer-startup.ps1` 验证真实 Inno 任务选择与登记逻辑，测试键限定到随机的当前用户隔离子树，正式 Run/StartupApproved/卸载登记仅作前后只读核对。安装默认值来自当时实际的 Run 注册，显式 `/TASKS` 或 `/MERGETASKS` 由 Inno 处理；不沿用过时的安装历史，也不修改 Windows 启动许可。

`.github/workflows/verify.yml` 在 Windows runner 上运行六个代码测试项目、离线发布工具检查和 Release 构建。工作流只读仓库，仅上传测试日志及结果；不运行硬件、安装器或发布操作。本机可运行相同的 `ci-verify.ps1`。远端运行是否通过，以对应提交的 Actions 结果为准。

## 真实安装升级验收

真实验收共用当前用户的安装登记和配置；即使安装目录在项目内，也必须先备份现有配置、Run/StartupApproved 状态、卸载登记及开始菜单快捷方式。以用户实际使用的便携目录为迁移来源，不能从开发预览目录猜测最新配置。所有结果集中保存到本轮 `.artifacts/evidence/<阶段>`。

普通安装版带 `--test-pipe <唯一后缀>` 启动后，可通过 `app-test-client.ps1` 打开设置，并调用 `ui / management-import-directory / value=<便携目录>`。该入口沿用正式导入、无覆盖与保存屏障；它使用真实存储，不能当作演示模式使用。

端到端更新验收可额外指定 `--update-fixture <绝对 JSON 路径>`。该模式仅替换 HTTP 响应：文件内 URL 与请求精确匹配，未匹配直接失败，不回退网络。真实版本比较、manifest、长度与 SHA256 校验、下载、保存排空、原生 I/O 排空及 Inno 进程交接继续执行。界面和日志明确标记本地验收；不能与 `--scenario` 合用。安装时仅该模式附加固定静默参数，安装日志留在 fixture 目录。

fixture 的 `schemaVersion` 为 1，`purpose` 为 `local-update-acceptance`；`responses` 每项明确填写 `url`、`statusCode`、`bodyFile`、`location`、`delayMilliseconds`。`bodyFile` 只能引用同目录内的相对文件；没有内容或跳转时对应字段为 null。GitHub release JSON、更新 manifest 与安装包必须使用同一个真实版本，不得将旧程序改名充当新版。候选可以只在本地存在，不应为了验收发布虚构线上版本。

按“旧版启动及导入 → 发现新版 → 下载校验 → App 排空退出 → 安装器等待旧进程结束 → 新版启动与配置核对 → 回退旧版并核对 → 恢复候选”的顺序验收。真实登录自启需另由用户注销并登录；本地 HTTP fixture 验收不能代替 GitHub 实际发布与下载验证。
