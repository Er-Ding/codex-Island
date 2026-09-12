# Windows 开发说明

日常使用请先看 [Windows 使用指南](../../README.md)。本文用于从源码构建、测试和诊断。

## 目录

```text
windows/
├── CodexIsland.Core/       # 额度、任务流、交互、位置和设置逻辑
├── CodexIsland.Windows/    # 桌面窗口和托盘
├── CodexIsland.Checks/     # 核心与进程通信检查
├── CodexIsland.slnx
├── Directory.Build.props
├── scripts/               # PowerShell 构建、测试及环境准备
├── docs/
├── assets/                # 使用指南中的演示截图
├── .build/                # 本地 SDK、诊断输出；不入 Git
└── dist/                  # 可执行程序和发布包；不入 Git
```

## 构建

需要 Windows 和 .NET 10 SDK。在仓库根目录的 PowerShell 运行：

```powershell
# 已安装 .NET 10 SDK 时，可跳过这一行。
powershell -NoProfile -ExecutionPolicy Bypass -File windows/scripts/setup-windows.ps1

powershell -NoProfile -ExecutionPolicy Bypass -File windows/scripts/test-windows.ps1 -IncludeUI
powershell -NoProfile -ExecutionPolicy Bypass -File windows/scripts/build-windows.ps1
```

安装脚本将 SDK 放到 `windows/.build/dotnet`，不修改系统 PATH。脚本根据自身位置定位项目，从其他工作目录用完整路径调用也可运行。

输出位于：

- `windows/dist/win-x64/CodexIsland.exe`
- `windows/dist/Codex-Island-<版本>-win-x64.zip`
- 同名 ZIP 的 `.sha256` 校验文件

版本自动读取 `windows/Directory.Build.props`。如果发布目录中的程序正在运行，需要先从托盘退出，才能覆盖构建。

标准发布包包含 .NET 运行时。`build-windows.ps1 -FrameworkDependent` 可生成需要 .NET 10 Desktop Runtime 的包。`-Runtime win-arm64` 可构建 ARM64 包，但尚未经过 ARM64 实机验证。

## 测试与诊断

默认测试不读取真实账号；`-IncludeUI` 使用隔离的演示设置并保存 PNG，需要交互式 Windows 桌面。可额外用 `-LiveQuota` 验证本机账号连接：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows/scripts/test-windows.ps1 -IncludeUI -LiveQuota
```

在发布程序目录运行以下诊断命令。管道用于等待 GUI 可执行程序退出并显示结果：

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
.\CodexIsland.exe --check-quota | Out-String
.\CodexIsland.exe --check-tasks | Out-String
.\CodexIsland.exe --diagnose-screen | Out-String
.\CodexIsland.exe --demo --expanded
```

`--check-quota` 只输出额度类别、周期、剩余比例和时间；`--check-tasks` 只输出 Desktop 连接状态、任务数量与状态统计，不输出任务标题、提问或会话内容；`--diagnose-screen` 输出屏幕几何与 DPI。`--demo` 使用明确标注的额度和任务演示数据，不执行真实会话导航。`--smoke-test <输出目录>` 运行隔离的界面检查后退出，不改用户设置。

应用可通过托盘、`--codex-path` 或 `CODEX_ISLAND_CODEX_PATH` 指定原生 `codex.exe`。继承的 `CODEX_HOME` 会传给 Codex 子进程。本工具不启动 WSL、不复制认证文件；数据来自该 Windows 进程实际登录的账号。

## 平台分支

- `Windows` 分支维护 `windows/`。当前 macOS 新功能在远端 `main` 的 `Sources/CodexIsland/`。
- 根目录保留使用入口、许可和 Git 公共配置；平台文档、脚本和产物放在平台目录内。
- 本地 `macOS` 分支的 `macos/` 是早期隔离快照；截至 2026-09-12，远端没有同名分支。本次不切换分支、不重写历史。
- 平台功能从对应分支创建功能分支，完成后合回同一平台。不要将两个平台分支直接互相合并；需要共享修复时，选择相关提交并在目标平台验证。

获取 Windows 分支：

```bash
git fetch origin
git switch --track origin/Windows
```

需要对照最新 macOS 源码时，可使用独立工作目录，基于刷新后的 `origin/main` 创建本地开发分支：

```bash
git worktree add -b macos-latest ../codex-island-macos origin/main
```

新建本地分支不会自动在 GitHub 发布，需另行推送。平台切换时，Git 不会清除 `.build/`、`dist/` 等被忽略的本地产物。

## 验证范围

2026-09-12 的 Windows 0.4.1 修复启动前任务漏计，已通过 99 项核心／任务管道检查和 75 项界面／导航保护检查。新增回归覆盖无会话索引的既有任务、远端和后台子任务、首次订阅无回应、断线重连、1300 个历史候选的公平轮换，以及冷却期间实时通知。实机顺序冷启动诊断中，旧版发现 2 个运行任务，新版发现 3 个；验证过程中没有重新开始 Codex 任务。

2026-09-12 的 Windows 0.4.0 本地构建已通过 85 项核心／任务管道检查、75 项界面／导航保护检查及 34 项真实鼠标输入检查，编译无警告或错误。本机 Desktop 的真实任务流连接和真实额度读取通过。导航检查覆盖无效目标、跨设备歧义、预取消、元数据边界和页签精确匹配；未逐一点击真实会话验收各来源跳转。完整范围与限制见 [更新日志](../../CHANGELOG.md)。

历史版本 0.3.1 在 Windows 11 x64 单屏环境通过 40 项核心检查和 23 项界面检查，并验证真实额度读取。界面检查包括即时展开、额度切换、菜单关闭、150% 大小、位置保存、单实例相关窗口行为和正常退出。

0.3.3 支持顶部即刻拖动和双击复位：按住左键后首次移动就拖动，松开自动保存；原地单击固定 / 取消固定并保持展开，双击回主屏初始位置并收起。面板不再显示“调整位置”按钮，托盘调整模式继续保留。已通过 51 项核心检查、46 项界面检查及 28 项原生鼠标输入检查，覆盖首像素跟随、快速移动、松开补采样、取消、捕获释放、保存与恢复、屏幕边界、缩放保留和双击与拖动的优先级。原生检查通过实际鼠标事件验证进入展开、按下后 20 毫秒移动 1 像素即拖动、跳出原窗口范围、松开保存，以及固定 / 未固定状态下从标题侧边双击复位并保持前台焦点。

原生输入检查只在 `--smoke-test` 下显式设置 `CODEX_ISLAND_NATIVE_INPUT_TEST=1` 时启用，会暂时移动鼠标并在结束时恢复；使用演示数据和隔离设置。普通测试不会发送鼠标输入。需要复验时，在交互式桌面的 PowerShell 中运行：

```powershell
$env:CODEX_ISLAND_NATIVE_INPUT_TEST = '1'
try {
    powershell -NoProfile -ExecutionPolicy Bypass -File windows/scripts/test-windows.ps1 -IncludeUI
} finally {
    Remove-Item Env:CODEX_ISLAND_NATIVE_INPUT_TEST
}
```

混合 DPI、多屏热插拔、远程桌面、休眠唤醒和 ARM64 仍需对应设备验收。当前没有安装器、代码签名或自动更新功能。

详细实现见 [架构记录](ARCHITECTURE.md)。
