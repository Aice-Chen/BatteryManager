# Battery Manager

Windows 11 x64 外设电量与状态管理程序，使用 C# / .NET 10 / WinUI 3。

## 当前版本

当前源码为 `0.2.1`，统一提供 Windows 11 x64 安装版，原 `0.1.0` 便携版停止维护。本次修复设备关联后同一蓝牙设备的电量来源反复切换；变化见 [0.2.1 发布说明](docs/releases/0.2.1/发布说明.md)。

- 主窗口、托盘数字和快捷面板。
- Windows 蓝牙电量、标准蓝牙电池服务。
- 已验证的 ATK A9 Ultimate、Lofree Flow Lite100、迈从 G3 A 接收器，以及罗技 K380 蓝牙读取。
- 托盘优先级、名称与类型设置、主题、手动关联同一设备的不同连接。
- 电量旧值、断开重连、系统睡眠恢复及首次活跃准入。
- 当前用户安装包、登录自启设置、正式版本检查与校验下载。

完整支持范围、显示规则与已知边界见 [0.1.0 发布说明](docs/releases/0.1.0/发布说明.md)。私有协议仅用于已验证的型号与接口。

## 项目结构

| 目录 | 职责 |
|---|---|
| `src/BatteryManager.Core` | 公共数据、连接与电量规则、扫描调度、显示投影 |
| `src/BatteryManager.Application` | 设置与存储、自启规则、安装数据目录、更新检查与下载协调 |
| `src/BatteryManager.WindowsNative` | Windows / 蓝牙 / HID 通信、设备协议与读取器 |
| `src/BatteryManager.App` | WinUI 主窗口、托盘与应用生命周期 |
| `src/BatteryManager.Cli` | 诊断入口，复用正式采集与状态链路 |
| `tests` | 自动化回归测试 |
| `scripts` | 可重复的构建、测试、打包与验收入口 |
| `tools` | 持续维护的开发工具 |
| `docs/releases` | 按版本保存的发布说明 |
| `.tools` | 本地 SDK 等工具，不提交 Git |
| `.artifacts` | 构建、验收、发布与本地证据，不提交 Git |

## 开发与验证

在 Windows PowerShell 中执行：

```powershell
./scripts/bootstrap.ps1
./scripts/verify.ps1
./scripts/publish-preview.ps1
./scripts/run-preview.ps1
```

版本统一在 `version.props` 中维护。`scripts/package-preview.ps1` 和正式入口 `scripts/package-release.ps1` 生成内部构建负载；再由 `scripts/package-installer.ps1` 生成用户使用的安装器。内部 ZIP 不作为便携版发布。

包从干净输出目录构建，包含来源指纹、文件摘要及依赖许可。个人设置、日志和硬件证据不进入发行包。

测试优先使用可替换的硬件通信接口。应用的演示场景只替换硬件 I/O，继续经过正式读取器、状态规则和控件，配置仅保存在内存。

开发约定、安装验收与版本交付命令见 [开发维护说明](docs/development.md)。安装框架的既有验收范围见 [0.2.0 验收记录](docs/releases/0.2.0/验收记录.md)。

配置统一保存在当前用户的 LocalAppData/BatteryManager 目录，升级或更换安装位置沿用现有配置。程序不提供旧便携配置导入功能。自动检查每天最多一次，仅发现 GitHub 正式发布；下载与安装仍由用户操作，更新统一使用安装器。

## 许可状态

项目源码尚未选定开源许可证。字体与依赖组件的许可按各自条款保留；发行包包含对应版本的许可与声明。
