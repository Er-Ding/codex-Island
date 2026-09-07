namespace CodexIsland.Core;

public enum HeaderRelease { None, Click, Drag }

// The caller supplies physical desktop coordinates. A held press becomes a
// drag on its first position change, without a dwell time or distance threshold.
public sealed class HeaderDragGesture
{
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }

    private PointD origin;

    public void Press(PointD pointer)
    {
        Cancel();
        origin = pointer;
        IsPressed = true;
    }

    public bool Move(PointD pointer)
    {
        if (!IsPressed || IsDragging || pointer == origin) return false;
        IsDragging = true;
        return true;
    }

    public HeaderRelease Release(PointD pointer)
    {
        if (!IsPressed) return HeaderRelease.None;
        Move(pointer);
        var result = IsDragging ? HeaderRelease.Drag : HeaderRelease.Click;
        Cancel();
        return result;
    }

    public void Cancel()
    {
        IsPressed = IsDragging = false;
        origin = default;
    }
}
