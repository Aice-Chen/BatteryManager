# Battery Manager

Windows 11 x64 外设电量与状态管理程序，使用 C# / .NET 10 / WinUI 3。

## 当前版本

当前源码版本为 `0.3.1`，统一提供 Windows 11 x64 安装版，原 `0.1.0` 便携版停止维护。本版改进托盘面板、登录与自动更新后的启动方式，以及设置页的更新操作，见 [0.3.1 发布说明](docs/releases/0.3.1/发布说明.md)。自动更新与卸载清理规则见 [0.3.0 发布说明](docs/releases/0.3.0/发布说明.md)。已公开版本以 [GitHub Releases](https://github.com/Aice-Chen/BatteryManager/releases/latest) 为准。

- 主窗口、托盘数字和快捷面板。
- Windows 蓝牙电量、标准蓝牙电池服务。
- 已验证的 ATK A9 Ultimate、Lofree Flow Lite100、迈从 G3 A 接收器，以及罗技 K380 蓝牙读取。
- 托盘优先级、名称与类型设置、主题、手动关联同一设备的不同连接。
- 电量旧值、断开重连、系统睡眠恢复及首次活跃准入。
- 当前用户安装包、登录自启设置、正式版本校验下载与全自动更新。

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

配置统一保存在当前用户的 LocalAppData/BatteryManager 目录，升级或更换安装位置沿用现有配置。程序不提供旧便携配置导入功能。新卸载流程删除个人设置、备份、日志、更新缓存和本安装对应的启动项；用户自行放入安装目录的其他文件保留。

“自动更新”默认开启，原有关闭设置继续保留；启动 30 秒后判断检查资格，之后每小时判断一次，两次自动尝试至少间隔 24 小时。发现 GitHub 正式新版后自动下载、校验、正常退出、静默安装并重新启动，保留个人设置和 Windows 启动许可。可在设置中关闭，也可继续手动检查、下载和安装。只有安装版启用自动安装；开发构建不自动安装。升级按原文件清单清理已不再使用的程序文件，新版启动后清理旧安装包缓存。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。后续构建的安装包将附带 `LICENSE.txt`，保留项目版权声明和许可全文。

字体与第三方依赖继续适用各自的许可证；发行包的 `licenses` 目录保留对应版本的许可与声明。
