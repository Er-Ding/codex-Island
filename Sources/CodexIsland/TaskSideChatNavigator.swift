import AppKit
import ApplicationServices
import Foundation

/// Selects an existing side-chat tab only after a user clicks its island task.
/// The title comes from the side chat's first input, not its generated task title.
@MainActor
enum TaskSideChatNavigator {
    static func open(parentURL: URL, tabTitle: String) async -> String {
        let title = normalized(tabTitle)
        guard !title.isEmpty else { return "此侧边聊天缺少页签名称，暂时无法定位" }
        guard !Task.isCancelled else { return "已取消打开会话" }
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        guard AXIsProcessTrustedWithOptions(options) else {
            return "请在系统设置 → 隐私与安全性 → 辅助功能中允许 Codex Island，然后再次点击此任务"
        }
        guard let application = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.openai.codex") else {
            return "未找到 Codex Desktop，无法打开此侧边聊天"
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        let processID: pid_t? = await withCheckedContinuation { continuation in
            NSWorkspace.shared.open([parentURL], withApplicationAt: application, configuration: configuration) { app, error in
                continuation.resume(returning: error == nil ? app?.processIdentifier : nil)
            }
        }
        guard !Task.isCancelled else { return "已取消打开会话" }
        guard let processID, processID > 0 else { return "无法打开 Codex Desktop，请确认应用可正常启动后重试" }

        // AX calls can wait for the other process. Keep all tree access off the
        // main actor, and explicitly propagate cancellation to this worker.
        let worker = Task.detached(priority: .userInitiated) {
            await selectTab(processID: processID, title: title)
        }
        return await withTaskCancellationHandler(operation: {
            await worker.value
        }, onCancel: {
            worker.cancel()
        })
    }

    nonisolated private static let messagingTimeout: Float = 0.15
    nonisolated private static let tabPrefix = "app-shell-tab-"
    nonisolated private static let panelPrefix = "app-shell-tab-panel-"
    nonisolated private static let revealLabels: Set<String> = ["Show tabs", "显示标签页"]
    nonisolated private static let focusChanged = "窗口焦点已变化，已停止定位侧边聊天，请再次点击任务"
    nonisolated private static let notLocated = "父会话已请求打开，但未能定位侧边聊天页签，请在 Desktop 中手动选择"

    private enum Scan {
        case unique(AXUIElement, String, AXUIElement)
        case revealPanel(AXUIElement, AXUIElement)
        case missing, incomplete, ambiguous, ambiguousPanel
    }

