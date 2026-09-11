import Combine
import Foundation

enum TaskActivityStatus: String, Sendable {
    case running, waiting, unknown

    var title: String {
        switch self {
        case .running: return "进行中"
        case .waiting: return "等待操作"
        case .unknown: return "状态待同步"
        }
    }
}

struct TaskNavigationContext: Equatable, Sendable {
    let threadID: String
    let hostID: String
    let ownerClientType: String?
    let source: String?
    let cwd: String?
    let rolloutPath: String?
    let sshAlias: String?
    let isSideConversation: Bool
    let parentNavigationPath: String?
    let sideTabTitle: String?

    var sideChatParentThreadID: String? {
        guard isSideConversation, let path = parentNavigationPath,
              let route = URLComponents(string: path), route.scheme == nil, route.host == nil,
              route.fragment == nil else { return nil }
        let parts = route.path.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 3, parts[0].isEmpty, parts[1] == "local",
              let id = UUID(uuidString: String(parts[2])) else { return nil }
        // The parent path is app metadata, not an arbitrary external URL. Only
        // the known thread route and its optional host selector are accepted.
        let query = route.queryItems ?? []
        guard query.count <= 1, query.allSatisfy({ $0.name == "hostId" && $0.value == hostID }) else { return nil }
        return id.uuidString.lowercased()
    }
}

struct TaskActivity: Identifiable, Equatable, Sendable {
    let id: String
    let title: String
    let deviceName: String
    var status: TaskActivityStatus
    var detail: String
    var navigation: TaskNavigationContext? = nil

    var openActionTitle: String {
        if navigation?.isSideConversation == true { return "在 Codex Desktop 定位此侧边聊天" }
        switch navigation?.ownerClientType {
        case "desktop": return "在 Codex Desktop 打开此会话"
        case "vscode": return "打开 VS Code 中的此会话"
        default:
            return navigation?.source == "cli" ? "定位此会话的终端页签" : "打开此会话"
        }
    }
}

struct TaskActivitySnapshot {
    let tasks: [TaskActivity]
    let connectionText: String
    let isConnected: Bool
}

@MainActor
final class TaskActivityStore: ObservableObject {
    @Published private(set) var tasks: [TaskActivity] = []
    @Published private(set) var connectionText = "正在连接 Codex Desktop…"
    @Published private(set) var isConnected = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var openingTaskID: String?
    @Published private(set) var navigationMessage: String?

    var activeCount: Int { tasks.filter { $0.status != .unknown }.count }
    var featuredTask: TaskActivity? {
        tasks.first { $0.status == .waiting }
            ?? tasks.first { $0.status == .running }
            ?? tasks.first
    }

    private let client = TaskActivityClient()
    private let isDemo: Bool
    private var active = false
    private var timer: Timer?
    private var fetchTask: Task<Void, Never>?
    private var navigationTask: Task<Void, Never>?
    private var navigationFeedbackTask: Task<Void, Never>?

    init(isDemo: Bool = false) {
        self.isDemo = isDemo
        if isDemo { connectionText = "演示模式不读取设备任务" }
    }

    func start() {
        guard !active, !isDemo else { return }
        active = true
        refresh()
        let timer = Timer(timeInterval: 3, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.poll(showProgress: false) }
        }
        timer.tolerance = 0.3
        self.timer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    func refresh() { poll(showProgress: true) }

    func openTask(_ task: TaskActivity) {
        guard active, !isDemo, openingTaskID == nil else { return }
        openingTaskID = task.id
        navigationMessage = "正在定位会话…"
        navigationFeedbackTask?.cancel()
        navigationTask = Task { [weak self] in
            guard let self else { return }
            var target = task
            if let context = target.navigation, context.isSideConversation, context.sideTabTitle == nil,
               let refreshed = await self.client.refreshSideChatNavigation(context) {
                target.navigation = refreshed
            }
            guard self.active, !Task.isCancelled else { return }
            // Another device can report the same UUID. For side chats the URL
            // addresses the parent, so check that ID as well as the target host.
            let desktopThreadID = target.navigation?.sideChatParentThreadID ?? target.navigation?.threadID
            var otherHosts = Set(self.tasks.filter {
                $0.navigation?.threadID == desktopThreadID || $0.navigation?.sideChatParentThreadID == desktopThreadID
            }
                .compactMap { $0.navigation?.hostID })
            if let host = target.navigation?.hostID { otherHosts.insert(host) }
            let message = await TaskNavigator.open(target, desktopRouteIsAmbiguous: otherHosts.count > 1)
            guard self.active, !Task.isCancelled else { return }
            self.openingTaskID = nil
            self.navigationMessage = message
            self.navigationTask = nil
            self.navigationFeedbackTask = Task { [weak self] in
                try? await Task.sleep(nanoseconds: 10_000_000_000)
                guard !Task.isCancelled else { return }
                self?.navigationMessage = nil
            }
        }
    }

    private func poll(showProgress: Bool) {
        guard active, fetchTask == nil else { return }
        if isRefreshing != showProgress { isRefreshing = showProgress }
        fetchTask = Task { [weak self] in
            guard let self else { return }
            do {
                let snapshot = try await self.client.fetch()
                guard self.active, !Task.isCancelled else { return }
                if self.tasks != snapshot.tasks { self.tasks = snapshot.tasks }
                if self.connectionText != snapshot.connectionText { self.connectionText = snapshot.connectionText }
                if self.isConnected != snapshot.isConnected { self.isConnected = snapshot.isConnected }
            } catch {
                guard self.active, !Task.isCancelled else { return }
                // A disconnected device is not evidence that its work finished.
                let staleTasks = self.tasks.map { item in
                    var stale = item
                    stale.status = .unknown
                    stale.detail = "连接已中断，等待重新同步"
                    return stale
                }
                if self.tasks != staleTasks { self.tasks = staleTasks }
                self.connectionText = "无法连接 Codex Desktop，正在重试"
                self.isConnected = false
            }
            if self.isRefreshing { self.isRefreshing = false }
            self.fetchTask = nil
        }
    }

    func stop() {
        active = false
        timer?.invalidate()
        timer = nil
        fetchTask?.cancel()
        fetchTask = nil
        client.stop()
        isRefreshing = false
        navigationTask?.cancel()
        navigationTask = nil
        navigationFeedbackTask?.cancel()
        navigationFeedbackTask = nil
        openingTaskID = nil
        navigationMessage = nil
    }
}
