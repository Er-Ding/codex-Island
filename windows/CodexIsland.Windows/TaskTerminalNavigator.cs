using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows.Automation;

namespace CodexIsland.Windows;

internal static class TaskTerminalNavigator
{
    internal static async Task<string?> OpenAsync(TaskNavigationSession session, Guid thread, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { return await Task.Run(() => LocateAsync(session, thread, timeout.Token), timeout.Token).WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return "原终端定位超时，未切换任何不确定的页签"; }
        catch (Exception error) when (error is ElementNotAvailableException or ElementNotEnabledException
            or COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return "无法验证原终端页签"; }
        finally { timeout.Cancel(); }
    }

    private static async Task<string?> LocateAsync(TaskNavigationSession session, Guid thread, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var processes = TaskNavigationNative.Processes("codex");
        var owners = HoldingFile(session.RolloutPath).Where(processes.ContainsKey).Select(pid => processes[pid]).ToArray();
        if (owners.Length != 1) return "无法唯一确认原终端会话进程";
        var owner = owners[0];

        // A classic console's HWND is an exact process-to-window relation. Pseudoconsole windows
        // (Windows Terminal/VS Code/WSL) are invisible and must not be treated as visible terminals.
        // AttachConsole changes process-wide standard handles and control handlers. Run the probe
        // in an isolated helper so the GUI and its quota client never join the user's console.
        var snapshot = await InspectConsoleAsync(owner, token);
        var console = snapshot is null ? IntPtr.Zero : new IntPtr(snapshot.Window);
        var consoleMembers = snapshot?.Members ?? [];
        token.ThrowIfCancellationRequested();
        if (console != IntPtr.Zero && TaskNavigationNative.IsWindowVisible(console)
            && consoleMembers.Contains((uint)owner.Pid)
            && processes.Keys.Count(pid => consoleMembers.Contains((uint)pid)) == 1)
        {
            if (!TaskNavigationNative.Alive(owner) || !HoldingFile(session.RolloutPath).Contains(owner.Pid))
                return "原终端会话已变化，无法准确定位";
            token.ThrowIfCancellationRequested();
            if (TaskNavigationNative.IsIconic(console)) TaskNavigationNative.ShowWindowAsync(console, 9);
            token.ThrowIfCancellationRequested();
            if (!TaskNavigationNative.SetForegroundWindow(console) || TaskNavigationNative.GetForegroundWindow() != console)
                return "系统未允许激活原终端窗口";
            return null;
        }

        // Windows Terminal does not expose a public thread UUID -> pane selector. Only select a
        // pre-existing, unique tab whose accessible title contains the exact session UUID, whose
        // content has one terminal pane, and whose live Codex process still owns the rollout file.
        // A generic title such as "PowerShell" is never a navigation witness.
        var terminals = TaskNavigationNative.Processes("WindowsTerminal");
        var candidates = new List<(IntPtr Window, int Process, AutomationElement Tab)>();
        foreach (var window in TaskNavigationNative.Windows())
        {
            token.ThrowIfCancellationRequested();
            var pid = TaskNavigationNative.WindowProcess(window);
            if (!terminals.ContainsKey(pid)) continue;
            var root = AutomationElement.FromHandle(window);
            var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            if (tabs.Count > 64) return "原终端页签过多，无法准确定位";
            foreach (AutomationElement tab in tabs)
            {
                token.ThrowIfCancellationRequested();
                if (ExactThreadTitle(tab.Current.Name, thread)) candidates.Add((window, pid, tab));
            }
        }
        if (candidates.Count != 1) return candidates.Count > 1
            ? "多个 Windows Terminal 页签包含此会话标识，无法唯一定位"
            : "Windows Terminal 未提供可验证的原会话页签标识";
        var candidate = candidates[0];
        // Split panes can contain separate sessions. Without an exact pane selector we must not
        // change the user's selected tab and then discover that the input focus is on another task.
        var panes = candidate.Tab.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "TermControl"));
        if (panes.Count != 1) return "无法唯一确认 Windows Terminal 原会话的终端窗格";
        if (!candidate.Tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)
            || !ExactThreadTitle(candidate.Tab.Current.Name, thread)
            || !TaskNavigationNative.Alive(owner) || !TaskNavigationNative.Alive(terminals[candidate.Process])
            || !HoldingFile(session.RolloutPath).Contains(owner.Pid)) return "原终端会话已变化，无法准确定位";
        token.ThrowIfCancellationRequested();
        if (TaskNavigationNative.IsIconic(candidate.Window)) TaskNavigationNative.ShowWindowAsync(candidate.Window, 9);
        token.ThrowIfCancellationRequested();
        if (!TaskNavigationNative.SetForegroundWindow(candidate.Window)
            || TaskNavigationNative.GetForegroundWindow() != candidate.Window) return "系统未允许激活原终端窗口";
        token.ThrowIfCancellationRequested();
        ((SelectionItemPattern)selection).Select();
        token.ThrowIfCancellationRequested();
        return TaskNavigationNative.GetForegroundWindow() == candidate.Window
            && ((SelectionItemPattern)selection).Current.IsSelected ? null : "无法确认原终端页签已选中";
    }

    private static bool ExactThreadTitle(string title, Guid thread) => Regex.IsMatch(title,
        $@"(?i)(?<![0-9a-f-]){Regex.Escape(thread.ToString("D"))}(?![0-9a-f-])");

    private sealed record ConsoleSnapshot(long Window, uint[] Members);

    private static async Task<ConsoleSnapshot?> InspectConsoleAsync(TaskNavigationNative.Identity owner, CancellationToken token)
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return null;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "CodexIsland.dll");
            if (!File.Exists(assemblyPath)) return null;
            start.ArgumentList.Add(assemblyPath);
        }
        start.ArgumentList.Add("--inspect-task-console");
        start.ArgumentList.Add(owner.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(owner.Started.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start);
        if (process is null) return null;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(token));
            if (process.ExitCode != 0 || output.Result.Length > 16384) return null;
            return JsonSerializer.Deserialize<ConsoleSnapshot>(output.Result);
        }
        catch (JsonException) { return null; }
        finally
        {
            if (!process.HasExited) try { process.Kill(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Private, read-only helper entry point, before the GUI/single-instance path in Main.</summary>
    internal static bool TryRunConsoleProbe(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--inspect-task-console") return false;
        if (args.Length != 3 || !int.TryParse(args[1], out var pid) || pid <= 0
            || !long.TryParse(args[2], out var started) || started <= 0)
        { exitCode = 2; return true; }
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks != started || !string.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase))
            { exitCode = 1; return true; }
            // Capture the redirected pipe before AttachConsole replaces standard handles. Nothing is
            // written to the attached console, and the probe never reads console input or output.
            using var output = Console.OpenStandardOutput();
            var snapshot = new ConsoleSnapshot(0, []);
            if (GetConsoleWindow() == IntPtr.Zero && AttachConsole((uint)pid))
            {
                try
                {
                    var members = new uint[512];
                    var count = GetConsoleProcessList(members, (uint)members.Length);
                    if (count > 0 && count <= members.Length && process.StartTime.ToUniversalTime().Ticks == started && !process.HasExited)
                        snapshot = new(GetConsoleWindow().ToInt64(), members.Take((int)count).ToArray());
                }
                finally { FreeConsole(); }
            }
            JsonSerializer.Serialize(output, snapshot);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        { exitCode = 1; }
        return true;
    }

    private static IReadOnlyList<int> HoldingFile(string file)
    {
        var key = new StringBuilder(33);
        if (RmStartSession(out var handle, 0, key) != 0) return [];
        try
        {
            // Restart Manager is used only as a read-only open-file inventory; no restart/shutdown APIs.
            if (RmRegisterResources(handle, 1, [file], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return [];
            uint required = 0, count = 0, reason = 0;
            var result = RmGetList(handle, out required, ref count, null, ref reason);
            if (result == 0) return [];
            if (result != 234 || required > 128) return [];
            var entries = new RmProcessInfo[required];
            count = required;
            if (RmGetList(handle, out required, ref count, entries, ref reason) != 0) return [];
            return entries.Take((int)count).Select(entry => entry.Process.Id).Distinct().ToArray();
        }
        finally { RmEndSession(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        internal int Id;
        internal System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        internal RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string ServiceName;
        internal uint ApplicationType, Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint fileCount, string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint handle, out uint needed, ref uint count,
        [In, Out] RmProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint handle);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint process);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
}