    /// Every AX message shares the same deadline, including confirmation reads.
    private struct Access: Sendable {
        let deadline: TimeInterval
        nonisolated var active: Bool { !Task.isCancelled && ProcessInfo.processInfo.systemUptime < deadline }
        nonisolated func prepare(_ element: AXUIElement) -> Bool {
            guard active else { return false }
            let remaining = deadline - ProcessInfo.processInfo.systemUptime
            guard remaining > 0 else { return false }
            AXUIElementSetMessagingTimeout(element, min(messagingTimeout, Float(remaining)))
            return true
        }
        nonisolated func read(_ element: AXUIElement, _ name: String) -> (CFTypeRef?, AXError) {
            guard prepare(element) else { return (nil, .cannotComplete) }
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name as CFString, &value)
            return (value, error)
        }
    }

    nonisolated private static func selectTab(processID: pid_t, title: String) async -> String {
        let app = AXUIElementCreateApplication(processID)
        let access = Access(deadline: ProcessInfo.processInfo.systemUptime + 8)
        // Electron requires this opt-in to expose its renderer accessibility tree.
        // https://www.electronjs.org/docs/latest/tutorial/accessibility
        let previousManual = boolean(access.read(app, "AXManualAccessibility").0)
        let enabled = access.prepare(app)
            && AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue) == .success
        defer {
            // Only undo an observed false -> true change; an unknown prior state
            // may belong to another accessibility client and must stay enabled.
            if enabled, previousManual == false {
                AXUIElementSetMessagingTimeout(app, messagingTimeout)
                AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanFalse)
            }
        }
        guard await pause(300_000_000) else { return "已取消打开会话" }
        guard let window = focusedWindow(app, access) else { return stopped(access, fallback: focusChanged) }
        var previousIdentifier: String?
        var previousPanelButton: AXUIElement?
        var requestedPanelReveal = false
        while access.active {
            guard stillFocused(app, window, access) else { return stopped(access, fallback: focusChanged) }
            switch scan(window: window, title: title, access: access) {
            case .ambiguous:
                return "父会话已请求打开，但有多个同名侧边聊天页签，无法唯一定位，请手动选择"
            case .ambiguousPanel:
                return "父会话已请求打开，但无法唯一确定侧栏显示按钮，请手动展开侧栏后重试"
            case .unique(let element, let identifier, let root):
                previousPanelButton = nil
                if previousIdentifier == identifier {
                    guard tabIdentifier(element, access) == identifier,
                          matchesLabel(element, titles: [title], access) == true,
                          belongsTo(element, root, access) == true else { previousIdentifier = nil; break }
                    guard stillFocused(app, window, access) else { return stopped(access, fallback: focusChanged) }
                    if selected(element, access), stillFocused(app, window, access) {
                        return "已选中对应的侧边聊天页签"
                    }
                    guard stillFocused(app, window, access), access.prepare(element) else {
                        return stopped(access, fallback: focusChanged)
                    }
                    let pressed = AXUIElementPerformAction(element, kAXPressAction as CFString)
                    let confirmationDeadline = min(access.deadline, ProcessInfo.processInfo.systemUptime + 1)
                    while access.active, ProcessInfo.processInfo.systemUptime < confirmationDeadline {
                        guard stillFocused(app, window, access) else { return stopped(access, fallback: focusChanged) }
                        if tabIdentifier(element, access) == identifier,
                           matchesLabel(element, titles: [title], access) == true,
                           belongsTo(element, root, access) == true, selected(element, access),
                           stillFocused(app, window, access) {
                            return "已选中对应的侧边聊天页签"
                        }
                        guard await pause(100_000_000) else { return "已取消打开会话" }
                    }
                    return stopped(access, fallback: pressed == .success
                        ? "父会话已请求打开，但尚未确认侧边聊天页签已选中，请手动检查"
                        : "父会话已请求打开，但无法选中对应侧边聊天页签，请手动选择")
                }
                previousIdentifier = identifier
            case .revealPanel(let button, let root):
                previousIdentifier = nil
                if !requestedPanelReveal, let previousPanelButton, CFEqual(previousPanelButton, button),
                   matchesLabel(button, titles: revealLabels, access) == true,
                   belongsTo(button, root, access) == true, unpressed(button, access) {
                    guard stillFocused(app, window, access), access.prepare(button) else {
                        return stopped(access, fallback: focusChanged)
                    }
                    // Only restore existing right-side tabs, once. Never invoke
                    // a new-tab control or the possibly-creating bottom toggle.
                    AXUIElementPerformAction(button, kAXPressAction as CFString)
                    requestedPanelReveal = true
                }
                previousPanelButton = button
            case .missing, .incomplete:
                previousIdentifier = nil
                previousPanelButton = nil
            }
            guard await pause(150_000_000) else { return "已取消打开会话" }
        }
        return stopped(access, fallback: notLocated)
    }

    nonisolated private static func focusedWindow(_ app: AXUIElement, _ access: Access) -> AXUIElement? {
        guard boolean(access.read(app, "AXFrontmost").0) == true else { return nil }
        return element(access.read(app, "AXFocusedWindow").0)
    }

    nonisolated private static func stillFocused(_ app: AXUIElement, _ window: AXUIElement, _ access: Access) -> Bool {
        guard let focused = focusedWindow(app, access) else { return false }
        return CFEqual(window, focused)
    }

    nonisolated private static func scan(window: AXUIElement, title: String, access: Access) -> Scan {
        guard let root = mainWebArea(window, access), access.prepare(root) else { return .incomplete }
        // Chromium supports ControlSearchKey (including tabs), not a radio-button
        // search key. Search inside the trusted app document, then filter controls.
        let limit = 512
        let predicate: [String: Any] = ["AXSearchKey": "AXControlSearchKey", "AXDirection": "AXDirectionNext",
            "AXResultsLimit": limit, "AXVisibleOnly": false, "AXImmediateDescendantsOnly": false]
        var value: CFTypeRef?
        let error = AXUIElementCopyParameterizedAttributeValue(root, "AXUIElementsForSearchPredicate" as CFString,
                                                               predicate as CFDictionary, &value)
        guard error == .success, let controls = elements(value), controls.count < limit else { return .incomplete }
        var matches: [(AXUIElement, String)] = []
        var panelButtons: [AXUIElement] = []
        for control in controls {
            guard access.active else { return .incomplete }
            guard let role = access.read(control, "AXRole").0 as? String else { return .incomplete }
            if role == "AXRadioButton" || role == "AXTabButton" {
                let (identifierValue, identifierError) = access.read(control, "AXDOMIdentifier")
                guard availableOrAbsent(identifierError) else { return .incomplete }
                if let identifier = identifierValue as? String, isTabIdentifier(identifier) {
                    guard let titleMatches = matchesLabel(control, titles: [title], access) else { return .incomplete }
                    if titleMatches {
                        guard let owned = belongsTo(control, root, access) else { return .incomplete }
                        if owned, !matches.contains(where: { CFEqual($0.0, control) }) {
                            matches.append((control, identifier))
                            if matches.count > 1 { return .ambiguous }
                        }
                    }
                }
            } else if role == "AXButton" || role == "AXCheckBox" {
                guard let showsTabs = matchesLabel(control, titles: revealLabels, access) else { return .incomplete }
                if showsTabs {
                    guard let owned = belongsTo(control, root, access) else { return .incomplete }
                    if owned, !panelButtons.contains(where: { CFEqual($0, control) }) { panelButtons.append(control) }
                }
            }
        }
        if let match = matches.first { return .unique(match.0, match.1, root) }
        if panelButtons.count > 1 { return .ambiguousPanel }
        if let button = panelButtons.first, unpressed(button, access) { return .revealPanel(button, root) }
        return .missing
    }

    nonisolated private static func mainWebArea(_ window: AXUIElement, _ access: Access) -> AXUIElement? {
        // Traverse only shallow native window chrome. Never descend into a web
        // document: embedded browsers and iframes are not app-shell controls.
        var pending = [(window, 0)]
        var visited = 0
        var result: AXUIElement?
        while let (node, depth) = pending.popLast() {
            guard access.active, visited < 128 else { return nil }
            visited += 1
            guard let role = access.read(node, "AXRole").0 as? String else { return nil }
            if role == "AXWebArea" {
                let value = access.read(node, "AXURL").0
                let url = (value as? URL) ?? (value as? String).flatMap { URL(string: $0) }
                if let url, url.scheme == "app", url.host == "-", url.path == "/index.html" {
                    if let result, !CFEqual(result, node) { return nil }
                    result = node
                }
                continue
            }
            let (value, error) = access.read(node, "AXChildren")
            if error == .attributeUnsupported || error == .noValue { continue }
            guard error == .success, let children = elements(value), children.isEmpty || depth < 12,
                  visited + pending.count + children.count <= 128 else { return nil }
            pending.append(contentsOf: children.map { ($0, depth + 1) })
        }
        return result
    }

    nonisolated private static func belongsTo(_ control: AXUIElement, _ root: AXUIElement, _ access: Access) -> Bool? {
        var node = control
        for _ in 0..<64 {
            guard let parent = element(access.read(node, "AXParent").0),
                  let role = access.read(parent, "AXRole").0 as? String else { return nil }
            if role == "AXWebArea" { return CFEqual(parent, root) }
            node = parent
        }
        return nil
    }

    nonisolated private static func element(_ value: CFTypeRef?) -> AXUIElement? {
        guard let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    nonisolated private static func elements(_ value: CFTypeRef?) -> [AXUIElement]? {
        guard let value, CFGetTypeID(value) == CFArrayGetTypeID(), let values = value as? [AnyObject] else { return nil }
        let result = values.compactMap { element($0) }
        return result.count == values.count ? result : nil
    }

    nonisolated private static func isTabIdentifier(_ value: String) -> Bool {
        value.hasPrefix(tabPrefix) && !value.hasPrefix(panelPrefix)
    }

    nonisolated private static func tabIdentifier(_ element: AXUIElement, _ access: Access) -> String? {
        guard let value = access.read(element, "AXDOMIdentifier").0 as? String, isTabIdentifier(value) else { return nil }
        return value
    }

    nonisolated private static func matchesLabel(_ element: AXUIElement, titles: Set<String>, _ access: Access) -> Bool? {
        var unreadable = false
        for name in ["AXTitle", "AXDescription"] {
            let (value, error) = access.read(element, name)
            if error == .success, let value = value as? String, titles.contains(normalized(value)) { return true }
            if !availableOrAbsent(error) { unreadable = true }
        }
        return unreadable ? nil : false
    }

    nonisolated private static func unpressed(_ element: AXUIElement, _ access: Access) -> Bool {
        var confirmed = false
        for name in ["AXValue", "AXPressed"] {
            let (value, error) = access.read(element, name)
            if error == .attributeUnsupported || error == .noValue { continue }
            guard error == .success, boolean(value) == false else { return false }
            confirmed = true
        }
        return confirmed
    }

    nonisolated private static func selected(_ element: AXUIElement, _ access: Access) -> Bool {
        boolean(access.read(element, "AXSelected").0) == true || boolean(access.read(element, "AXValue").0) == true
    }

    nonisolated private static func boolean(_ value: CFTypeRef?) -> Bool? {
        if let number = value as? NSNumber {
            if number.doubleValue == 0 { return false }
            if number.doubleValue == 1 { return true }
        }
        if let text = value as? String {
            if text == "false" || text == "0" { return false }
            if text == "true" || text == "1" { return true }
        }
        return nil
    }

    nonisolated private static func availableOrAbsent(_ error: AXError) -> Bool {
        error == .success || error == .attributeUnsupported || error == .noValue
    }

    nonisolated private static func stopped(_ access: Access, fallback: String) -> String {
        Task.isCancelled ? "已取消打开会话" : (access.active ? fallback : notLocated)
    }

    nonisolated private static func normalized(_ value: String) -> String {
        value.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
    }

    nonisolated private static func pause(_ nanoseconds: UInt64) async -> Bool {
        do { try await Task.sleep(nanoseconds: nanoseconds); return !Task.isCancelled }
        catch { return false }
    }
}
