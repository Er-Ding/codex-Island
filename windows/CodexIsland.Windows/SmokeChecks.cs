using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodexIsland.Core;

namespace CodexIsland.Windows;

/// <summary>Opt-in UI diagnostics use demo data and an isolated settings file.</summary>
internal static class SmokeChecks
{
    internal static async Task RunAsync(IslandWindow window, QuotaStore store, SettingsStore settings, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add("PASS: " + description);
        }
        void Invoke(string name)
        {
            var button = (Button)window.FindName(name);
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        }
        async Task Settle()
        {
            await Task.Delay(450);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        }
        // Freeze physical pointer polling so the user's mouse cannot change fixtures.
        window.Interaction.SetMenuOpen(true);
        window.ResetPosition();
        await Settle();
        var style = NativeMethods.GetWindowLong(window.Handle, NativeMethods.GwlExStyle);
        Check((style & NativeMethods.WsExNoActivate) != 0, "window uses WS_EX_NOACTIVATE");
        Check((style & NativeMethods.WsExToolWindow) != 0 && !window.ShowInTaskbar, "window stays out of the taskbar and Alt+Tab");
        Check(NativeMethods.GetForegroundWindow() != window.Handle, "showing the island does not take foreground focus");
        Check(Math.Abs(window.VisibleFrame.Height - 36 * window.UiScale * window.Display.DpiScale) < 2, "compact geometry respects monitor DPI");
        window.SavePreview(Path.Combine(output, "compact.png"));
        window.Interaction.SetMenuOpen(false);
        window.Interaction.ObservePointer(false, 0); // Clear the reset-position collapse suppression.
        window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        Check(window.Interaction.IsExpanded && !window.Interaction.IsPinned, "mouse entry expands in the input event without a timer or dwell");
        window.Interaction.SetMenuOpen(true);
        await Task.Delay(250);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Check(!window.IsAnimating && window.VisibleFrame.Height > 300 * window.Display.DpiScale, "hover expansion completes within 250 ms");
        Invoke("HeaderButton"); await Settle();
        Check(window.Interaction.IsExpanded && window.Interaction.IsPinned, "header automation invokes expand and pin");
        Check(!window.IsAnimating && window.VisibleFrame.Height > 300 * window.Display.DpiScale, "expansion animation completes");
        Check(NativeMethods.GetForegroundWindow() != window.Handle, "expanding preserves foreground focus");
        window.SavePreview(Path.Combine(output, "expanded.png"));
        Invoke("BucketButton"); await Settle();
        var menu = window.BucketMenu!;
        Check(menu.IsOpen && window.Interaction.IsMenuOpen, "quota selector opens and protects the island from hover collapse");
        Check(menu.Items.Count == store.Snapshot!.Buckets.Count, "quota menu exposes every available bucket");
        window.SaveMenuPreview(Path.Combine(output, "quota-menu.png"));
        var selectedItem = (MenuItem)menu.Items[1];
        ((IInvokeProvider)new MenuItemAutomationPeer(selectedItem).GetPattern(PatternInterface.Invoke)!).Invoke();
        await Settle();
        Check(!menu.IsOpen && !window.Interaction.IsMenuOpen, "choosing a quota closes the popup and releases hover protection");
        Check(((TextBlock)window.FindName("BucketTitle")).Text == store.Snapshot.Buckets[1].Name
            && ((ItemsControl)window.FindName("Cards")).Items.Count == store.Snapshot.Buckets[1].Windows.Count,
            "quota selection updates the heading and actual quota cards");
        window.SavePreview(Path.Combine(output, "expanded-selected.png"));
        window.Interaction.SetMenuOpen(true);
        Invoke("CollapseButton"); await Settle();
        Check(!window.Interaction.IsExpanded, "collapse automation invokes the actual button");
        window.BeginAdjustment();
        var origin = window.VisibleFrame;
        window.MoveDraft(origin with { X = origin.X + 70, Y = origin.Y + 90 });
        window.SettleDraft();
        window.SavePreview(Path.Combine(output, "adjustment.png"));
        window.CancelAdjustment();
        Check(settings.Load().Position is null, "cancel does not persist the draft position");
        window.BeginAdjustment();
        window.MoveDraft(window.VisibleFrame with { Y = window.VisibleFrame.Y + 100 });
        window.SettleDraft();
        Invoke("DoneButton"); await Settle();
        var saved = settings.Load().Position;
        Check(saved is { IsValid: true, TopOffset: > 0 }, "Done persists the position for a new settings store");
        var restored = window.VisibleFrame;
        window.RefreshEnvironment();
        Check(Math.Abs(restored.Y - window.VisibleFrame.Y) < 2, "environment refresh restores the saved top offset");
        window.SetSize(IslandSize.Percent150);
        Check(settings.Load().DisplaySizes.GetValueOrDefault(window.Display.Id) == IslandSize.Percent150, "display size persists independently of position");
        Invoke("HeaderButton"); await Settle();
        window.SavePreview(Path.Combine(output, "expanded-150.png"));
        Invoke("BucketButton"); await Settle();
        var scaledMenu = window.BucketMenu!;
        Check(Math.Abs(scaledMenu.LayoutTransform.Value.M11 - window.UiScale) < .01, "quota popup scales with the island at 150 percent");
        window.SaveMenuPreview(Path.Combine(output, "quota-menu-150.png"));
        scaledMenu.IsOpen = false;
        window.Interaction.SetMenuOpen(true);
        window.ToggleVisibility();
        Check(!window.IsVisible, "hide removes the native window");
        NativeMethods.PostMessage(window.Handle, NativeMethods.ShowMessage, 0, 0);
        await Settle();
        Check(window.IsVisible, "show restores the native window");
        Check(store.IsDemo && store.ErrorMessage is null, "UI diagnostics use explicit demo data");
        // Exercise capture/move/release synchronously: the very first changed coordinate must move,
        // before the dispatcher can run a timer. These checks do not move the user's pointer.
        window.ResetPosition();
        window.SetSize(IslandSize.Percent100);
        window.Interaction.SetMenuOpen(false);
        void Expand()
        {
            window.Interaction.ObservePointer(false, 0);
            window.Interaction.ObservePointer(true, 1);
            window.ApplyState(false);
            window.UpdateLayout();
        }
        PointD TopPoint()
        {
            var button = (Button)window.FindName("HeaderButton");
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            return new(point.X, point.Y);
        }
        Expand();
        var pressOrigin = window.VisibleFrame;
        var pressPoint = TopPoint();
        Check(window.BeginHeaderPress(pressPoint), "expanded header captures the pointer immediately on press");
        window.MoveHeaderPress(pressPoint);
        Check(window.VisibleFrame == pressOrigin && !window.IsHeaderDragging, "stationary input does not begin dragging");
        window.EndHeaderPress(pressPoint);
        Check(window.Interaction.IsPinned && !window.IsHeaderPressed && settings.Load().Position is null,
            "a stationary click pins without saving a position");
        window.BeginHeaderPress(pressPoint);
        window.EndHeaderPress(pressPoint);
        Check(!window.Interaction.IsPinned && window.Interaction.IsExpanded && window.VisibleFrame == pressOrigin,
            "single-click unpins without shrinking the double-click target");
        Check(window.BeginHeaderPress(pressPoint), "unpinned expanded header can start another drag");
        window.MoveHeaderPress(new(pressPoint.X + 1, pressPoint.Y));
        Check(window.IsHeaderDragging && Math.Abs(window.VisibleFrame.X - pressOrigin.X - 1) < .1,
            "the first one-pixel move starts dragging in the same input call with no timer or threshold");
        window.MoveHeaderPress(new(pressPoint.X + 90, pressPoint.Y + 100));
        Check(window.Interaction.IsExpanded && !window.Interaction.IsAdjusting,
            "immediate dragging preserves the expanded panel without adjustment mode");
        Check(Math.Abs(window.VisibleFrame.X - pressOrigin.X - 90) < 2 && Math.Abs(window.VisibleFrame.Y - pressOrigin.Y - 100) < 2,
            "header drag follows physical movement using the original press anchor");
        Check(settings.Load().Position is null, "dragging does not persist an unfinished move");
        window.EndHeaderPress(new(pressPoint.X + 90, pressPoint.Y + 100));
        var droppedFrame = window.VisibleFrame;
        Check(settings.Load().Position is { IsValid: true } && !window.IsHeaderPressed
            && window.Interaction.IsExpanded && !window.Interaction.IsPinned,
            "release automatically saves the position without toggling pinning");
        window.RefreshEnvironment();
        Check(Math.Abs(window.VisibleFrame.X - droppedFrame.X) < 2 && Math.Abs(window.VisibleFrame.Y - droppedFrame.Y) < 2,
            "dragged position survives a settings reload");
        var droppedSettings = settings.Load().Position;
        var nextPoint = TopPoint();
        window.BeginHeaderPress(nextPoint);
        window.MoveHeaderPress(new(nextPoint.X - 60, nextPoint.Y + 70));
        ((Button)window.FindName("HeaderButton")).ReleaseMouseCapture();
        Check(!window.IsHeaderPressed && window.VisibleFrame == droppedFrame && settings.Load().Position == droppedSettings,
            "capture loss cancels the draft and restores the saved position");
        nextPoint = TopPoint();
        window.BeginHeaderPress(nextPoint);
        window.EndHeaderPress(new(nextPoint.X + 30, nextPoint.Y));
        Check(!window.Interaction.IsPinned && Math.Abs(window.VisibleFrame.X - droppedFrame.X - 30) < 2
            && settings.Load().Position != droppedSettings,
            "even a quick release-only movement is a drag and saves automatically");
        var afterSwipe = settings.Load().Position;
        nextPoint = TopPoint();
        window.BeginHeaderPress(nextPoint, clickCount: 2);
        window.EndHeaderPress(nextPoint);
        Check(window.Interaction.IsPinned && settings.Load().Position == afterSwipe,
            "a click after a drag cannot masquerade as the second half of a reset double-click");

