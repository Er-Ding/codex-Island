using System.Text;
using System.Text.Json.Nodes;

namespace CodexIsland.Core;

public enum TaskActivityStatus { Running, Waiting, Unknown }

public sealed record TaskNavigationContext(string ThreadId, string HostId, string? OwnerClientType = null,
    string? Source = null, string? Cwd = null, string? RolloutPath = null, string? SshAlias = null,
    bool IsSideConversation = false, string? ParentNavigationPath = null, string? SideTabTitle = null)
{
    public string? SideChatParentThreadId
    {
        get
        {
            if (!IsSideConversation || ParentNavigationPath is not { } path || path.Contains('#')) return null;
            var parts = path.Split('?', 2);
            var route = parts[0].Split('/');
            if (route.Length != 3 || route[0] != "" || route[1] != "local" || !Guid.TryParseExact(route[2], "D", out var id)) return null;
            if (parts.Length == 2 && parts[1] != "hostId=" + Uri.EscapeDataString(HostId)) return null;
            return id.ToString("D");
        }
    }
}

public sealed record TaskActivity(string Id, string Title, string DeviceName, TaskActivityStatus Status,
    string Detail, TaskNavigationContext? Navigation = null)
{
    public string StatusTitle => Status switch
    {
        TaskActivityStatus.Running => "进行中", TaskActivityStatus.Waiting => "等待操作", _ => "状态待同步"
    };
    public string OpenActionTitle => Navigation?.IsSideConversation == true ? "在 Codex Desktop 定位此侧边聊天"
        : Navigation?.OwnerClientType switch
        {
            "desktop" => "在 Codex Desktop 打开此会话", "vscode" => "打开 VS Code 中的此会话",
            _ => Navigation?.Source == "cli" ? "定位此会话的终端页签" : "打开此会话"
        };
}

public sealed record TaskActivitySnapshot(IReadOnlyList<TaskActivity> Tasks, string ConnectionText, bool IsConnected);

public interface ITaskActivityClient : IAsyncDisposable
{
    Task<TaskActivitySnapshot> FetchAsync(CancellationToken cancellationToken = default);
    Task<TaskNavigationContext?> RefreshSideChatNavigationAsync(TaskNavigationContext context, CancellationToken cancellationToken = default);
    void Stop() { }
}

internal static class TaskJson
{
    public static string? Text(this JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    public static int? Integer(this JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    public static bool Boolean(this JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    public static IEnumerable<JsonObject> Objects(this JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
    public static string? Nonempty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    public static string Compact(string text, int limit) => string.Concat(string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).EnumerateRunes().Take(limit).Select(r => r.ToString()));
}
