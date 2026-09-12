using System.Text;
using System.Text.Json;
using CodexIsland.Core;

namespace CodexIsland.Windows;

/// <summary>Isolated rejection-path checks: no shell dispatch, no existing session reads, no UIA actions.</summary>
internal static class TaskNavigationChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string output)
    {
        var foreground = TaskNavigationNative.GetForegroundWindow();
        var thread = Guid.Parse("01994176-7952-7000-8000-111111111111");
        var parent = Guid.Parse("01994176-7952-7000-8000-222222222222");
        var task = new TaskActivity("fixture", "Navigation fixture", "fixture", TaskActivityStatus.Running, "");
        check((await TaskNavigator.OpenAsync(task, false)).Contains("缺少有效"), "task navigation rejects missing navigation metadata");
        check((await TaskNavigator.OpenAsync(task with { Navigation = new("not-a-thread", "local") }, false)).Contains("缺少有效"),
            "task navigation rejects an invalid thread before any application dispatch");
        var desktop = task with { Navigation = new(thread.ToString("D"), "local", OwnerClientType: "desktop") };
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            check((await TaskNavigator.OpenAsync(desktop, false, canceled.Token)).Contains("已取消"),
                "pre-canceled Desktop navigation never opens an application");
        }
        check((await TaskNavigator.OpenAsync(desktop, true)).Contains("多台设备"),
            "ambiguous Desktop routes stop before dispatch");
        var side = task with { Navigation = new(thread.ToString("D"), "local", IsSideConversation: true,
            ParentNavigationPath: $"/local/{parent:D}", SideTabTitle: "Fixture tab") };
        check((await TaskNavigator.OpenAsync(side, true)).Contains("多台设备"),
            "ambiguous side chats do not open their parent or inspect accessibility");
        check((await TaskNavigator.OpenAsync(side with { Navigation = side.Navigation! with { ParentNavigationPath = $"/local/{parent:D}?hostId=other" } }, false)).Contains("缺少可打开"),
            "side-chat navigation rejects a parent belonging to a different host");
        check((await TaskNavigator.OpenAsync(side with { Navigation = side.Navigation! with { SideTabTitle = null } }, false)).Contains("页签名称"),
            "side-chat navigation waits for an exact tab title before opening a parent");

        var directory = Path.Combine(output, "navigation-fixtures");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.jsonl");
        var encoding = new UTF8Encoding(false);
        void WriteRecord(string type, Guid id, bool newline = true) => File.WriteAllText(path,
            JsonSerializer.Serialize(new { type, payload = new { id = id.ToString("D"), source = "cli", originator = "fixture" } })
                + (newline ? "\n" : ""), encoding);
        WriteRecord("session_meta", parent);
        check(TaskNavigationSession.Read(path, thread) is null, "session metadata from another thread cannot establish terminal ownership");
        WriteRecord("response_item", thread);
        check(TaskNavigationSession.Read(path, thread) is null, "conversation body records are never used as terminal metadata");
        WriteRecord("session_meta", thread, false);
        check(TaskNavigationSession.Read(path, thread) is null, "an incomplete first session record cannot establish terminal ownership");
        WriteRecord("session_meta", thread);
        File.AppendAllText(path, "this is intentionally not valid JSON and must not be parsed", encoding);
        check(TaskNavigationSession.Read(path, thread) is { Source: "cli", Originator: "fixture" },
            "local session navigation reads only the matching first metadata record");
        File.WriteAllText(path, new string(' ', 256 * 1024) + "\n", encoding);
        check(TaskNavigationSession.Read(path, thread) is null, "oversized session metadata is rejected within its read bound");

        check(TaskSideChatNavigator.MatchesTab("app-shell-tab-existing", "  Fixture\n tab  ", "Fixture tab", true),
            "side-chat tab matching normalizes whitespace without changing title identity");
        check(!TaskSideChatNavigator.MatchesTab("app-shell-tab-panel-existing", "Fixture tab", "Fixture tab", true)
            && !TaskSideChatNavigator.MatchesTab("app-shell-tab-", "Fixture tab", "Fixture tab", true),
            "a side-chat panel or empty tab identifier is never selectable");
        check(!TaskSideChatNavigator.MatchesTab("unrelated-tab", "Fixture tab", "Fixture tab", true)
            && !TaskSideChatNavigator.MatchesTab("app-shell-tab-existing", "Fixture tab extra", "Fixture tab", true)
            && !TaskSideChatNavigator.MatchesTab("app-shell-tab-existing", "Fixture tab", "Fixture tab", false),
            "unrelated controls and partial title matches cannot select a side chat");
        check(TaskTerminalNavigator.TryRunConsoleProbe(["--inspect-task-console", "0", "0"], out var code) && code == 2,
            "the isolated console probe rejects invalid process identities before attaching");
        check(TaskNavigationNative.GetForegroundWindow() == foreground, "navigation rejection checks preserve the user's foreground window");
    }
}
