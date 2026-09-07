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
