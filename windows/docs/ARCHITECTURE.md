# Windows 适配：逻辑与实现

## 原应用的数据流

基于仓库 macOS 提交 `0043137` 梳理。`IslandApp` 创建状态仓库、窗口和菜单栏图标；`QuotaStore` 控制刷新；`QuotaClient` 管理独立的 Codex 子进程；`QuotaModels` 解析额度；`IslandView` 展示当前额度类别。`IslandPanel` 是窗口、动画、屏幕环境和交互状态的协调者。

```mermaid
flowchart LR
    A[托盘 / 窗口操作] --> B[QuotaStore 刷新调度]
    B --> C[QuotaClient 子进程通信]
    C --> D[本机 Codex app-server]
    D --> E[QuotaParser 解析额度]
    E --> B
    B --> F[WPF 灵动岛]
    G[悬停 / 固定] --> H[IslandInteraction]
    H --> F
    K[顶部按住移动 / 双击复位] --> L[HeaderDragGesture]
    L --> F
    I[显示器 / DPI / 已保存位置] --> J[IslandPlacement]
    J --> F
```

不是从日志累计 token 推算额度。采用官方账号额度接口，由 Codex 自身管理认证与服务访问。多额度优先取 `rateLimitsByLimitId`，旧 `rateLimits` 只补缺失项；已用值转成剩余值后限制在 0–100。周期与恢复时间保留服务实际返回的含义。

## 为什么单独增加 WPF 实现

现有项目依赖 SwiftUI、AppKit、CoreGraphics 和 Darwin，界面与 macOS 窗口代码不能直接编译到 Windows。这个工具的核心需求是一个小型透明、置顶、不抢焦点的原生窗口。WPF 能直接承担这些职责，Win32 补充窗口风格、物理屏幕坐标和显示器 DPI，WinForms 仅用于系统托盘。项目不依赖额外 NuGet 包。

macOS 的 Swift 源码和构建脚本在 `macOS` 分支的 `macos/` 目录中维护；Windows 在 `Windows` 分支的 `windows/` 目录中维护。业务规则采用等价的 C# 实现，通过测试保持一致，当前不建立 Swift/C# 跨语言运行时共享。未来若统一两个平台，应先共享协议样本与行为测试，再决定是否重构桌面框架。

## 模块对应

| macOS 文件 | Windows 文件 | 适配内容 |
| --- | --- | --- |
| `QuotaModels.swift` | `Core/QuotaModels.cs` | 相同的额度优先级、周期、剩余值与错误文案策略 |
| `QuotaClient.swift` | `Core/QuotaClient.cs`、`CodexLocator.cs`、`JsonLineBuffer.cs` | Windows exe 发现、隐藏子进程、UTF-8 行协议、请求关联、超时、重连 |
| `QuotaStore.swift` | `Core/QuotaStore.cs` | 合并同时刷新、30 秒调度、失败退避、保留旧快照、取消 |
| `IslandState`、`HoverTracking.swift` | `Core/IslandInteraction.cs` | 进入立即展开、离开延迟、手动收起抑制、固定、菜单和调整模式 |
| Windows 专属手势 | `Core/HeaderDragGesture.cs`、`Windows/IslandWindow.HeaderDrag.cs` | 顶部按住即拖、单击固定、双击复位、鼠标捕获和松开保存 |
| `IslandPlacement.swift`、`IslandSizing.swift` | `Core/IslandGeometry.cs`、`SettingsStore.cs` | 工作区、顶部锚点、按屏幕设置、DIP 与像素转换 |
| `IslandPanel.swift`、`PositionDragHandle.swift` | `Windows/IslandWindow.xaml.cs`、`NativeMethods.cs` | HWND 风格、动画画布、拖动鼠标捕获、屏幕变化 |
| `IslandView.swift`、`IslandBucketMenu.swift` | `Windows/IslandWindow.xaml` | 胶囊、额度卡片、菜单和按钮 |
| `IslandApp.swift` | `Windows/Program.cs`、`TrayIcon.cs` | 入口、单实例、通知区域菜单、退出清理 |
| 各 Swift checks | `Checks/Program.cs`、`Windows/SmokeChecks.cs` | 无测试框架依赖的逻辑、进程与 WPF 检查 |

