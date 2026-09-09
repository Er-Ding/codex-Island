import AppKit
import Darwin

@main
enum CodexIslandApp {
    @MainActor
    static func main() {
        if CommandLine.arguments.contains("--check-quota") {
            Task { @MainActor in await checkQuota() }
            dispatchMain()
        }
        let app = NSApplication.shared
        if CommandLine.arguments.contains("--diagnose-screen") {
            IslandPanelController.printScreenDiagnostics()
            return
        }
        app.setActivationPolicy(.accessory)
        let delegate = IslandAppDelegate()
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }

    @MainActor
    private static func checkQuota() async {
        let client = QuotaClient()
        do {
            let snapshot = try await client.fetch()
            print("fetchedAt=\(ISO8601DateFormatter().string(from: snapshot.fetchedAt))")
            for bucket in snapshot.buckets {
                for window in [bucket.primary, bucket.secondary].compactMap({ $0 }) {
                    print("\(bucket.id): \(window.periodTitle) remaining=\(Int(window.remainingPercent.rounded()))%")
                }
            }
            client.stop()
            Darwin.exit(0)
        } catch {
            let failure = (error as? QuotaFailure) ?? .disconnected
            print(failure.localizedDescription)
            client.stop()
            Darwin.exit(1)
        }
    }
}

@MainActor
final class IslandAppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var store: QuotaStore?
    private var activity: TaskActivityStore?
    private var island: IslandPanelController?
    private var statusItem: NSStatusItem?
    private var visibilityItem: NSMenuItem?
    private var positionItem: NSMenuItem?
    private var cancelPositionItem: NSMenuItem?
    private var resetPositionItem: NSMenuItem?
    private var displaySizeItem: NSMenuItem?
    private var displaySizeOptions: [NSMenuItem] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        let arguments = CommandLine.arguments
        let store = QuotaStore(isDemo: arguments.contains("--demo"))
        self.store = store
        let activity = TaskActivityStore(isDemo: store.isDemo)
        self.activity = activity
        island = IslandPanelController(store: store, activity: activity,
                                       initiallyExpanded: arguments.contains("--expanded"))
        installStatusItem(isDemo: store.isDemo)
        store.start()
        activity.start()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        island?.show()
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        store?.stop()
        activity?.stop()
        island?.close()
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
        statusItem = nil
    }

    private func installStatusItem(isDemo: Bool) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button {
            let icon = NSImage(systemSymbolName: "chart.bar.xaxis", accessibilityDescription: "Codex 额度")
            icon?.isTemplate = true
            button.image = icon
            button.toolTip = isDemo ? "Codex Island · 演示数据" : "Codex Island · 查看额度"
        }

        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        let title = NSMenuItem(title: isDemo ? "Codex Island · 演示" : "Codex Island", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        menu.addItem(.separator())
        let visibility = NSMenuItem(title: "隐藏灵动岛", action: #selector(toggleIsland), keyEquivalent: "")
        visibility.target = self
        visibilityItem = visibility
        menu.addItem(visibility)
        let refresh = NSMenuItem(title: "刷新额度", action: #selector(refreshQuota), keyEquivalent: "r")
        refresh.target = self
        menu.addItem(refresh)
        menu.addItem(.separator())
        let displaySize = NSMenuItem(title: "显示大小", action: nil, keyEquivalent: "")
        displaySize.toolTip = "调整浮动岛当前所在显示器上的大小"
        let displaySizeMenu = NSMenu(title: "显示大小")
        displaySizeMenu.delegate = self
        displaySizeMenu.autoenablesItems = false
        for preference in IslandSizePreference.allCases {
            let option = NSMenuItem(title: preference.title, action: #selector(setDisplaySize(_:)), keyEquivalent: "")
            option.target = self
            option.tag = preference.rawValue
            displaySizeOptions.append(option)
            displaySizeMenu.addItem(option)
        }
        displaySize.submenu = displaySizeMenu
        displaySizeItem = displaySize
        menu.addItem(displaySize)
        let position = NSMenuItem(title: "调整位置…", action: #selector(adjustPosition), keyEquivalent: "")
        position.target = self
        positionItem = position
        menu.addItem(position)
        let cancelPosition = NSMenuItem(title: "取消位置调整", action: #selector(cancelPosition), keyEquivalent: "")
        cancelPosition.target = self
        cancelPosition.isHidden = true
        cancelPositionItem = cancelPosition
        menu.addItem(cancelPosition)
        let resetPosition = NSMenuItem(title: "恢复默认位置", action: #selector(resetPosition), keyEquivalent: "")
        resetPosition.target = self
        resetPositionItem = resetPosition
        menu.addItem(resetPosition)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出 Codex Island", action: #selector(quitApp), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        item.menu = menu
        statusItem = item
    }

    func menuWillOpen(_ menu: NSMenu) {
        visibilityItem?.title = island?.isVisible == true ? "隐藏灵动岛" : "显示灵动岛"
        let adjusting = island?.isAdjustingPosition == true
        positionItem?.title = adjusting ? "完成并锁定位置" : "调整位置…"
        cancelPositionItem?.isHidden = !adjusting
        resetPositionItem?.isEnabled = adjusting || island?.hasCustomPosition == true
        let sizePreference = island?.sizePreference ?? .automatic
        let scalePercent = Int(((island?.resolvedUIScale ?? 1) * 100).rounded())
        let canAdjustSize = island != nil && !adjusting
        displaySizeItem?.isEnabled = canAdjustSize
        for option in displaySizeOptions {
            guard let preference = IslandSizePreference(rawValue: option.tag) else { continue }
            option.state = preference == sizePreference ? .on : .off
            option.isEnabled = canAdjustSize
            option.title = preference == .automatic && sizePreference == .automatic
                ? "\(preference.title)（当前 \(scalePercent)%）"
                : preference.title
        }
    }

    @objc private func toggleIsland() { island?.toggleVisibility() }
    @objc private func refreshQuota() { store?.refresh() }
    @objc private func setDisplaySize(_ sender: NSMenuItem) {
        guard let island, !island.isAdjustingPosition,
              let preference = IslandSizePreference(rawValue: sender.tag) else { return }
        island.setSizePreference(preference)
    }
    @objc private func adjustPosition() {
        if island?.isAdjustingPosition == true { island?.finishPositionAdjustment() }
        else { island?.beginPositionAdjustment() }
    }
    @objc private func cancelPosition() { island?.cancelPositionAdjustment() }
    @objc private func resetPosition() { island?.resetPosition() }
    @objc private func quitApp() { NSApp.terminate(nil) }
}
