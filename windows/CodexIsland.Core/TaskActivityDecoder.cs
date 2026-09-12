using System.Text.Json.Nodes;

namespace CodexIsland.Core;

/// <summary>Only a confirmed Desktop stream supplies execution evidence; index metadata supplies titles only.</summary>
public static class TaskActivityDecoder
{
    public static bool HasConfirmedEnd(JsonObject state)
    {
        if (state["threadRuntimeStatus"] is JsonObject runtime && runtime["type"].Text() is { } type) return type == "idle";
        return LatestTurn(state)?["status"].Text() is "completed" or "interrupted" or "failed";
    }

    public static TaskActivity? Decode(JsonObject state, string id, string deviceName, string? fallbackTitle = null)
    {
        var runtime = state["threadRuntimeStatus"] as JsonObject;
        var runtimeType = runtime?["type"].Text();
        var flags = (runtime?["activeFlags"] as JsonArray)?.Select(value => value.Text()).ToArray() ?? [];
        var turn = LatestTurn(state);
        var approval = flags.Contains("waitingOnApproval");
        var requests = state["requests"].Objects().ToArray();
        // Older streams expose the pending approval through requests rather than runtime flags.
        approval |= requests.Any(request => request["method"].Text()?.Contains("requestApproval", StringComparison.OrdinalIgnoreCase) == true);
        var waiting = approval || flags.Contains("waitingOnUserInput") || requests.Length > 0;
        if (runtimeType is not null ? runtimeType != "active" : turn?["status"].Text() != "inProgress" && !waiting) return null;
        var title = new[] { state["title"].Text(), state["generatedTitle"].Text(), fallbackTitle }
            .Where(text => text is not null).Select(text => TaskJson.Compact(text!, 120)).FirstOrDefault(text => text.Length > 0) ?? "未命名任务";
        var detail = approval ? "等待你在 Codex Desktop 中批准操作" : waiting ? "等待你在 Codex Desktop 中回复" : Progress(turn);
        return new(id, title, deviceName, waiting ? TaskActivityStatus.Waiting : TaskActivityStatus.Running, detail);
    }

    internal static JsonObject? LatestTurn(JsonObject state)
    {
        if (state["turnHistory"] is JsonObject historyState && historyState["kind"].Text() == "canonical")
        {
            if (historyState["history"] is not JsonObject history || history["islands"].Objects().LastOrDefault() is not { } island
                || island["newerBoundary"] is not JsonObject boundary || boundary["status"].Text() != "exhausted"
                || island["entries"].Objects().LastOrDefault()?["value"].Text() is not { } key) return null;
            return (history["entitiesByKey"] as JsonObject)?[key] as JsonObject;
        }
        return state["turns"].Objects().LastOrDefault();
    }

    private static string Progress(JsonObject? turn)
    {
        if (turn?["status"].Text() != "inProgress") return "正在执行，等待进展更新";
        var items = turn["items"].Objects().ToArray();
        var plan = items.LastOrDefault(item => item["type"].Text() == "todo-list")?["plan"].Objects().ToArray();
        if (plan is { Length: > 0 })
        {
            var completed = plan.Count(step => step["status"].Text() == "completed");
            var step = plan.FirstOrDefault(step => step["status"].Text() == "inProgress")?["step"].Text();
            return $"计划步骤 {completed}/{plan.Length}" + (string.IsNullOrWhiteSpace(step) ? "" : " · " + TaskJson.Compact(step, 120));
        }
        var message = items.LastOrDefault(item => item["type"].Text() == "agentMessage" && item["phase"].Text() != "final_answer")?["text"].Text();
        if (!string.IsNullOrWhiteSpace(message)) return TaskJson.Compact(message, 160);
        return items.LastOrDefault(item => item["status"].Text() == "inProgress")?["type"].Text() switch
        {
            "commandExecution" => "正在执行命令", "fileChange" => "正在修改文件",
            "mcpToolCall" or "dynamicToolCall" => "正在调用工具", _ => "正在执行，等待进展更新"
        };
    }
}
