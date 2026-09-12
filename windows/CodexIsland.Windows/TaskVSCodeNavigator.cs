using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexIsland.Windows;

internal static class TaskVSCodeNavigator
{
    private sealed record Destination(int WindowId, TaskNavigationNative.Identity Root,
        TaskNavigationNative.Identity Renderer, TaskNavigationNative.Identity ExtensionHost,
        string Run, string Log, DateTime LogCreated);

    internal static async Task<string?> OpenAsync(Guid thread, CancellationToken cancellationToken)
    {
        try { return await OpenCoreAsync(thread, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return "无法读取或验证 VS Code 原会话窗口"; }
    }

    private static async Task<string?> OpenCoreAsync(Guid thread, CancellationToken cancellationToken)
    {
        var first = await LocateAsync(thread, cancellationToken);
        if (first.Destination is not { } destination) return first.Reason;
        // An old windowId silently routes to a different Code window. Obtain live renderer IDs twice,
        // and require the same root, extension host and ownership record immediately before dispatch.
        var second = await LocateAsync(thread, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (second.Destination != destination || !TaskNavigationNative.Alive(destination.Root)
            || !TaskNavigationNative.Alive(destination.Renderer) || !TaskNavigationNative.Alive(destination.ExtensionHost))
            return "VS Code 会话窗口已变化，无法准确定位";
        try
        {
            var start = new ProcessStartInfo(destination.Root.Path) { UseShellExecute = false };
            start.Environment.Remove("ELECTRON_RUN_AS_NODE");
            start.ArgumentList.Add("--open-url");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add($"vscode://openai.chatgpt/local/{thread:D}?windowId={destination.WindowId}");
            using var process = Process.Start(start);
            return process is null ? "VS Code 未接受会话链接" : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return "VS Code 未接受会话链接"; }
    }

    private static async Task<(Destination? Destination, string Reason)> LocateAsync(Guid thread, CancellationToken cancellationToken)
    {
        var processes = TaskNavigationNative.Processes("Code");
        var roots = processes.Values.Where(p => !processes.ContainsKey(p.Parent)).ToArray();
        if (roots.Length != 1) return (null, "无法确定正在运行的 VS Code 实例");
        var root = roots[0];
        var liveWindows = await LiveWindowsAsync(root, cancellationToken);
        if (liveWindows is null) return (null, "无法读取 VS Code 当前窗口标识");
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "logs");
        if (!Directory.Exists(logs)) return (null, "无法读取 VS Code 窗口信息");
        var matches = new List<Destination>();
        foreach (var run in Directory.EnumerateDirectories(logs).OrderDescending().Take(12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DateTime.TryParseExact(Path.GetFileName(run), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var runStarted)
                || Math.Abs((runStarted.ToUniversalTime() - new DateTime(root.Started, DateTimeKind.Utc)).TotalSeconds) > 10) continue;
            foreach (var window in Directory.EnumerateDirectories(run, "window*").Take(65))
            {
                var name = Path.GetFileName(window);
                if (!int.TryParse(name.AsSpan(6), out var id) || !liveWindows.TryGetValue(id, out var rendererPid)
                    || !processes.TryGetValue(rendererPid, out var renderer)
                    || !TaskNavigationNative.DescendsFrom(renderer, root, processes)) continue;
                var log = Path.Combine(window, "exthost", "openai.chatgpt", "Codex.log");
                if (LastRole(log, thread) != "owner") continue;
                var hostEvent = LastExtensionHost(Path.Combine(window, "exthost", "exthost.log"));
                if (hostEvent is not { } host || !processes.TryGetValue(host.Pid, out var extensionHost)
                    || !TaskNavigationNative.DescendsFrom(extensionHost, root, processes)
                    || Math.Abs((new DateTime(extensionHost.Started, DateTimeKind.Utc) - host.Started.ToUniversalTime()).TotalSeconds) > 10)
                    continue;
                matches.Add(new(id, root, renderer, extensionHost, run, log, File.GetCreationTimeUtc(log)));
            }
        }
        return matches.Count == 1 ? (matches[0], "")
            : (null, matches.Count == 0 ? "未找到可验证的 VS Code 原会话窗口" : "多个 VS Code 窗口包含此会话，无法准确定位");
    }

    private static async Task<Dictionary<int, int>?> LiveWindowsAsync(TaskNavigationNative.Identity root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(root.Path)!;
        var cli = Path.Combine(directory, "resources", "app", "out", "cli.js");
        if (!File.Exists(cli))
        {
            var launcher = Path.Combine(directory, "bin", "code.cmd");
            if (!File.Exists(launcher) || new FileInfo(launcher).Length > 16384) return null;
            // Recent Code installers keep app resources in a version subdirectory. Read the installed
            // launcher's literal resource path; never execute a command assembled from its contents.
            var version = Regex.Match(File.ReadAllText(launcher), @"\.\.\\(?<version>[A-Za-z0-9._-]+)\\resources\\app\\out\\cli\.js");
            if (!version.Success || version.Groups["version"].Value is "." or "..") return null;
            cli = Path.Combine(directory, version.Groups["version"].Value, "resources", "app", "out", "cli.js");
        }
        if (!File.Exists(cli)) return null;
        var start = new ProcessStartInfo(root.Path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        start.Environment.Remove("VSCODE_DEV");
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("--status");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var process = Process.Start(start);
        if (process is null) return null;
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, 1024 * 1024, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, 64 * 1024, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0 || output.Result is not { } text) return null;
            var windows = new Dictionary<int, int>();
            foreach (Match match in Regex.Matches(text, @"(?m)^\s*\d+\s+\d+\s+(?<pid>\d+)\s+[^\r\n]*?window \[(?<id>\d+)\]"))
            {
                if (!int.TryParse(match.Groups["pid"].Value, out var pid) || !int.TryParse(match.Groups["id"].Value, out var id)
                    || !windows.TryAdd(id, pid)) return null;
            }
            return windows;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        finally
        {
            // Only terminate our bounded diagnostics helper, never the user's Code process or its children.
            if (!process.HasExited) try { process.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var value = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (value.Length + count > limit) return null;
            value.Append(buffer, 0, count);
        }
        return value.ToString();
    }

    private static string? LastRole(string path, Guid thread)
    {
        foreach (var line in CompleteTail(path).Reverse())
        {
            if (line.Length > 4096 || !Regex.IsMatch(line, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[info\] thread_stream_role_changed ")) continue;
            var fields = line.Split(' ').Where(field => field.Contains('=')).Select(field => field.Split('=', 2)).ToArray();
            var id = fields.LastOrDefault(field => field[0] == "conversationId")?[1];
            if (Guid.TryParse(id, out var recorded) && recorded == thread) return fields.LastOrDefault(field => field[0] == "role")?[1];
        }
        return null;
    }

    private static (int Pid, DateTime Started)? LastExtensionHost(string path)
    {
        foreach (var line in CompleteTail(path).Reverse())
        {
            var match = Regex.Match(line, @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[info\] Extension host with pid (?<pid>\d+) started");
            if (match.Success && int.TryParse(match.Groups["pid"].Value, out var pid)
                && DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var time)) return (pid, time);
        }
        return null;
    }

    private static string[] CompleteTail(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return [];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, file.Length - 2 * 1024 * 1024);
            file.Position = offset;
            var bytes = new byte[(int)Math.Min(file.Length - offset, 2 * 1024 * 1024)];
            file.ReadExactly(bytes);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            return lines.Skip(offset > 0 ? 1 : 0).SkipLast(1).Select(line => line.TrimEnd('\r')).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
}
