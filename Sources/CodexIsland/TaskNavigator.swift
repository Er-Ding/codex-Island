import AppKit
import Foundation

/// Navigation runs only in response to a click. It selects existing sessions;
/// it never sends a prompt, runs `codex resume`, or creates an SSH connection.
@MainActor
enum TaskNavigator {
    static func open(_ task: TaskActivity, desktopRouteIsAmbiguous: Bool) async -> String {
        guard let context = task.navigation, UUID(uuidString: context.threadID) != nil else {
            return "此任务缺少有效的会话标识，暂时无法打开"
        }
        // Side chats are ephemeral. Desktop's public thread URL first performs
        // thread/read, which can fail for their IDs after only raising the app.
        // This branch must precede owner/source detection (the IPC owner type
        // can still be unknown when an already running Desktop is discovered).
        if context.isSideConversation {
            guard !desktopRouteIsAmbiguous else { return "多台设备存在相同会话标识，请在 Desktop 选择对应设备后打开" }
            guard let parentID = context.sideChatParentThreadID,
                  let parentURL = URL(string: "codex://threads/\(parentID)") else {
                return "此侧边聊天缺少可打开的主会话位置，请在 Desktop 中打开所属页面"
            }
            guard let title = context.sideTabTitle else {
                return "尚未取得此侧边聊天的页签名称，请刷新任务后重试"
            }
            return await TaskSideChatNavigator.open(parentURL: parentURL, tabTitle: title)
        }
        if context.ownerClientType == "desktop" {
            return await openDesktop(context, ambiguous: desktopRouteIsAmbiguous)
        }

        let session: TaskLocalSession?
        if context.hostID == "local" {
            session = await Task.detached(priority: .userInitiated) {
                TaskLocalSession.find(threadID: context.threadID, rolloutPath: context.rolloutPath)
            }.value
        } else {
            // A remote path belongs to that host, even when a local file happens
            // to have the same name. Never read it as local session metadata.
            session = nil
        }
        guard !Task.isCancelled else { return "已取消打开会话" }

        let originator = session?.originator?.lowercased() ?? ""
        if context.ownerClientType == "vscode" || originator == "codex_vscode" {
            if let reason = await TaskVSCodeNavigator.open(threadID: context.threadID) {
                return await openDesktop(context, ambiguous: desktopRouteIsAmbiguous,
                                         fallbackReason: reason)
            }
            return "已请求 VS Code 打开对应会话"
        }
        if session?.source == "cli" || context.source == "cli" {
            let reason: String
            if let session {
                guard let failure = await TaskTerminalNavigator.open(session: session, threadID: context.threadID) else {
                    return "已切换到此会话的终端页签"
                }
                reason = failure
            } else {
                reason = context.hostID == "local" ? "无法定位原终端页签" : "无法定位远端会话的原终端页签"
            }
            return await openDesktop(context, ambiguous: desktopRouteIsAmbiguous, fallbackReason: reason)
        }
        let fallback = originator == "codex desktop" ? nil : "无法确认原应用窗口"
        return await openDesktop(context, ambiguous: desktopRouteIsAmbiguous, fallbackReason: fallback)
    }

    private static func openDesktop(_ context: TaskNavigationContext, ambiguous: Bool,
                                    fallbackReason: String? = nil) async -> String {
        guard !Task.isCancelled else { return "已取消打开会话" }
        guard !ambiguous else { return "多台设备存在相同会话标识，请在 Desktop 选择对应设备后打开" }
        guard let uuid = UUID(uuidString: context.threadID),
              let url = URL(string: "codex://threads/\(uuid.uuidString.lowercased())"),
              let application = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.openai.codex") else {
            return "未找到 Codex Desktop，无法打开此会话"
        }
        // Desktop resolves its local/SSH catalog using the thread UUID. This
        // version does not honor a hostId query on its public threads URL.
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        let opened: Bool = await withCheckedContinuation { continuation in
            NSWorkspace.shared.open([url], withApplicationAt: application, configuration: configuration) { openedApplication, error in
                continuation.resume(returning: error == nil && openedApplication != nil)
            }
        }
        if !opened { return "无法打开 Codex Desktop，请确认应用可正常启动后重试" }
        if let fallbackReason {
            let reason = fallbackReason.trimmingCharacters(in: CharacterSet(charactersIn: "。；， "))
            return "已改用 Desktop 请求打开同一会话：\(reason)"
        }
        return "已请求 Desktop 打开对应会话"
    }
}
