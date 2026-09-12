using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexIsland.Windows;

internal static class TaskNavigationNative
{
    internal readonly record struct Identity(int Pid, int Parent, long Started, string Path);

    internal static Dictionary<int, Identity> Processes(string name)
    {
        var parents = Parents();
        var result = new Dictionary<int, Identity>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var path = ProcessPath(process.Id);
                    if (path is not null) result[process.Id] = new(process.Id, parents.GetValueOrDefault(process.Id),
                        process.StartTime.ToUniversalTime().Ticks, path);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }

    internal static bool Alive(Identity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.Started
                && string.Equals(ProcessPath(identity.Pid), identity.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static bool DescendsFrom(Identity child, Identity root, IReadOnlyDictionary<int, Identity> all)
    {
        var visited = new HashSet<int>();
        for (var i = 0; i < 32 && visited.Add(child.Pid); i++)
        {
            if (child == root) return true;
            if (!all.TryGetValue(child.Parent, out child)) return false;
        }
        return false;
    }

    internal static string? ProcessPath(int pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    internal static int WindowProcess(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var process);
        return unchecked((int)process);
    }

    internal static bool IsDesktopWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
        var path = ProcessPath(WindowProcess(window));
        return path is not null && string.Equals(System.IO.Path.GetFileName(path), "Codex.exe", StringComparison.OrdinalIgnoreCase)
            && (path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                || File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "resources", "app.asar")));
    }

    internal static IReadOnlyList<IntPtr> Windows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) => { if (IsWindowVisible(window)) windows.Add(window); return true; }, IntPtr.Zero);
        return windows;
    }

    private static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (Process32First(snapshot, ref entry))
                do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
                while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal UIntPtr DefaultHeapId;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
}
