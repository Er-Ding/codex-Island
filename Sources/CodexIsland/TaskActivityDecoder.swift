import Foundation

/// Projects Desktop's confirmed stream snapshot into a small read-only summary.
/// Cached thread metadata is deliberately not accepted as execution evidence.
enum TaskActivityDecoder {
    static func hasConfirmedEnd(_ state: [String: Any]) -> Bool {
        if let runtime = state["threadRuntimeStatus"] as? [String: Any],
           let type = runtime["type"] as? String {
            return type == "idle"
        }
        guard let status = latestTurn(in: state)?["status"] as? String else { return false }
        return ["completed", "interrupted", "failed"].contains(status)
    }

    static func task(from state: [String: Any], id: String, deviceName: String,
                     fallbackTitle: String? = nil) -> TaskActivity? {
        let runtime = state["threadRuntimeStatus"] as? [String: Any]
        let runtimeType = runtime?["type"] as? String
        let flags = runtime?["activeFlags"] as? [String] ?? []
        let turn = latestTurn(in: state)
        let requests = state["requests"] as? [[String: Any]] ?? []
        let awaitingApproval = flags.contains("waitingOnApproval")
        let awaitingInput = flags.contains("waitingOnUserInput")
        let waiting = awaitingApproval || awaitingInput || !requests.isEmpty

        // A previous in-progress turn can remain in history after runtime went
        // idle. The live runtime status takes precedence over that old turn.
        if let runtimeType {
            guard runtimeType == "active" else { return nil }
        } else {
            guard turn?["status"] as? String == "inProgress" || waiting else { return nil }
        }

        let detail: String
        if awaitingApproval {
            detail = "等待你在 Codex Desktop 中批准操作"
        } else if waiting {
            detail = "等待你在 Codex Desktop 中回复"
        } else {
            detail = progress(in: turn)
        }
        // Cached names may fill a missing title, but never determine task status.
        let title = [state["title"] as? String, state["generatedTitle"] as? String, fallbackTitle]
            .compactMap { $0 }.map { compact($0, limit: 120) }.first { !$0.isEmpty } ?? "未命名任务"
        return TaskActivity(id: id, title: title,
                            deviceName: deviceName, status: waiting ? .waiting : .running,
                            detail: detail)
    }

    private static func latestTurn(in state: [String: Any]) -> [String: Any]? {
        if let historyState = state["turnHistory"] as? [String: Any],
           historyState["kind"] as? String == "canonical" {
            guard let history = historyState["history"] as? [String: Any],
                  let island = (history["islands"] as? [[String: Any]])?.last,
                  let boundary = island["newerBoundary"] as? [String: Any],
                  boundary["status"] as? String == "exhausted",
                  let key = (island["entries"] as? [[String: Any]])?.last?["value"] as? String,
                  let entities = history["entitiesByKey"] as? [String: Any] else { return nil }
            return entities[key] as? [String: Any]
        }
        return (state["turns"] as? [[String: Any]])?.last
    }

    private static func progress(in turn: [String: Any]?) -> String {
        guard turn?["status"] as? String == "inProgress" else { return "正在执行，等待进展更新" }
        let items = turn?["items"] as? [[String: Any]] ?? []
        if let update = items.last(where: { $0["type"] as? String == "todo-list" }),
           let plan = update["plan"] as? [[String: Any]], !plan.isEmpty {
            let completed = plan.filter { $0["status"] as? String == "completed" }.count
            let step = plan.first { $0["status"] as? String == "inProgress" }?["step"] as? String
            let summary = "计划步骤 \(completed)/\(plan.count)"
            if let step, !step.isEmpty { return "\(summary) · \(compact(step, limit: 120))" }
            return summary
        }
        // Only public assistant messages are surfaced. Raw reasoning, tool
        // arguments, terminal output and approval payloads stay out of the UI.
        if let message = items.last(where: {
            $0["type"] as? String == "agentMessage" && $0["phase"] as? String != "final_answer"
        }), let text = message["text"] as? String, !text.isEmpty {
            return compact(text, limit: 160)
        }
        if let item = items.last(where: { $0["status"] as? String == "inProgress" }) {
            switch item["type"] as? String {
            case "commandExecution": return "正在执行命令"
            case "fileChange": return "正在修改文件"
            case "mcpToolCall", "dynamicToolCall": return "正在调用工具"
            default: break
            }
        }
        return "正在执行，等待进展更新"
    }

    private static func compact(_ text: String, limit: Int) -> String {
        String(text.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ").prefix(limit))
    }
}
