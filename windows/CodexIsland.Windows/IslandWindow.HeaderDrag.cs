using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexIsland.Core;

namespace CodexIsland.Windows;

public partial class IslandWindow
{
    private readonly HeaderDragGesture headerGesture = new();
    private readonly DispatcherTimer headerInputTimer;
    private PointD headerPressPointer;
    private RectD headerPressFrame;
    private RectD headerOriginalHitFrame;
    private bool headerFinishing;
    private bool previousHeaderClick, headerDoubleClick;
    private bool headerTaskPress, headerCancellationEventsAttached;
    private TaskActivity? headerPressedTask;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    internal bool IsHeaderDragging => headerGesture.IsDragging;
    internal bool IsHeaderPressed => headerGesture.IsPressed;

    private void HeaderPressed(object sender, MouseButtonEventArgs e)
    {
        var pointer = NativeMethods.Pointer;
        // Freeze the target before expansion or a live task refresh changes the badge.
        var taskPress = TaskBadge.IsVisible && ScreenFrame(TaskBadge).Contains(pointer);
        var pressedTask = taskPress ? activity.FeaturedTask : null;
        if (!BeginHeaderPress(pointer, e.ClickCount)) return;
        headerTaskPress = taskPress;
        headerPressedTask = pressedTask;
        if (taskPress) headerDoubleClick = false;
        e.Handled = true;
    }

    internal bool BeginHeaderPress(PointD pointer, int clickCount = 1)
    {
        if (!interaction.IsVisible || interaction.IsAdjusting || interaction.IsMenuOpen || dragging
            || headerGesture.IsPressed || !double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y)) return false;

