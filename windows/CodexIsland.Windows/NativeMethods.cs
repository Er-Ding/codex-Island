using System.Runtime.InteropServices;
using CodexIsland.Core;

namespace CodexIsland.Windows;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000,
        WsExTransparent = 0x20, WsExAppWindow = 0x40000;
    internal static readonly uint ShowMessage = RegisterWindowMessage("CodexIsland.Windows.Show.v1");
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);

    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly RectD ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice data, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint handle);

    internal static PointD Pointer => GetCursorPos(out var point) ? new(point.X, point.Y) : new(double.NaN, double.NaN);
    internal static RectD WindowRect(nint hwnd) => GetWindowRect(hwnd, out var rect) ? rect.ToRect() : default;
    internal static void SetFrame(nint hwnd, RectD frame) => SetWindowPos(hwnd, new nint(-1),
        (int)Math.Floor(frame.X), (int)Math.Floor(frame.Y), (int)Math.Ceiling(frame.Width), (int)Math.Ceiling(frame.Height), 0x0010 | 0x0200);

    internal static IReadOnlyList<DisplayInfo> Displays()
    {
        var displays = new List<DisplayInfo>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            var id = EnumDisplayDevices(info.Device, 0, ref device, 1) && !string.IsNullOrEmpty(device.Id) ? device.Id : info.Device;
            var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0 ? x / 96d : 1;
            displays.Add(new(id, info.Monitor.ToRect(), info.Work.ToRect(), dpi, (info.Flags & 1) != 0));
            return true;
        }, 0);
        if (displays.Count == 0) throw new InvalidOperationException("无法读取显示器信息。");
        return displays;
    }
}
