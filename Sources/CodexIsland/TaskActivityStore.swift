import Combine
import Foundation

enum TaskActivityStatus: String {
    case running, waiting, unknown

    var title: String {
        switch self {
        case .running: return "进行中"
        case .waiting: return "等待操作"
        case .unknown: return "状态待同步"
        }
    }
}

struct TaskActivity: Identifiable, Equatable {
    let id: String
    let title: String
    let deviceName: String
    var status: TaskActivityStatus
    var detail: String
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

    var activeCount: Int { tasks.filter { $0.status != .unknown }.count }

    private let client = TaskActivityClient()
    private let isDemo: Bool
    private var active = false
    private var timer: Timer?
    private var fetchTask: Task<Void, Never>?

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
    }
}
