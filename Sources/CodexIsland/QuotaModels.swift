import Foundation

struct QuotaWindow: Codable, Equatable {
    var usedPercent: Double
    var windowDurationMins: Int?
    var resetsAt: Double?

    var remainingPercent: Double { max(0, min(100, 100 - usedPercent)) }
    var periodTitle: String {
        guard let minutes = windowDurationMins, minutes > 0 else { return "当前周期" }
        if minutes == 10_080 { return "本周" }
        if minutes % 1440 == 0 { return "\(minutes / 1440)天" }
        if minutes % 60 == 0 { return "\(minutes / 60)小时" }
        return "\(minutes)分钟"
    }
    func resetText(now: Date) -> String {
        guard let reset = resetsAt else { return "恢复时间暂未提供" }
        let remaining = reset - now.timeIntervalSince1970
        guard remaining > 0 else { return "已到恢复时间，等待刷新" }
        let minutes = max(1, Int(ceil(remaining / 60)))
        if minutes >= 1440 { return "\(minutes / 1440)天\((minutes % 1440) / 60)小时后恢复" }
        if minutes >= 60 { return "\(minutes / 60)小时\(minutes % 60)分钟后恢复" }
        return "\(minutes)分钟后恢复"
    }
}

struct QuotaBucket: Identifiable, Equatable {
    var id: String
    var name: String
    var primary: QuotaWindow?
    var secondary: QuotaWindow?
}

struct QuotaSnapshot: Equatable {
    var buckets: [QuotaBucket]
    var fetchedAt: Date
    var planName: String?
    var mainBucket: QuotaBucket? {
        buckets.first(where: { $0.id == "codex" }) ?? buckets.first
    }
    static func demo(now: Date = Date()) -> QuotaSnapshot {
        QuotaSnapshot(buckets: [QuotaBucket(id: "codex", name: "Codex", primary:
            QuotaWindow(usedPercent: 28, windowDurationMins: 300, resetsAt: now.addingTimeInterval(8640).timeIntervalSince1970),
            secondary: QuotaWindow(usedPercent: 46, windowDurationMins: 10_080, resetsAt: now.addingTimeInterval(230400).timeIntervalSince1970))],
            fetchedAt: now, planName: "演示")
    }
}

enum QuotaFailure: Error, LocalizedError {
    case missingCodex, disconnected, timedOut, invalidResponse, noQuota, service(String)
    var errorDescription: String? {
        switch self {
        case .missingCodex: return "未找到 Codex，请先安装并登录 Codex。"
        case .disconnected: return "与 Codex 的连接已断开，稍后会重试。"
        case .timedOut: return "读取超时，请检查网络后刷新。"
        case .invalidResponse: return "暂时无法识别 Codex 返回的额度信息。"
        case .noQuota: return "账号暂未返回订阅额度，请确认已用 ChatGPT 账号登录 Codex。"
        case .service(let message): return message
        }
    }
    // Only controlled messages reach the UI; upstream errors may contain account data.
    static func fromServer(_ message: String) -> QuotaFailure {
        let text = message.lowercased()
        if text.contains("api key") || text.contains("apikey") || text.contains("unsupported auth") {
            return .service("当前登录方式未提供订阅额度，请用 ChatGPT 账号登录 Codex。")
        }
        if text.contains("401") || text.contains("auth") || text.contains("login") || text.contains("sign in") {
            return .service("请先在 Codex 中登录 ChatGPT 账号，再点刷新。")
        }
        if text.contains("429") { return .service("查询过于频繁，稍后会自动重试。") }
        return .service("暂时无法读取额度，请检查 Codex 登录状态和网络。")
    }
}

enum QuotaParser {
    private struct Response: Decodable {
        var rateLimits: Bucket?
        var rateLimitsByLimitId: [String: Bucket]?
    }
    private struct Bucket: Decodable {
        var limitId: String?
        var limitName: String?
        var primary: QuotaWindow?
        var secondary: QuotaWindow?
        var planType: String?
    }
    static func parse(_ data: Data, now: Date = Date()) throws -> QuotaSnapshot {
        let response: Response
        do { response = try JSONDecoder().decode(Response.self, from: data) }
        catch { throw QuotaFailure.invalidResponse }
        var all = response.rateLimitsByLimitId ?? [:]
        if let legacy = response.rateLimits {
            let key = legacy.limitId ?? "codex"
            if all[key] == nil { all[key] = legacy }
        }
        let keys = all.keys.sorted { a, b in
            if a == "codex" { return b != "codex" }
            if b == "codex" { return false }
            return a < b
        }
        let buckets = keys.compactMap { key -> QuotaBucket? in
            guard let value = all[key], value.primary != nil || value.secondary != nil else { return nil }
            return QuotaBucket(id: key, name: value.limitName ?? (key == "codex" ? "Codex" : key),
                               primary: value.primary, secondary: value.secondary)
        }
        guard !buckets.isEmpty else { throw QuotaFailure.noQuota }
        let plan = all["codex"]?.planType ?? keys.compactMap { all[$0]?.planType }.first
        return QuotaSnapshot(buckets: buckets, fetchedAt: now, planName: plan.map(planLabel))
    }
    private static func planLabel(_ plan: String) -> String {
        switch plan.lowercased() {
        case "plus": return "Plus"
        case "pro": return "Pro"
        case "free": return "免费版"
        case "team", "business": return "团队版"
        case "enterprise": return "企业版"
        case "edu": return "教育版"
        default: return "ChatGPT"
        }
    }
}

struct JSONLineBuffer {
    private var pending = Data()
    mutating func append(_ data: Data) throws -> [Data] {
        pending.append(data)
        guard pending.count <= 4 * 1024 * 1024 else { throw QuotaFailure.invalidResponse }
        var lines: [Data] = []
        while let newline = pending.firstIndex(of: 10) {
            let line = Data(pending[..<newline])
            pending.removeSubrange(...newline)
            if !line.isEmpty { lines.append(line) }
        }
        return lines
    }
}
