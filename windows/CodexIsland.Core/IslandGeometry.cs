namespace CodexIsland.Core;

// Desktop coordinates are physical pixels with y down. UI dimensions and saved
// top offsets are DIPs; conversion happens once, at the monitor boundary.
public readonly record struct PointD(double X, double Y);
public readonly record struct SizeD(double Width, double Height);
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public bool IsUsable => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
        && double.IsFinite(Height) && double.IsFinite(Right) && double.IsFinite(Bottom) && Width > 0 && Height > 0;
    public bool Contains(PointD point) => IsUsable && point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
    public RectD Union(RectD other)
    {
        if (!IsUsable) return other;
        if (!other.IsUsable) return this;
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        return new(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }
    public double IntersectionArea(RectD other) => Math.Max(0, Math.Min(Right, other.Right) - Math.Max(X, other.X))
        * Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y));
    public static RectD Interpolate(RectD start, RectD end, double progress)
    {
        var amount = 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
        double Mix(double a, double b) => a + (b - a) * amount;
        var width = Mix(start.Width, end.Width);
        return new(Mix(start.CenterX, end.CenterX) - width / 2, Mix(start.Y, end.Y), width, Mix(start.Height, end.Height));
    }
}

public sealed record DisplayInfo(string Id, RectD Bounds, RectD WorkArea, double DpiScale, bool IsPrimary);
public enum IslandSize { Automatic = 0, Percent100 = 100, Percent125 = 125, Percent150 = 150, Percent175 = 175, Percent200 = 200 }

public sealed record SavedPosition(string DisplayId, double HorizontalFraction, double TopOffset)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(DisplayId) && double.IsFinite(HorizontalFraction)
        && HorizontalFraction is >= 0 and <= 1 && double.IsFinite(TopOffset) && TopOffset >= 0;
    public static SavedPosition Capture(RectD frame, DisplayInfo display)
    {
        var bounded = IslandPlacement.Clamp(frame, display.WorkArea);
        return new(display.Id, (bounded.CenterX - display.WorkArea.X) / display.WorkArea.Width,
            Math.Max(0, bounded.Y - display.WorkArea.Y) / display.DpiScale);
    }
    public RectD Restore(DisplayInfo display, SizeD size)
    {
        var work = display.WorkArea;
        return IslandPlacement.Clamp(new(work.X + work.Width * (IsValid ? HorizontalFraction : 0.5) - size.Width / 2,
            work.Y + (IsValid ? TopOffset * display.DpiScale : 0), size.Width, size.Height), work);
    }
}

public static class IslandPlacement
{
    public static RectD Clamp(RectD frame, RectD work)
    {
        if (!work.IsUsable) return default;
        var width = double.IsFinite(frame.Width) ? Math.Clamp(frame.Width, 0, work.Width) : 0;
        var height = double.IsFinite(frame.Height) ? Math.Clamp(frame.Height, 0, work.Height) : 0;
        var x = double.IsFinite(frame.X) ? frame.X : work.CenterX - width / 2;
        var y = double.IsFinite(frame.Y) ? frame.Y : work.Y;
        return new(Math.Clamp(x, work.X, work.Right - width), Math.Clamp(y, work.Y, work.Bottom - height), width, height);
    }

    public static DisplayInfo Select(IReadOnlyList<DisplayInfo> displays, string? preferredId) =>
        displays.FirstOrDefault(d => d.Id == preferredId) ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

    public static double Scale(DisplayInfo display, IslandSize preference)
    {
        var width = display.WorkArea.Width / display.DpiScale;
        var height = display.WorkArea.Height / display.DpiScale;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return 1;
        var automatic = Math.Clamp(Math.Min(Math.Max(width, height) / 1920, Math.Min(width, height) / 1080), 1, 2);
        var requested = preference == IslandSize.Automatic ? automatic : (int)preference / 100d;
        return Math.Max(0.1, Math.Min(requested, Math.Min(width / 420, height / 336)));
    }
}
