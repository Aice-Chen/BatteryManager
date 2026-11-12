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
| `.artifacts/packages` | 安装器内部负载 ZIP 及其干净构建目录，不对外提供便携版 |
| `.artifacts/installers` | 安装器构建及编译日志 |
| `.artifacts/releases` | 已组装的版本交付目录 |
| `.artifacts/evidence/<版本或阶段>` | 验收摘要与证据入口 |
| `.artifacts/hardware` | 原始硬件证据 |
| `.artifacts/archive` | 已归档历史证据 |
| `docs/releases/<版本>` | 发布说明与变更记录 |

一次性调查结果留在对应输出目录；需要长期复用的命令才进入 scripts 或 tools。已有正式交付目录不覆盖；新的候选交付用独立目录。清理按明确目录和验证后的边界执行，保留当前发行物及必要回退版本。

## 安装与更新

`packaging/windows/BatteryManager.iss` 构建当前用户安装器，允许选择安装目录，升级复用原目录。产品统一以安装版交付；`installed.json` 标识安装版，用户数据使用 LocalApplicationData 下的 BatteryManager。未带标记的开发构建继续使用自身输出目录，避免测试读写日常配置。卸载删除个人设置、备份、日志和下载缓存，同时清理本安装对应的启动登记与 Windows 启动状态记录；升级保留个人设置和系统启动许可。安装目录内用户自行添加的文件保留，不对整个安装目录使用递归通配删除；不提供旧便携配置导入功能。

升级按旧负载的 SHA256 清单识别新版不再使用的文件，验证相对路径、内容和目录边界后清理，并移除因此变空的子目录。清单无效、目录包含重解析点或文件无法安全清理时报告失败，避免删除未知文件。卸载前要求程序正常退出；旧目录的卸载器不得删除当前另一安装位置的共用数据和登记。

安装器在写入前检查现有目标文件是否可替换，旧文件清理放在新负载写入完成之后。中断记录用于下次重试；未写完整的临时记录保存原字节后重建，不执行其中未验证的路径。自动更新的安装前检查失败且原程序已正常退出时，重新启动未被改写的原程序。开始写入后发生的磁盘或提取故障不具备完整事务回滚保证，需要重新运行安装包修复；不能把锁定预检和清理重试测试表述为任意故障自动回退。

编译入口为 `scripts/package-installer.ps1`，接收通过校验的内部负载 ZIP 和项目内已有的可信 Inno Setup 编译器，不负责下载或安装工具。既有 `PortablePackagePath` 参数与 `portable-common.ps1` 名称暂保留为内部接口，不表示继续发布便携版。本地验证使用 7.1.1；从官方发行取得的工具先核对发布摘要及 Authenticode，再放入 `.tools/inno-7.1.1`。编译器的有效签名不等于本项目生成的安装包已签名。

`scripts/prepare-release.ps1` 校验安装包与内部负载的版本、来源指纹和摘要一致，再生成独立交付目录与仅含安装器的 `update-manifest.json`。内部 ZIP 留作发布前源码绑定校验，不上传；旧双包清单不得进入新的发布入口。

更新客户端仅发现公开仓库的正式发布，忽略草稿和预览。先核对仓库、标签、版本、架构、文件名及大小，再下载到临时文件；实际长度与 SHA256 全部一致才保留完成文件。取消和失败不替换已有完成文件。HTTPS 与仓库发布权限构成当前来源信任边界，摘要不能代替发布者独立数字签名。

`UpdateCoordinator` 串行协调检查、下载、取消、安装提交和设置保存，AppManagementController 管理 UI 与定时入口。安装版启动 30 秒后按检查资格执行，随后每小时判断一次；两次自动尝试至少间隔 24 小时，失败也计入间隔。当前进程使用单调时间，跨重启用保存的 UTC 时间。自动更新默认开启，可在设置关闭；沿用原有配置键，既有关闭状态保持。自动周期会检查、下载和安装正式新版，托盘隐藏或不在设置页时也可运行；手动检查仍只检查，手动下载仍由用户决定安装。未带安装标记的开发运行、硬件场景及本地 HTTP fixture 不自动启动该周期。

安装前冻结设备设置编辑，等待已接受的保存并重新校验安装文件，持有只读句柄直到启动安装器。提交安装之前，自动更新开关及取消控件保持可操作；关闭会取消自动下载或准备流程。自动安装使用 `/AUTOUPDATE=1 /WAITTOKEN=<32位随机令牌> /WAITPID /DIR`，安装器核实进程与目录后发 Accepted，App 完成配置及原生 I/O 排空后发 Ready；此时才开始 30 秒退出等待，不强杀进程。Ready 前取消或退出不执行安装；成功安装和清理后启动一次。普通手动安装仍使用原有退出后交接。

安装版启动时按快照清理旧安装器、内部旧 ZIP 与遗留 GUID.partial 文件，正在退出的安装器文件锁采用有限重试；未来版本和未知文件保留，目录或祖先的重解析点拒绝跟随。清理失败记录剩余路径，下次启动再尝试；不把仍被占用的文件报告为删除成功。更新保留配置，卸载才移除整个应用数据目录。

自启使用当前用户 Run 的 BatteryManager 项，写入完整且带引号的程序路径，后接 `--background`。带此参数启动时只在托盘运行，不打开主窗口；15 秒后托盘图标仍未登记成功则打开主窗口。自动更新安装完成后同样以此参数重新启动。0.3.0 写入的无参数命令仍视为本程序的启动项，安装器升级或用户在程序中重新开启时改写为新命令。运行及升级期间 StartupApproved 仅读取，卸载清理本安装拥有的同名记录；Windows 禁用或未知状态单独显示，不绕过系统设置。用户在程序中明确开启时才修复已识别的旧路径；不覆盖同名但无法识别的命令。

