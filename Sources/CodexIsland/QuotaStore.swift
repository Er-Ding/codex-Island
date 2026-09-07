import Foundation
import Combine

@MainActor
final class QuotaStore: ObservableObject {
    @Published var snapshot: QuotaSnapshot?
    @Published var isRefreshing = false
    @Published var errorMessage: String?
    let isDemo: Bool
    var isStale: Bool {
        errorMessage != nil || snapshot.map { Date().timeIntervalSince($0.fetchedAt) > 90 } != false
    }
    private let client = QuotaClient()
    private var timer: Timer?
    private var task: Task<Void, Never>?
    private var failures = 0
    private var nextAttempt = Date.distantPast
    private var active = false

    init(isDemo: Bool = false) {
        self.isDemo = isDemo
        if isDemo { snapshot = .demo() }
        client.onQuotaChanged = { [weak self] in
            Task { @MainActor [weak self] in
                guard let self, self.active, !self.isRefreshing,
                      Date().timeIntervalSince(self.snapshot?.fetchedAt ?? .distantPast) > 5 else { return }
                self.refresh()
            }
        }
    }
    func start() {
        guard !active else { return }
        active = true
        refresh()
        let timer = Timer(timeInterval: 30, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, Date() >= self.nextAttempt else { return }
                self.refresh()
            }
        }
        timer.tolerance = 3
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }
    func refresh() {
        guard active, !isRefreshing else { return }
        if isDemo { snapshot = .demo(); return }
        isRefreshing = true
        task = Task { [weak self] in
            guard let self, self.active, !Task.isCancelled else { return }
            do {
                let latest = try await client.fetch()
                guard !Task.isCancelled else { return }
                snapshot = latest
                errorMessage = nil
                failures = 0
                nextAttempt = Date().addingTimeInterval(30)
            } catch {
                guard !Task.isCancelled else { return }
                errorMessage = (error as? QuotaFailure)?.errorDescription ?? "暂时无法读取额度，请稍后刷新。"
                failures += 1
                nextAttempt = Date().addingTimeInterval(min(300, 30 * pow(2, Double(min(failures, 4)))))
            }
            isRefreshing = false
        }
    }
    func stop() {
        active = false
        timer?.invalidate()
        timer = nil
        task?.cancel()
        task = nil
        client.stop()
        isRefreshing = false
    }
}
