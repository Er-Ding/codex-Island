namespace CodexIsland.Core;

/// <summary>Pure hover state machine; caller supplies monotonic elapsed seconds.</summary>
public sealed class IslandInteraction
{
    public bool IsExpanded { get; private set; }
    public bool IsPinned { get; private set; }
    public bool IsAdjusting { get; private set; }
    public bool IsVisible { get; private set; } = true;
    public bool IsMenuOpen { get; private set; }
    private bool? lastInside;
    private bool suppressUntilExit;
    private (bool Expanded, double At)? pending;

    public bool ObservePointer(bool inside, double now)
    {
        if (!IsVisible || IsAdjusting || IsMenuOpen) { pending = null; return false; }
        if (suppressUntilExit)
        {
            if (!inside) { suppressUntilExit = false; lastInside = false; }
            return false;
        }
        if (IsPinned) { pending = null; lastInside = inside; return false; }
        if (lastInside != inside)
        {
            lastInside = inside;
            // Entry is immediate; only departure waits, so crossing an edge does not flash the island closed.
            pending = IsExpanded == inside ? null : (inside, inside ? now : now + 0.28);
        }
        if (pending is { } change && now >= change.At)
        {
            IsExpanded = change.Expanded;
            pending = null;
            return true;
        }
        return false;
    }

    public void ToggleHeader()
    {
        if (IsAdjusting) return;
        if (IsPinned) { Collapse(); return; }
        pending = null;
        suppressUntilExit = false;
        IsPinned = IsExpanded = true;
    }
    public void TogglePin()
    {
        if (IsAdjusting) return;
        pending = null;
        lastInside = null;
        IsPinned = !IsPinned;
        if (IsPinned) { IsExpanded = true; suppressUntilExit = false; }
    }
    public void Collapse()
    {
        pending = null;
        IsPinned = IsExpanded = false;
        suppressUntilExit = true;
    }
    public void SetMenuOpen(bool open)
    {
        IsMenuOpen = open;
        pending = null;
        lastInside = null;
    }
    public void BeginAdjustment() { Collapse(); IsAdjusting = true; IsVisible = true; }
    public void EndAdjustment() { IsAdjusting = false; Collapse(); }
    public void SetVisible(bool visible)
    {
        if (!visible) Collapse();
        IsVisible = visible;
        lastInside = null;
        if (visible) suppressUntilExit = false;
    }
}