        foreach (var initiallyPinned in new[] { false, true })
        {
            window.SetSize(IslandSize.Percent125);
            Expand();
            if (window.Interaction.IsPinned != initiallyPinned) window.Interaction.TogglePin();
            window.ApplyState(false);
            window.UpdateLayout();
            var anchor = TopPoint();
            window.BeginHeaderPress(anchor);
            window.EndHeaderPress(new(anchor.X + 35, anchor.Y + 65));
            var beforeDouble = window.VisibleFrame;
            var header = (Button)window.FindName("HeaderButton");
            var leftEdge = header.PointToScreen(new Point(12, header.ActualHeight / 2));
            var doublePoint = new PointD(leftEdge.X, leftEdge.Y);
            window.BeginHeaderPress(doublePoint);
            window.EndHeaderPress(doublePoint);
            Check(window.Interaction.IsExpanded && window.VisibleFrame == beforeDouble
                && window.Interaction.IsPinned != initiallyPinned,
                $"first click on the expanded header edge preserves its geometry (initially pinned: {initiallyPinned})");
            window.BeginHeaderPress(doublePoint, clickCount: 2);
            window.EndHeaderPress(doublePoint);
            var primary = IslandPlacement.Select(NativeMethods.Displays(), null);
            Check(settings.Load().Position is null && !window.Interaction.IsExpanded && !window.Interaction.IsPinned
                && window.Display.Id == primary.Id && Math.Abs(window.VisibleFrame.CenterX - primary.WorkArea.CenterX) < 2
                && Math.Abs(window.VisibleFrame.Y - primary.WorkArea.Y) < 2,
                $"double-click restores the primary display's initial position (initially pinned: {initiallyPinned})");
            Check(settings.Load().DisplaySizes.GetValueOrDefault(primary.Id) == IslandSize.Percent125,
                "double-click resets position while preserving display size preferences");
        }
        Expand();
        var firstClick = TopPoint();
        window.BeginHeaderPress(firstClick);
        window.EndHeaderPress(firstClick);
        window.BeginHeaderPress(firstClick, clickCount: 2);
        window.EndHeaderPress(new(firstClick.X + 1, firstClick.Y));
        Check(window.Interaction.IsExpanded && window.Interaction.IsPinned && settings.Load().Position is not null,
            "movement during the second press takes precedence over double-click reset");
        nextPoint = TopPoint();
        window.BeginHeaderPress(nextPoint);
        window.MoveHeaderPress(new(nextPoint.X + 10000, nextPoint.Y + 10000));
        window.EndHeaderPress(new(nextPoint.X + 10000, nextPoint.Y + 10000));
        var boundedFrame = window.VisibleFrame;
        var workArea = window.Display.WorkArea;
        Check(boundedFrame.X >= workArea.X && boundedFrame.Y >= workArea.Y
            && boundedFrame.Right <= workArea.Right + 1 && boundedFrame.Bottom <= workArea.Bottom + 1,
            "release constrains the expanded island to the destination work area");
        Check(NativeMethods.GetForegroundWindow() != window.Handle, "dragging and double-click reset do not take foreground focus");
        window.Interaction.SetMenuOpen(true);
        if (Environment.GetEnvironmentVariable("CODEX_ISLAND_NATIVE_INPUT_TEST") == "1")
            await NativeDragSmoke.RunAsync(window, settings, output);
        var closeProbe = new IslandWindow(store, settings, enablePolling: false);
        var closeHandled = false;
        closeProbe.Interaction.SetMenuOpen(true);
        closeProbe.ExitRequested += () => { closeProbe.CloseForExit(); closeHandled = true; };
        closeProbe.Show();
        closeProbe.Close();
        AssertNotReentrant();
        await Settle();
        Check(closeHandled && !closeProbe.IsVisible, "window close defers application shutdown until Closing has returned");
        void AssertNotReentrant()
        {
            if (closeHandled) throw new InvalidOperationException("Exit was requested inside the Closing event.");
        }
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Append($"{checks.Count} UI checks passed."));
        Console.WriteLine($"{checks.Count} UI checks passed. Previews: {output}");
    }
}
