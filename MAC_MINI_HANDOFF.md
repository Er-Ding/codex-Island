# 在 Mac mini 上继续调试 Codex Island

这是当前对话的调试交接摘要，不是完整聊天记录。

## 用户目标与当前问题
- 原生 macOS Codex 额度灵动岛，摄像头附近显示；无刘海屏幕默认贴顶。
- 当前 0.2.0 支持进入调整模式、拖动、完成保存、取消和菜单恢复默认。位置按显示器编号、横向比例、距顶部距离保存。
- 用户最新反馈：MacBook 上调位置正常，Mac mini 上拖不动。尚未在 Mac mini 复现，根因未确认，不能说已修复。
- 用户希望在 Mac mini 本机继续调试，中文解释说人话。

## 实现入口
- Swift + SwiftUI + AppKit，macOS 13+，现有发布包 arm64。
- Sources/CodexIsland/IslandApp.swift：入口、菜单。
- IslandPanel.swift：窗口、全矩形悬停、位置模式、保存和屏幕约束。
- PositionDragHandle.swift：NSView.mouseDown/mouseDragged/mouseUp；通过事件 locationInWindow 转屏幕坐标移动窗口，不依靠全局鼠标位置。
- IslandPlacement.swift：位置存储和屏幕/刘海约束。设置键 island.placement.v1，应用编号 local.stephen.codexisland。
- HoverTracking.swift：完整窗口内外判断。每 0.1 秒读取鼠标位置，进入120ms展开、离开300ms收起，调整位置时暂停。
- QuotaClient.swift：启动本机 Codex app-server，只读取账号额度，不发起聊天任务。

## 已完成验证与边界
- 本机编译、应用签名验证通过。
- 18项位置检查、8项悬停检查通过；原有14项额度检查曾通过。
- MacBook GUI检查过进入调整、拖动、完成、取消；确认保存位置改变且取消不覆盖保存值，进程退出后设置仍在。
- 这些结果不等于 Mac mini 的实际鼠标事件链已验证。
- CUA 使用的虚拟指针与 NSEvent.mouseLocation 的物理鼠标位置不总是一致；不要混淆。

## 在 Mac mini 的检查顺序
1. 先确认实际运行的应用路径，退出重复或旧进程，核对二进制 SHA-256（见 BUILD-FINGERPRINT.txt），不能只看0.2.0版本号。
2. 确认进入绿色边框的“拖动调整位置”模式；普通模式故意锁定窗口。
3. 记录 Mac mini 的 macOS 版本、显示器分辨率/缩放/布局，以及直接用鼠标还是通过远程桌面操作。上述都是待核实因素，不是已确认原因。
4. 为 PositionDragView 临时记录 mouseDown / mouseDragged / mouseUp 是否触发、window.isMovable、事件坐标、移动前后frame；不记录账号和凭据。
5. 区分没收到鼠标事件、没进入可移动状态、frame没变化、移动后被位置约束拉回，按实际证据修复。
6. 实际检查拖动、取消、保存、重启恢复、悬停仍正常，并重新打包。

## 编译与检查
在项目目录运行：
- bash scripts/build-app.sh
- bash scripts/test-placement.sh
- bash scripts/test-hover.sh
- bash scripts/test.sh

现有脚本使用 Apple 命令行开发工具即可编译，无需第三方软件包。若这台 Mac 没有开发工具，先检查并按系统提示安装。