## 版本交付顺序

1. 修改 `version.props`，在 `docs/releases/<版本>` 写使用者能看到的变更和已知限制。
2. 运行代码与受影响的应用场景。安装/更新改动额外运行隔离生命周期及进程交接测试。
3. 提交已审核的源代码与版本号，在干净工作树使用 `package-preview.ps1` 或正式入口 `package-release.ps1` 构建内部负载 ZIP；修改任何源输入后重新构建。打包前后必须保持同一提交和输入文件。
4. 把该 ZIP 交给 `package-installer.ps1 -PortablePackagePath <路径>`，确保安装器与内部负载来源一致。
5. 使用 `prepare-release.ps1` 生成独立交付目录；预览必须带 `-Prerelease`。本地的 `installer-build.json`、`delivery.json` 含构建路径，不作为公开附件。
6. 先运行 `publish-github-release.ps1 -DeliveryDirectory <目录> -TargetCommit <40位提交> -PlanOnly` 审核四个公开附件及目标提交：安装器、安装器摘要、`update-manifest.json`、`RELEASE_NOTES.md`。去掉 `-PlanOnly` 才调用 GitHub CLI，且只创建草稿，拒绝覆盖已有标签或发布。正式发布标签使用 `v<版本>`；正式更新客户端忽略预览与草稿。发布后本地保留内部 ZIP、安装器、摘要、manifest 和验证入口。

新包同时记录实际构建字节的 `source-files.sha256` 和 Git 规范字节的 `source-git-files.sha256`。发布校验逐项比较文件集合、摘要与指定的完整提交，只容许明确限定的文本换行转换；额外、缺失或不同内容的源码不能通过。旧包仍可做完整性检查，但缺少提交绑定信息的旧包不能走新的发布入口。`test-github-release-tooling.ps1` 使用本地 CLI 替身验证失败、覆盖保护和白名单，不访问 GitHub。

应用管理交互测试入口为 `test-app-management.ps1 -PreviewExe <项目内隔离程序>`，替换网络、注册表、硬件及安装动作，仍运行正式协调和控件。`test-installer-lifecycle.ps1` 与 `test-installer-handoff.ps1` 先生成计划，复核后对该计划传 `-Execute -PreparedDirectory <目录>`；使用独立 AppId/互斥体、项目内固定安装与数据目录，以及假负载或最小进程组件。需要验证登记时只写随机隔离注册表子树；自动重启测试只启动受路径限制的测试组件，不能启动日常程序。测试版安装器不能进入发布链。

`test-installer-startup.ps1` 验证真实 Inno 任务选择与登记逻辑，测试键限定到随机的当前用户隔离子树，正式 Run/StartupApproved/卸载登记仅作前后只读核对。安装默认值来自当时实际的 Run 注册，显式 `/TASKS` 或 `/MERGETASKS` 由 Inno 处理；不沿用过时的安装历史，也不修改 Windows 启动许可。

`.github/workflows/verify.yml` 在 Windows runner 上运行六个代码测试项目、离线发布工具检查和 Release 构建。工作流只读仓库，仅上传测试日志及结果；不运行硬件、安装器或发布操作。本机可运行相同的 `ci-verify.ps1`。远端运行是否通过，以对应提交的 Actions 结果为准。

## 真实安装升级验收

真实验收共用当前用户的安装登记和配置；即使安装目录在项目内，也必须先备份现有配置、Run/StartupApproved 状态、卸载登记及开始菜单快捷方式。迁移安装位置保留现有 LocalAppData 配置，不从其他程序目录导入。所有结果集中保存到本轮 `.artifacts/evidence/<阶段>`。更换安装目录须先正常退出后安装，不能沿用要求路径一致的 `/WAITPID` 原地升级交接；新目录注册完成后不可运行旧目录的卸载器去删除共用登记。

安装验收同时检查程序实际运行路径、快捷方式、卸载登记、Run 与 Windows 启动许可。工具进程回读注册表不足以单独证明系统已接受登记，必要时与独立系统提供者及 Windows 设置交叉核对。

端到端更新验收可额外指定 `--update-fixture <绝对 JSON 路径>`。该模式仅替换 HTTP 响应：文件内 URL 与请求精确匹配，未匹配直接失败，不回退网络。真实版本比较、manifest、长度与 SHA256 校验、下载、保存排空、原生 I/O 排空及 Inno 进程交接继续执行。界面和日志明确标记本地验收；不能与 `--scenario` 合用。安装时仅该模式附加固定静默参数，安装日志留在 fixture 目录。

fixture 的 `schemaVersion` 为 1，`purpose` 为 `local-update-acceptance`；`responses` 每项明确填写 `url`、`statusCode`、`bodyFile`、`location`、`delayMilliseconds`。`bodyFile` 只能引用同目录内的相对文件；没有内容或跳转时对应字段为 null。GitHub release JSON、更新 manifest 与安装包必须使用同一个真实版本，不得将旧程序改名充当新版。候选可以只在本地存在，不应为了验收发布虚构线上版本。

按“旧安装版启动 → 发现新版 → 下载校验 → App 排空退出 → 安装器等待旧进程结束 → 新版启动与配置核对 → 回退旧版并核对 → 恢复候选”的顺序验收。真实登录自启需另由用户注销并登录；本地 HTTP fixture 验收不能代替 GitHub 实际发布与下载验证。
