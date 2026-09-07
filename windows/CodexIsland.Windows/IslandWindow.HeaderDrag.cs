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
    private bool headerFinishing;
    private bool previousHeaderClick, headerDoubleClick;

    internal bool IsHeaderDragging => headerGesture.IsDragging;
    internal bool IsHeaderPressed => headerGesture.IsPressed;

    private void HeaderPressed(object sender, MouseButtonEventArgs e)
    {
        if (BeginHeaderPress(NativeMethods.Pointer, e.ClickCount)) e.Handled = true;
    }

    internal bool BeginHeaderPress(PointD pointer, int clickCount = 1)
    {
        if (!interaction.IsExpanded || interaction.IsAdjusting || interaction.IsMenuOpen || dragging
            || headerGesture.IsPressed || !double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y)) return false;

        // Settle an in-flight hover animation before recording a stable physical-pixel anchor.
        ApplyState(false);
        var doubleClick = clickCount == 2 && previousHeaderClick;
        previousHeaderClick = false;
        if (!HeaderButton.CaptureMouse()) return false;
        headerDoubleClick = doubleClick;
        headerPressPointer = pointer;
        headerPressFrame = visibleFrame;
        headerGesture.Press(pointer);
        interaction.ResetPointerTracking();
        headerInputTimer.Start();
        return true;
    }

    private void HeaderPointerMoved(object sender, MouseEventArgs e)
    {
        if (!headerGesture.IsPressed || headerFinishing) return;
        if (e.LeftButton != MouseButtonState.Pressed) EndHeaderPress(NativeMethods.Pointer);
        else MoveHeaderPress(NativeMethods.Pointer);
        e.Handled = true;
    }

    private void TrackHeaderPress()
    {
        if (!headerGesture.IsPressed || headerFinishing) return;
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
            HeaderButton.Cursor = Cursors.SizeAll;
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
        var localPointer = HeaderButton.PointFromScreen(new(pointer.X, pointer.Y));
        var clickedHeader = new Rect(0, 0, HeaderButton.ActualWidth, HeaderButton.ActualHeight).Contains(localPointer);
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
                if (doubleClick) ResetPosition();
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