        if (!headerCancellationEventsAttached)
        {
            HeaderButton.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(HeaderOtherButtonPressed), true);
            HeaderButton.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(HeaderKeyPressed), true);
            headerCancellationEventsAttached = true;
        }
        var doubleClick = clickCount == 2 && previousHeaderClick;
        previousHeaderClick = false;
        headerTaskPress = false;
        headerPressedTask = null;
        headerOriginalHitFrame = ScreenFrame(HeaderButton);
        if (!HeaderButton.CaptureMouse()) return false;
        headerDoubleClick = doubleClick;
        headerPressPointer = pointer;
        // Own this same press before expansion can change layout or dispatch hover events.
        headerGesture.Press(pointer);
        interaction.Expand();
        ApplyState(false);
        HeaderButton.UpdateLayout();
        headerPressFrame = visibleFrame;
        interaction.ResetPointerTracking();
        headerInputTimer.Start();
        return true;
    }

    private static RectD ScreenFrame(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new(0, 0));
        var bottomRight = element.PointToScreen(new(element.ActualWidth, element.ActualHeight));
        return new(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private void HeaderOtherButtonPressed(object sender, MouseButtonEventArgs e)
    {
        if (headerGesture.IsPressed && e.ChangedButton != MouseButton.Left) CancelHeaderPress();
    }

    private void HeaderKeyPressed(object sender, KeyEventArgs e)
    {
        if (!headerGesture.IsPressed || e.Key != Key.Escape) return;
        CancelHeaderPress();
        e.Handled = true;
    }

    private bool CancelHeaderFromInput()
    {
        // A non-activating island cannot depend on receiving keyboard focus. Read key state
        // only while its own press is captured; no global hook or focus change is necessary.
        if ((GetAsyncKeyState(0x1B) & 0x8000) == 0 && (GetAsyncKeyState(0x02) & 0x8000) == 0
            && (GetAsyncKeyState(0x04) & 0x8000) == 0 && (GetAsyncKeyState(0x05) & 0x8000) == 0
            && (GetAsyncKeyState(0x06) & 0x8000) == 0) return false;
        CancelHeaderPress();
        return true;
    }

    private void HeaderPointerMoved(object sender, MouseEventArgs e)
    {
        if (!headerGesture.IsPressed || headerFinishing) return;
        if (CancelHeaderFromInput()) { e.Handled = true; return; }
        if (e.LeftButton != MouseButtonState.Pressed) EndHeaderPress(NativeMethods.Pointer);
        else MoveHeaderPress(NativeMethods.Pointer);
        e.Handled = true;
    }

    private void TrackHeaderPress()
    {
        if (!headerGesture.IsPressed || headerFinishing) return;
        if (CancelHeaderFromInput()) return;
        if (!HeaderButton.IsMouseCaptured)
        {
            CancelHeaderPress();
            return;
        }
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndHeaderPress(NativeMethods.Pointer);
            return;
        }
        MoveHeaderPress(NativeMethods.Pointer);
    }

    internal void MoveHeaderPress(PointD pointer)
    {
        if (!headerGesture.IsPressed || !double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y)) return;
        if (headerGesture.Move(pointer))
        {
            dragging = true;
            headerDoubleClick = false;
            HeaderButton.Cursor = Cursors.ScrollAll;
            IslandSurface.BorderBrush = Brush("#508F74");
        }
        if (!headerGesture.IsDragging) return;

        var frame = headerPressFrame with
        {
            X = headerPressFrame.X + pointer.X - headerPressPointer.X,
            Y = headerPressFrame.Y + pointer.Y - headerPressPointer.Y
        };
        // Do not constrain motion to the starting monitor: the user can drag across displays.
        SetCanvas(frame);
        targetFrame = canvasFrame;
        centerX = canvasFrame.CenterX;
        topY = canvasFrame.Y;
        RenderFrame(canvasFrame);
    }

    private void HeaderReleased(object sender, MouseButtonEventArgs e)
    {
        if (!headerGesture.IsPressed) return;
        if (CancelHeaderFromInput()) { e.Handled = true; return; }
        EndHeaderPress(NativeMethods.Pointer);
        e.Handled = true; // A completed drag must never bubble into Button.Click.
    }

    internal void EndHeaderPress(PointD pointer)
    {
        if (!headerGesture.IsPressed || headerFinishing) return;
        if (!double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y)) { CancelHeaderPress(); return; }
        headerFinishing = true;
        try { CompleteHeaderPress(pointer); }
        finally { headerFinishing = false; }
    }

    private void CompleteHeaderPress(PointD pointer)
    {
        MoveHeaderPress(pointer);
        var release = headerGesture.Release(pointer);
        var doubleClick = headerDoubleClick;
        var taskPress = headerTaskPress;
        var pressedTask = headerPressedTask;
        var localPointer = HeaderButton.PointFromScreen(new(pointer.X, pointer.Y));
        var clickedHeader = new Rect(0, 0, HeaderButton.ActualWidth, HeaderButton.ActualHeight).Contains(localPointer)
            || headerOriginalHitFrame.Contains(pointer);
        headerTaskPress = false;
        headerPressedTask = null;
        ReleaseHeaderCapture();
        if (release == HeaderRelease.Drag)
        {
            display = NativeMethods.Displays().OrderByDescending(d => d.Bounds.IntersectionArea(visibleFrame)).First();
            uiScale = IslandPlacement.Scale(display, CurrentSize);
            LayoutRoot.LayoutTransform = new ScaleTransform(uiScale, uiScale);
            var settled = DesiredFrame;
            centerX = settled.CenterX;
            topY = settled.Y;
            ApplyState(false);
            if (!TrySave(Settings with { Position = SavedPosition.Capture(visibleFrame, display) })) RefreshEnvironment();
        }
        else
        {
            RefreshEnvironment();
            if (release == HeaderRelease.Click && clickedHeader)
            {
                if (taskPress)
                {
                    if (pressedTask is not null) _ = OpenTaskAsync(pressedTask);
                    else OpenTaskList();
                }
                else if (doubleClick) ResetPosition();
                else
                {
                    // Keep the header in place after the first click, including when unpinning,
                    // so a double-click on either end of the expanded header can complete.
                    interaction.TogglePin();
                    ApplyState();
                    previousHeaderClick = true;
                }
            }
        }
        interaction.ResetPointerTracking();
    }

    private void HeaderCaptureLost(object sender, MouseEventArgs e)
    {
        if (headerGesture.IsPressed) CancelHeaderPress();
    }

    internal void CancelHeaderPress()
    {
        previousHeaderClick = headerDoubleClick = false;
        headerTaskPress = false;
        headerPressedTask = null;
        if (!headerGesture.IsPressed) return;
        headerGesture.Cancel();
        ReleaseHeaderCapture();
        // Capture loss cancels the draft and restores the saved anchor, without writing settings.
        RefreshEnvironment();
        interaction.ResetPointerTracking();
    }

    private void ReleaseHeaderCapture()
    {
        headerInputTimer.Stop();
        dragging = false;
        HeaderButton.ClearValue(CursorProperty);
        HeaderButton.ReleaseMouseCapture();
        IslandSurface.BorderBrush = Brush("#282D31");
    }
}