表内 Windows 路径以 `windows/CodexIsland.` 开头的项目目录为基准。

## Windows 特有处理

1. **子进程与安装路径**：查找本机实际存在的原生 exe，支持桌面 CLI 缓存及 npm 新旧目录；不执行 `.cmd` 包装器。通过参数列表传入命令，不拼接 shell 命令。继承用户环境，允许显式路径和 `CODEX_HOME`。
2. **生命周期**：通信串行化，所有请求都有时限；故障后重建连接。退出时取消刷新并只回收本应用启动的进程。Windows Job Object 在可用时进一步防止 UI 意外退出后残留子进程。
3. **焦点与托盘**：无边框透明窗口，加 `WS_EX_TOOLWINDOW` 和 `WS_EX_NOACTIVATE`，点击通过 `WM_MOUSEACTIVATE` 保持非激活。使用 `NotifyIcon`；重复启动向已存在的窗口发送显示消息。
4. **缩放和位置**：Win32 几何全部使用物理像素，WPF 布局使用 DIP。保存显示器 ID、横向比例、相对工作区顶部的 DIP 距离，按目标屏幕 DPI 转换一次。这样避开任务栏，也能表示左侧或上方的负坐标显示器。
5. **动画与命中**：移入事件立即触发展开，动画为 180 毫秒；移出事件配合指针轮询维护 280 毫秒的收起缓冲。窗口在过渡期间采用起止区域的并集画布，岛形状在其中变化，结束后裁去透明空间。画布透明区域不截获鼠标。悬停检测考虑当前可见范围和目标范围，避免展开到详情区时立刻收起。
6. **持久化**：设置按用户保存在 LocalAppData，采用临时文件替换。顶部拖动保存失败时恢复上次保存的位置并显示提示；托盘调整模式保存失败时保留草稿。损坏设置采用默认位置；外接屏暂时不存在时不覆盖原屏幕 ID。
7. **菜单与排版**：额度菜单使用自定义 WPF 模板，统一深色圆角、选中状态与字体。弹窗单独应用显示大小，打开时暂停自动收起，选择后释放保护。标题、数值与辅助文字采用统一层级，参考 [Geist](https://vercel.com/geist/typography) 和 [Fluent](https://fluent2.microsoft.design/typography)。
8. **顶部移动与复位**：展开后在顶部额度栏按住左键，首次物理坐标变化就开始拖动，没有长按时间或位移门槛。拖动期间暂停悬停收起，使用物理像素保持按压锚点；松开时按落点显示器重新计算大小、限制到工作区并保存。捕获丢失时取消草稿，恢复已保存位置。原地单击切换固定状态并保持展开，避免双击首击缩小第二击的命中区域；依据系统 ClickCount 识别第二击，未移动时松开复位到主屏顶部并收起，保留各屏幕大小设置。第二击发生移动时优先拖动；拖动后的点击不会误作复位。面板中的位置按钮已移除，托盘仍提供原有调整模式。

## 验证与交付

Windows 0.3.3 的即刻拖动和顶部双击复位已通过 51 项核心检查、46 项 WPF 窗口检查和 28 项原生鼠标输入检查。运行方法、包位置、已验证环境和剩余实机验收项见 [开发说明](DEVELOPMENT.md)，日常操作见 [使用指南](../../README.md)。当前实现额度查询和主要交互，不添加账号管理、声音提醒、开机启动或额度重置。

Windows 适配涉及两个独立层面：额度通信已经在本机真实账号验证；显示器与窗口行为已经在本机单屏验证。多屏混合 DPI、唤醒和 ARM64 需要后续实机补充，不以数学模型测试替代。
