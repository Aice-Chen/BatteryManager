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

## 安装与更新基础

`packaging/windows/BatteryManager.iss` 是当前用户安装草稿，允许选择安装目录，升级复用原目录。`installed.json` 标识安装版；安装版用户数据使用 LocalApplicationData 下的 BatteryManager，便携版仍使用程序目录。安装器不强杀采集进程，卸载脚本不删除独立用户数据目录。

编译入口为 `scripts/package-installer.ps1`，接收通过校验的便携包和项目内已有的可信编译器。编译器获取脚本曾被本机安全软件拦截，已停止并移除自动获取路径；当前没有可用编译器，安装器尚未编译或验收。

`scripts/prepare-release.ps1` 校验安装包与便携包版本、来源指纹和摘要一致，再生成独立交付目录与 `update-manifest.json`。当前仅准备本地材料，不执行上传。

更新客户端仅发现公开仓库的正式发布，忽略草稿和预览。先核对仓库、标签、版本、架构、文件名及大小，再下载到临时文件；实际长度与 SHA256 全部一致才保留完成文件。取消和失败不替换已有完成文件。HTTPS 与仓库发布权限构成当前来源信任边界，摘要不能代替发布者独立数字签名。

当前更新客户端仍是底层模块。自动检查频率、设置页交互、用户确认、正常退出后的安装交接、安装与回退的端到端检查尚未完成，不能把基础测试等同于完整升级验收。
