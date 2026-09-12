using CodexIsland.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexIsland.Windows;

/// <summary>Only called after a task is clicked. Navigation never submits a prompt or starts a shell session.</summary>
internal static class TaskNavigator
{
    internal static async Task<string> OpenAsync(TaskActivity task, bool desktopRouteIsAmbiguous,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Process discovery, log tails, shell dispatch and accessibility calls never run on WPF's dispatcher.
            return await Task.Run(() => OpenCoreAsync(task, desktopRouteIsAmbiguous, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) { return "已取消打开会话"; }
    }

    private static async Task<string> OpenCoreAsync(TaskActivity task, bool desktopRouteIsAmbiguous,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (task.Navigation is not { } context || !Guid.TryParse(context.ThreadId, out var threadId))
                return "此任务缺少有效的会话标识，暂时无法打开";
            if (context.IsSideConversation)
            {
                if (desktopRouteIsAmbiguous) return Ambiguous;
                if (!Guid.TryParse(context.SideChatParentThreadId, out var parent))
                    return "此侧边聊天缺少可打开的主会话位置，请在 Desktop 中打开所属页面";
                if (string.IsNullOrWhiteSpace(context.SideTabTitle))
                    return "尚未取得此侧边聊天的页签名称，请刷新任务后重试";
                return await TaskSideChatNavigator.OpenAsync(parent, context.SideTabTitle, cancellationToken);
            }
            if (string.Equals(context.OwnerClientType, "desktop", StringComparison.OrdinalIgnoreCase))
                return OpenDesktop(threadId, desktopRouteIsAmbiguous, cancellationToken);

            var local = context.HostId == "local"
                ? await Task.Run(() => TaskNavigationSession.Find(threadId, context.RolloutPath, cancellationToken), cancellationToken)
                : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(context.OwnerClientType, "vscode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(local?.Originator, "codex_vscode", StringComparison.OrdinalIgnoreCase))
            {
                var reason = await TaskVSCodeNavigator.OpenAsync(threadId, cancellationToken);
                return reason is null ? "已请求 VS Code 原窗口打开对应会话"
                    : OpenDesktop(threadId, desktopRouteIsAmbiguous, cancellationToken, reason);
            }
            if (context.Source == "cli" || local?.Source == "cli")
            {
                var reason = context.HostId != "local" ? "无法验证远端会话的原终端页签"
                    : local is null ? "无法读取原终端会话的位置"
                    : await TaskTerminalNavigator.OpenAsync(local, threadId, cancellationToken);
                return reason is null ? "已切换到此会话的原终端窗口或页签"
                    : OpenDesktop(threadId, desktopRouteIsAmbiguous, cancellationToken, reason);
            }
            return OpenDesktop(threadId, desktopRouteIsAmbiguous, cancellationToken,
                string.Equals(local?.Originator, "codex desktop", StringComparison.OrdinalIgnoreCase)
                    ? null : "无法确认原应用窗口");
        }
        catch (OperationCanceledException) { return "已取消打开会话"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return "暂时无法定位此会话，请确认原应用仍在运行后重试";
        }
    }

    private const string Ambiguous = "多台设备存在相同会话标识，请在 Desktop 选择对应设备后打开";

    internal static string OpenDesktop(Guid threadId, bool ambiguous, CancellationToken cancellationToken,
        string? fallbackReason = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ambiguous) return Ambiguous;
        if (!TryOpenDesktop(threadId, cancellationToken))
            return "无法打开 Codex Desktop，请确认已安装应用且 codex 链接可正常打开";
        return fallbackReason is null ? "已请求 Desktop 打开对应会话"
            : $"已改用 Desktop 请求打开同一会话：{fallbackReason.Trim().TrimEnd('。', '；', '，')}";
    }

    internal static bool TryOpenDesktop(Guid threadId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // The installed Desktop routes a thread UUID through its existing local/SSH catalog.
            // A hostId query is not a supported public routing selector.
            using var process = Process.Start(new ProcessStartInfo($"codex://threads/{threadId:D}") { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

}

/// <summary>Reads only the first bounded session_meta record, never the conversation body.</summary>
internal sealed record TaskNavigationSession(string RolloutPath, string? Source, string? Originator)
{
    internal static TaskNavigationSession? Find(Guid thread, string? rolloutPath, CancellationToken cancellationToken)
    {
        try { return FindCore(thread, rolloutPath, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    private static TaskNavigationSession? FindCore(Guid thread, string? rolloutPath, CancellationToken cancellationToken)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var sessions = Path.GetFullPath(Path.Combine(home, "sessions"));
        if (!string.IsNullOrWhiteSpace(rolloutPath) && Path.IsPathFullyQualified(rolloutPath)
            && Read(rolloutPath, thread) is { } direct) return direct;
        if (!Directory.Exists(sessions)) return null;
        var id = thread.ToString("D");
        var hex = thread.ToString("N");
        if (hex[12] == '7' && long.TryParse(hex[..12], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            foreach (var offset in new[] { 0, -1, 1 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(sessions, day.AddDays(offset).ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture));
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, $"*-{id}.jsonl").Take(4096))
                    if (Read(file, thread) is { } session) return session;
            }
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 4 };
        foreach (var file in Directory.EnumerateFiles(sessions, "*.jsonl", options).Take(8192))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith($"-{id}.jsonl", StringComparison.OrdinalIgnoreCase) && Read(file, thread) is { } session) return session;
        }
        return null;
    }

    internal static TaskNavigationSession? Read(string path, Guid thread)
    {
        try
        {
            // A UNC path may cause network access. Remote/WSL metadata is never interpreted as a local Windows file.
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)
                || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            for (var count = 0; count < 256 * 1024; count++)
            {
                var value = file.ReadByte();
                if (value < 0) return null;
                if (value != '\n') { buffer.WriteByte((byte)value); continue; }
                using var json = JsonDocument.Parse(buffer.ToArray());
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta"
                    || !root.TryGetProperty("payload", out var metadata)
                    || !Guid.TryParse(String(metadata, "id"), out var id) || id != thread) return null;
                return new(Path.GetFullPath(path), String(metadata, "source"), String(metadata, "originator"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException) { }
        return null;
    }

    private static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
