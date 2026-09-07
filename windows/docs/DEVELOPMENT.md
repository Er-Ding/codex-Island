# Windows 开发说明

日常使用请先看 [Windows 使用指南](../../README.md)。本文用于从源码构建、测试和诊断。

## 目录

```text
windows/
├── CodexIsland.Core/       # 额度、交互、位置和设置逻辑
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
.\CodexIsland.exe --diagnose-screen | Out-String
.\CodexIsland.exe --demo --expanded
```

`--check-quota` 只输出额度类别、周期、剩余比例和时间；`--diagnose-screen` 输出屏幕几何与 DPI。`--demo` 使用明确标注的演示数据。`--smoke-test <输出目录>` 运行隔离的界面检查后退出，不改用户设置。

应用可通过托盘、`--codex-path` 或 `CODEX_ISLAND_CODEX_PATH` 指定原生 `codex.exe`。继承的 `CODEX_HOME` 会传给 Codex 子进程。本工具不启动 WSL、不复制认证文件；数据来自该 Windows 进程实际登录的账号。

## 平台分支

- `Windows` 分支只维护 `windows/`；`macOS` 分支只维护 `macos/`。
- 根目录保留使用入口、许可和 Git 公共配置；平台文档、脚本和产物放在平台目录内。
- `main` 暂保留原有历史基线。本次整理不修改默认分支，不重写历史。
- 平台功能从对应分支创建功能分支，完成后合回同一平台。不要将两个平台分支直接互相合并；需要共享修复时，选择相关提交并在目标平台验证。

当前仓库已有本地平台分支时，使用 `git switch Windows` / `git switch macOS`。在远程已经发布相应分支、但新克隆尚无本地分支时：

```bash
git fetch origin
git switch --track origin/Windows
# macOS 开发机使用：git switch --track origin/macOS
```

需要同时开发两端时，推荐独立工作目录。例如当前已在 Windows 分支，可在 macOS 分支存在且未被其他工作区检出的情况下运行：

```bash
git worktree add ../codex-island-macos macOS
```

新建本地分支不会自动在 GitHub 发布，需另行推送。平台切换时，Git 不会清除 `.build/`、`dist/` 等被忽略的本地产物。

## 验证范围

0.3.1 在 Windows 11 x64 单屏环境通过 40 项核心检查和 23 项界面检查，并验证真实额度读取。界面检查包括即时展开、额度切换、菜单关闭、150% 大小、位置保存、单实例相关窗口行为和正常退出。

混合 DPI、多屏热插拔、远程桌面、休眠唤醒和 ARM64 仍需对应设备验收。当前没有安装器、代码签名或自动更新功能。

详细实现见 [架构记录](ARCHITECTURE.md)。
