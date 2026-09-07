# Codex Island for Windows

Windows 原生额度灵动岛，使用 C# / WPF 和 .NET 10。默认在主显示器工作区顶部居中，复用本机 Codex 的登录状态。保留 macOS 版的额度规则和主要交互。

## 0.3.1 更新

- 鼠标移入立即开始展开，无需停留等待；展开动画缩短为 180 毫秒。离开仍保留 280 毫秒缓冲，手动收起后需移出再移入。
- 额度选择菜单采用与面板一致的深色圆角、薄荷色选中状态和字体，并随灵动岛大小一起缩放。
- 统一标题、额度数字、辅助文字的字号、字重与行高，增大中文说明文字，使用等宽数字稳定百分比的排列。

## 运行

解压 Windows 发布包，双击 `CodexIsland.exe`。默认读取真实额度。标准 `win-x64` 发布包自带 .NET 运行时，使用者不需要安装 SDK 或运行时。

- 鼠标移入立即展开，离开约 280 毫秒后收起；点击顶部固定，再次点击收起。
- 展开后可切换额度种类、手动刷新、固定、收起和调整位置。
- “调整位置”模式下，按住文字区域拖动。点“完成”保存，点 × 取消。
- 任务栏右侧通知区域有柱状图图标。左键隐藏/显示；右键可刷新、调大小、调整位置、选择 Codex 程序或退出。图标也可能位于通知区域的折叠菜单中。
- 每个显示器可以独立选择自动、100%、125%、150%、175% 或 200% 大小。Windows 系统 DPI 缩放仍正常生效。

设置保存在 `%LOCALAPPDATA%\CodexIsland\settings.json`，包括位置、各显示器大小和可选的 Codex 路径。位置调整取消时不写入草稿；屏幕断开时保留原屏幕的设置。默认不添加开机启动。

## Codex 连接

需要在 **Windows 本机** 安装并使用 ChatGPT 账号登录 Codex。应用会查找桌面应用提取的 CLI、用户本地安装目录、PATH 及常见 npm 安装目录中的原生 `codex.exe`。兼容新旧 npm vendor 目录，直接启动 exe，不通过 `codex.cmd` 或 PowerShell 包装器。

若没有找到，可用托盘菜单“选择 Codex 程序…”指定 `codex.exe`，或设置 `CODEX_ISLAND_CODEX_PATH`。环境变量在下一次启动时生效。命令行 `--codex-path` 可为当前启动指定路径。“自动查找 Codex”清除持久化的自选路径；环境变量仍有效。

仅在 WSL 中安装/登录 Codex，不代表 Windows 侧也已登录；本版不启动 WSL，也不复制认证文件。自定义 `CODEX_HOME` 会传给子进程，额度来自该进程实际使用的账号。

应用启动独立的 `codex app-server --listen stdio://` 子进程，通过 `initialize` → `initialized` → `account/rateLimits/read` 读取额度。登录由 Codex 管理，本应用不读取密钥或聊天记录，不创建任务、不申请额度重置。子进程会正常打开 Codex 自己的本地状态文件。

百分比表示**剩余**；单周期、双周期与额外额度分别显示。≤25% 为橙色，≤10% 为红色。未知数值显示横线。恢复时间到了也不自行填满额度。每 30 秒轮询；连续失败按 60、120、240、300 秒退避，并保留上次成功数据。手动刷新和展开刷新可提前触发读取。上游更新通知仅用于提示重新读取。

## 从源码构建

要求 Windows 和 .NET 10 SDK。可选的 `setup-windows.ps1` 将 SDK 放到仓库 `.build/dotnet`，不修改系统 PATH，无需管理员权限。此目录已被 Git 忽略。

在仓库根目录的 PowerShell 运行：

```powershell
# 已安装 .NET 10 SDK 时可跳过第一行。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-windows.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-windows.ps1 -IncludeUI -LiveQuota
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-windows.ps1
```

输出：`dist/windows/win-x64/CodexIsland.exe` 和 `dist/Codex-Island-0.3.1-win-x64.zip`，附 ZIP 的 SHA-256 文件。包名版本自动读取 `windows/Directory.Build.props`。已运行的发布程序需先从托盘退出，再覆盖构建。

`build-windows.ps1 -Runtime win-arm64` 可构建 ARM64 包，尚未在 ARM64 实机验证。`-FrameworkDependent` 生成体积更小的包，运行机器需要另行安装 .NET 10 Desktop Runtime。主解决方案为 `windows/CodexIsland.slnx`。

## 诊断

在发布程序目录的 PowerShell 运行，管道可以等待 GUI 可执行程序退出并显示输出：

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
.\CodexIsland.exe --check-quota | Out-String
.\CodexIsland.exe --diagnose-screen | Out-String
.\CodexIsland.exe --demo --expanded
```

`--check-quota` 只输出额度类别、周期、剩余百分比和获取时间。`--diagnose-screen` 只输出屏幕几何与 DPI。`--demo` 才会使用演示数据，并在界面标注。重复启动会显示已运行的窗口。

开发用 `--smoke-test <输出目录>` 运行隔离的演示 UI 检查、保存 PNG 后退出；需要可交互的 Windows 桌面。它不会写真实用户设置，不会连接 Codex。普通 `test-windows.ps1` 的逻辑/子进程测试也不联网或读取真实账号；`-LiveQuota` 才查询真实额度。

## 已验证和边界

2026-09-07，在 Windows 11 x64、1920×1080、系统 100% 缩放的单屏环境验证：40 项模型、交互、位置、设置、刷新与真实管道测试；23 项 WPF 窗口检查；本机真实额度读取。检查中包含即时悬停展开、菜单切换和关闭、应用内 150% 菜单缩放、未知/缺失数据、分段 UTF-8、超时、断线重连、子进程回收、原生关闭事件，以及窗口不抢前台焦点。收起、展开、额度菜单和调整模式的 PNG 已目视检查。

还通过运行中的正式窗口确认了真实额度展示、后台更新时间推进、额度种类切换、重复启动保持单实例，以及正常退出后其 Codex 子进程已回收。

混合 DPI、多屏热插拔、远程桌面、休眠唤醒和 ARM64 仍需对应设备验收；自动化几何检查不代表这些实机组合已验证。窗口在当前 Windows 虚拟桌面上置顶，不承诺覆盖独占全屏程序或安全桌面。发布包尚未配置代码签名、安装器或自动更新。

协议依据：[OpenAI Codex App Server](https://learn.chatgpt.com/docs/app-server)。窗口依据：[Microsoft WPF 透明窗口](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency?view=windowsdesktop-10.0)。运行时采用 [.NET 10 LTS](https://dotnet.microsoft.com/en-us/platform/support/policy)。

排版参考：[Vercel Geist Typography](https://vercel.com/geist/typography) 的文字层级与等宽数字、[Microsoft Fluent Typography](https://fluent2.microsoft.design/typography) 的字号与行高体系；实际字体使用 Windows 自带的 Segoe UI 和 Microsoft YaHei UI。
