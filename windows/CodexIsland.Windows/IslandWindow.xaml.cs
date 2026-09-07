using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexIsland.Core;
using Microsoft.Win32;

namespace CodexIsland.Windows;

public partial class IslandWindow : Window
{
    private readonly QuotaStore store;
    private readonly SettingsStore settingsStore;
    private readonly bool enablePolling;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer hoverTimer, pollTimer, labelTimer;
    private readonly IslandInteraction interaction = new();
    private DisplayInfo display;
    private nint hwnd;
    private HwndSource? source;
    private RectD visibleFrame, targetFrame, canvasFrame, animationStart;
    private double centerX, topY, uiScale = 1, animationAt, animationDuration;
    private bool animating, wasExpanded, dragging, allowClose, environmentQueued;
    private PointD dragPointer;
    private RectD dragFrame;
    private DisplayInfo? adjustmentOrigin;
    private string? selectedBucketId, settingsError;
    private ContextMenu? bucketMenu;
    private QuotaSnapshot? renderedSnapshot;
    private string? renderedBucket;
    private List<CardViewModel> cardModels = [];

    public IslandSettings Settings { get; private set; }
    internal IslandInteraction Interaction => interaction;
    internal RectD VisibleFrame => visibleFrame;
    internal nint Handle => hwnd;
    internal double UiScale => uiScale;
    internal DisplayInfo Display => display;
    internal bool IsAnimating => animating;
    internal ContextMenu? BucketMenu => bucketMenu;
    public event Action? ExitRequested;
    public event Action? CodexPathChanged;

    public IslandWindow(QuotaStore store, SettingsStore settingsStore, bool initiallyExpanded = false, bool enablePolling = true)
    {
        this.store = store;
        this.settingsStore = settingsStore;
        this.enablePolling = enablePolling;
        Settings = settingsStore.Load();
        display = IslandPlacement.Select(NativeMethods.Displays(), Settings.Position?.DisplayId);
        InitializeComponent();
        // React on the input event; polling is only a fallback and handles leaving the island.
        MouseEnter += PointerEntered;
        MouseLeave += PointerLeft;
        headerInputTimer = new(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => TrackHeaderPress(), Dispatcher);
        headerInputTimer.Stop();
        if (initiallyExpanded) interaction.ToggleHeader();
        hoverTimer = new(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => CheckHover(), Dispatcher);
        pollTimer = new(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await store.RefreshAsync(scheduled: true), Dispatcher);
        labelTimer = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateDisplay(), Dispatcher);
        hoverTimer.Stop(); pollTimer.Stop(); labelTimer.Stop();
        SourceInitialized += (_, _) => InitializeNativeWindow();
        Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            // WPF forbids a second Close() while the Closing event is on the stack.
            Dispatcher.BeginInvoke(() => ExitRequested?.Invoke());
        };
        store.Changed += StoreChanged;
        UpdateDisplay();
    }

    private void InitializeNativeWindow()
    {
        hwnd = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WindowMessage);
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle,
            (style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow);
        RefreshEnvironment();
        hoverTimer.Start();
        if (enablePolling) { pollTimer.Start(); labelTimer.Start(); }
        SystemEvents.DisplaySettingsChanged += ScreenChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE, while still delivering the click.
        if ((uint)message == NativeMethods.ShowMessage) { Dispatcher.BeginInvoke(ShowIsland); handled = true; }
        if (message is 0x02E0 or 0x007E or 0x001A) QueueEnvironmentRefresh(); // DPI, displays, work area.
        return 0;
    }

    private void QueueEnvironmentRefresh()
    {
        if (environmentQueued || allowClose) return;
        environmentQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            environmentQueued = false;
            if (!dragging && !allowClose) RefreshEnvironment();
        });
    }
    private void ScreenChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(QueueEnvironmentRefresh);
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(async () =>
        {
            CancelHeaderPress();
            RefreshEnvironment();
            if (enablePolling) await store.RefreshAsync();
        });
    }

    internal void RefreshEnvironment()
    {
        if (hwnd == 0 || dragging || headerGesture.IsPressed) return;
        var displays = NativeMethods.Displays();
        display = IslandPlacement.Select(displays, interaction.IsAdjusting ? display.Id : Settings.Position?.DisplayId ?? display.Id);
        uiScale = IslandPlacement.Scale(display, CurrentSize);
        LayoutRoot.LayoutTransform = new ScaleTransform(uiScale, uiScale);
        bucketMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        if (!interaction.IsAdjusting)
        {
            var size = new SizeD(224 * uiScale * display.DpiScale, 36 * uiScale * display.DpiScale);
            var compact = Settings.Position?.Restore(display, size)
                ?? IslandPlacement.Clamp(new(display.WorkArea.CenterX - size.Width / 2, display.WorkArea.Y, size.Width, size.Height), display.WorkArea);
            centerX = compact.CenterX;
            topY = compact.Y;
        }
        ApplyState(animated: false);
    }

    internal IslandSize CurrentSize => Settings.DisplaySizes.GetValueOrDefault(display.Id, IslandSize.Automatic);
    private RectD DesiredFrame
    {
        get
        {
            var factor = uiScale * display.DpiScale;
            var width = (interaction.IsAdjusting ? 300 : interaction.IsExpanded ? 420 : 224) * factor;
            var height = (interaction.IsExpanded ? 336 : 36) * factor;
            return IslandPlacement.Clamp(new(centerX - width / 2, topY, width, height), display.WorkArea);
        }
    }

    internal void ApplyState(bool animated = true)
    {
        HeaderButton.Visibility = interaction.IsAdjusting ? Visibility.Collapsed : Visibility.Visible;
        AdjustmentHeader.Visibility = interaction.IsAdjusting ? Visibility.Visible : Visibility.Collapsed;
        IslandSurface.BorderBrush = Brush(interaction.IsAdjusting ? "#508F74" : "#282D31");
        PinButton.Foreground = Brush(interaction.IsPinned ? "#75E0B3" : "#A3AAAE");
        PinButton.ToolTip = interaction.IsPinned ? "取消固定" : "保持展开";
        if (wasExpanded != interaction.IsExpanded)
        {
            wasExpanded = interaction.IsExpanded;
            if (!wasExpanded) bucketMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            if (wasExpanded && enablePolling && (store.Snapshot is null || DateTimeOffset.UtcNow - store.Snapshot.FetchedAt > TimeSpan.FromSeconds(5)))
                _ = store.RefreshAsync();
        }
        if (hwnd == 0) return;
        var target = DesiredFrame;
        if (animated && animating && target == targetFrame) return;
        targetFrame = target;
        var start = visibleFrame.IsUsable ? visibleFrame : target;
        StopAnimation();
        if (animated && interaction.IsVisible && SystemParameters.ClientAreaAnimation && target != start)
        {
            animationStart = start;
            animationAt = clock.Elapsed.TotalSeconds;
            animationDuration = interaction.IsExpanded ? 0.18 : 0.16;
            SetCanvas(start.Union(target));
            animating = true;
            CompositionTarget.Rendering += RenderAnimation;
            RenderFrame(start);
        }
        else { SetCanvas(target); RenderFrame(target); }
        UpdateDisplay();
    }

    private void SetCanvas(RectD frame)
    {
        NativeMethods.SetFrame(hwnd, frame);
        canvasFrame = NativeMethods.WindowRect(hwnd);
    }
    private void RenderAnimation(object? sender, EventArgs e)
    {
        var progress = (clock.Elapsed.TotalSeconds - animationAt) / animationDuration;
        if (progress >= 1)
        {
            StopAnimation();
            SetCanvas(targetFrame);
            RenderFrame(targetFrame);
            CheckHover();
        }
        else RenderFrame(RectD.Interpolate(animationStart, targetFrame, progress));
    }
    private void StopAnimation()
    {
        CompositionTarget.Rendering -= RenderAnimation;
        animating = false;
    }

    private void RenderFrame(RectD frame)
    {
        visibleFrame = frame;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = frame.Width / dpi;
        var height = frame.Height / dpi;
        var expansion = Math.Clamp((height / uiScale - 36) / 300, 0, 1);
        var reveal = Math.Clamp(expansion / 0.75, 0, 1);
        Canvas.SetLeft(IslandSurface, (frame.X - canvasFrame.X) / dpi);
        Canvas.SetTop(IslandSurface, (frame.Y - canvasFrame.Y) / dpi);
        IslandSurface.Width = width;
        IslandSurface.Height = height;
        LayoutRoot.Width = Math.Max(1, (width - 2) / uiScale);
        CompactHeader.Width = Math.Max(1, LayoutRoot.Width - 10);
        var radius = Math.Min((17 + 8 * expansion) * uiScale, Math.Min(width, height) / 2);
        var topRadius = Math.Min(radius, Math.Max(0, frame.Y - display.WorkArea.Y) / dpi);
        IslandSurface.CornerRadius = new(topRadius, topRadius, radius, radius);
        IslandSurface.Clip = Outline(width, height, topRadius, radius);
        Details.Opacity = reveal * reveal * (3 - 2 * reveal);
        Details.IsHitTestVisible = interaction.IsExpanded && expansion > 0.9;
        UpdateClickThrough();
    }

    private static Geometry Outline(double width, double height, double top, double bottom)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new(top, 0), true, true);
            context.LineTo(new(width - top, 0), true, false);
            context.QuadraticBezierTo(new(width, 0), new(width, top), true, false);
            context.LineTo(new(width, height - bottom), true, false);
            context.QuadraticBezierTo(new(width, height), new(width - bottom, height), true, false);
            context.LineTo(new(bottom, height), true, false);
            context.QuadraticBezierTo(new(0, height), new(0, height - bottom), true, false);
            context.LineTo(new(0, top), true, false);
            context.QuadraticBezierTo(new(0, 0), new(top, 0), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void UpdateClickThrough()
    {
        if (hwnd == 0 || interaction.IsMenuOpen) return;
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        var transparent = animating && !dragging && !visibleFrame.Contains(NativeMethods.Pointer);
        var updated = transparent ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        if (style != updated) NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle, updated);
    }

    private void CheckHover()
    {
        if (!interaction.IsVisible || dragging || headerGesture.IsPressed) return;
        UpdateClickThrough();
        if (interaction.ObservePointer(visibleFrame.Union(targetFrame).Contains(NativeMethods.Pointer), clock.Elapsed.TotalSeconds)) ApplyState();
    }
    private void PointerEntered(object sender, MouseEventArgs e)
    {
        if (hwnd != 0 && interaction.IsVisible && !dragging && !headerGesture.IsPressed
            && interaction.ObservePointer(true, clock.Elapsed.TotalSeconds)) ApplyState();
    }
    private void PointerLeft(object sender, MouseEventArgs e)
    {
        // Record brief exits too, including an exit/reentry between two polling ticks.
        if (hwnd != 0 && interaction.IsVisible && !dragging && !headerGesture.IsPressed && !visibleFrame.Contains(NativeMethods.Pointer)
            && interaction.ObservePointer(false, clock.Elapsed.TotalSeconds)) ApplyState();
    }
    internal void SetMenuOpen(bool open) => interaction.SetMenuOpen(open);
    private void StoreChanged()
    {
        if (Dispatcher.CheckAccess()) UpdateDisplay();
        else Dispatcher.BeginInvoke(UpdateDisplay);
    }
    internal void QuotaNotification()
    {
        if (allowClose || !enablePolling) return;
        Dispatcher.BeginInvoke(async () =>
        {
            if (!allowClose && (store.Snapshot is null || DateTimeOffset.UtcNow - store.Snapshot.FetchedAt > TimeSpan.FromSeconds(5)))
                await store.RefreshAsync();
        });
    }

    internal void UpdateDisplay()
    {
        var snapshot = store.Snapshot;
        var bucket = snapshot?.Buckets.FirstOrDefault(b => b.Id == selectedBucketId) ?? snapshot?.MainBucket;
        var first = bucket?.Primary ?? bucket?.Secondary;
        var second = bucket?.Primary is null ? null : bucket.Secondary;
        FirstPeriod.Text = first?.PeriodTitle ?? "Codex";
        FirstPercent.Text = second is null ? "" : first?.PercentText ?? "—";
        FirstPercent.Foreground = QuotaBrush(first);
        SecondPeriod.Text = second?.PeriodTitle ?? "";
        SecondPercent.Text = (second ?? first)?.PercentText ?? "—";
        SecondPercent.Foreground = QuotaBrush(second ?? first);
        StaleDot.Visibility = store.IsStale && !store.IsDemo ? Visibility.Visible : Visibility.Collapsed;
        BucketTitle.Text = bucket?.Name ?? "Codex";
        BucketButton.ToolTip = bucket?.Name ?? "Codex";
        BucketArrow.Visibility = snapshot?.Buckets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PlanLabel.Text = store.IsDemo ? "演示数据" : snapshot?.PlanName ?? "账户额度";
        PlanLabel.Foreground = Brush(store.IsDemo ? "#FAB857" : "#A0A8AF");
        if (renderedSnapshot != snapshot || renderedBucket != bucket?.Id || cardModels.Count == 0)
        {
            renderedSnapshot = snapshot;
            renderedBucket = bucket?.Id;
            cardModels = (bucket?.Windows.Count > 0 ? bucket.Windows.Cast<QuotaWindow?>() : new QuotaWindow?[] { null })
                .Select(w => new CardViewModel(w)).ToList();
            Cards.ItemsSource = cardModels;
        }
        foreach (var card in cardModels) card.UpdateTime();
        var error = settingsError ?? store.ErrorMessage;
        StatusTitle.Text = store.IsDemo ? "演示模式" : store.IsRefreshing ? "正在更新额度…"
            : error is not null ? (snapshot is null ? "暂时无法读取" : "刷新未成功，显示上次数据")
            : store.IsStale ? "数据待更新" : "已同步账户额度";
        StatusDot.Fill = Brush(error is not null || store.IsStale ? "#FAB857" : "#75E0B3");
        ErrorLabel.Text = error ?? "";
        ErrorLabel.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorLabel.ToolTip = error;
        LastUpdated.Text = snapshot is not null ? "上次更新 " + snapshot.FetchedAt.ToLocalTime().ToString("HH:mm:ss")
            : error is null ? "正在连接 Windows 版 Codex" : "";
        RefreshButton.IsEnabled = !store.IsRefreshing;
    }

    internal static SolidColorBrush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
    internal static SolidColorBrush QuotaBrush(QuotaWindow? window) => Brush(window?.RemainingPercent switch
    {
        null => "#737D85", <= 10 => "#FF6D6D", <= 25 => "#FAB857", _ => "#75E0B3"
    });

    internal void ShowIsland()
    {
        interaction.SetVisible(true);
        Show();
        RefreshEnvironment();
        hoverTimer.Start();
    }
    internal void ToggleVisibility()
    {
        CancelHeaderPress();
        if (!interaction.IsVisible) { ShowIsland(); return; }
        if (interaction.IsAdjusting) CancelAdjustment();
        interaction.SetVisible(false);
        hoverTimer.Stop();
        StopAnimation();
        bucketMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        Hide();
    }

    internal void BeginAdjustment()
    {
        CancelHeaderPress();
        if (interaction.IsAdjusting) return;
        if (!interaction.IsVisible) ShowIsland();
        adjustmentOrigin = display;
        interaction.BeginAdjustment();
        ApplyState(false);
        centerX = visibleFrame.CenterX;
        topY = visibleFrame.Y;
    }
    internal void FinishAdjustment()
    {
        if (!interaction.IsAdjusting || dragging) return;
        var position = SavedPosition.Capture(visibleFrame, display);
        if (!TrySave(Settings with { Position = position })) return;
        interaction.EndAdjustment();
        RefreshEnvironment();
    }
    internal void CancelAdjustment()
    {
        if (!interaction.IsAdjusting) return;
        dragging = false;
        DragHandle.ReleaseMouseCapture();
        interaction.EndAdjustment();
        display = adjustmentOrigin ?? display;
        RefreshEnvironment();
    }
    internal void ResetPosition()
    {
        CancelHeaderPress();
        if (!TrySave(Settings with { Position = null })) return;
        if (interaction.IsAdjusting) interaction.EndAdjustment();
        interaction.Collapse();
        display = IslandPlacement.Select(NativeMethods.Displays(), null);
        RefreshEnvironment();
    }
    internal void SetSize(IslandSize size)
    {
        CancelHeaderPress();
        if (interaction.IsAdjusting) return;
        var sizes = new Dictionary<string, IslandSize>(Settings.DisplaySizes);
        if (size == IslandSize.Automatic) sizes.Remove(display.Id); else sizes[display.Id] = size;
        if (TrySave(Settings with { DisplaySizes = sizes })) RefreshEnvironment();
    }
    private bool TrySave(IslandSettings settings)
    {
        try { settingsStore.Save(settings); Settings = settings; settingsError = null; return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settingsError = "无法保存设置，请检查当前用户的本地应用数据目录。";
            UpdateDisplay();
            return false;
        }
    }

    internal void ChooseCodex()
    {
        var dialog = new OpenFileDialog { Title = "选择 Windows 版 codex.exe", Filter = "Codex 程序 (codex.exe)|codex.exe|可执行程序 (*.exe)|*.exe" };
        SetMenuOpen(true);
        try
        {
            if (dialog.ShowDialog() == true && TrySave(Settings with { CodexPath = dialog.FileName })) CodexPathChanged?.Invoke();
        }
        finally { SetMenuOpen(false); }
    }
    internal void AutoFindCodex()
    {
        if (TrySave(Settings with { CodexPath = null })) CodexPathChanged?.Invoke();
    }

    private void DragStarted(object sender, MouseButtonEventArgs e)
    {
        if (!interaction.IsAdjusting) return;
        StopAnimation();
        dragPointer = NativeMethods.Pointer;
        dragFrame = NativeMethods.WindowRect(hwnd);
        dragging = DragHandle.CaptureMouse();
        e.Handled = true;
    }
    private void DragMoved(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        var pointer = NativeMethods.Pointer;
        MoveDraft(new(dragFrame.X + pointer.X - dragPointer.X, dragFrame.Y + pointer.Y - dragPointer.Y, dragFrame.Width, dragFrame.Height));
        e.Handled = true;
    }
    internal void MoveDraft(RectD frame)
    {
        if (!interaction.IsAdjusting) return;
        NativeMethods.SetFrame(hwnd, frame);
        canvasFrame = NativeMethods.WindowRect(hwnd);
        visibleFrame = canvasFrame;
        centerX = visibleFrame.CenterX;
        topY = visibleFrame.Y;
        RenderFrame(visibleFrame);
    }
    private void DragEnded(object sender, MouseButtonEventArgs e) { if (dragging) { EndDrag(); e.Handled = true; } }
    private void DragCaptureLost(object sender, MouseEventArgs e) { if (dragging) EndDrag(); }
    private void EndDrag()
    {
        dragging = false;
        DragHandle.ReleaseMouseCapture();
        SettleDraft();
    }
    internal void SettleDraft()
    {
        if (!interaction.IsAdjusting) return;
        display = NativeMethods.Displays().OrderByDescending(d => d.Bounds.IntersectionArea(visibleFrame)).First();
        RefreshEnvironment();
        centerX = visibleFrame.CenterX;
        topY = visibleFrame.Y;
    }

    private void HeaderClicked(object sender, RoutedEventArgs e) { interaction.TogglePin(); ApplyState(); }
    private void PinClicked(object sender, RoutedEventArgs e) { interaction.TogglePin(); ApplyState(); }
    private void CollapseClicked(object sender, RoutedEventArgs e) { interaction.Collapse(); ApplyState(); }
    private void DoneClicked(object sender, RoutedEventArgs e) => FinishAdjustment();
    private void CancelClicked(object sender, RoutedEventArgs e) => CancelAdjustment();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await store.RefreshAsync();
    private void BucketClicked(object sender, RoutedEventArgs e)
    {
        if (store.Snapshot is not { Buckets.Count: > 1 } snapshot) return;
        if (bucketMenu is { IsOpen: true }) { bucketMenu.IsOpen = false; return; }
        bucketMenu = new ContextMenu
        {
            Style = (Style)FindResource("QuotaMenu"),
            LayoutTransform = new ScaleTransform(uiScale, uiScale),
            MaxWidth = Math.Max(1, (display.WorkArea.Width / display.DpiScale - 16) / uiScale),
            MaxHeight = Math.Min(420, (display.WorkArea.Height / display.DpiScale - 16) / uiScale),
            PlacementTarget = BucketButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 6
        };
        foreach (var bucket in snapshot.Buckets)
        {
            var item = new MenuItem
            {
                Header = bucket.Name, ToolTip = bucket.Name, IsCheckable = true, IsChecked = bucket.Id == renderedBucket,
                Style = (Style)FindResource("QuotaMenuItem")
            };
            item.Click += (_, _) =>
            {
                selectedBucketId = bucket.Id;
                bucketMenu.IsOpen = false;
                UpdateDisplay();
            };
            bucketMenu.Items.Add(item);
        }
        bucketMenu.Closed += (_, _) => SetMenuOpen(false);
        SetMenuOpen(true);
        bucketMenu.IsOpen = true;
    }

    internal void SavePreview(string path)
    {
        UpdateLayout();
        SaveVisual(HostCanvas, path);
    }
    internal void SaveMenuPreview(string path)
    {
        if (bucketMenu is not { IsOpen: true }) throw new InvalidOperationException("Open the quota menu before taking its preview.");
        bucketMenu.UpdateLayout();
        // Capture the popup root so its scale transform is included in the diagnostic image.
        SaveVisual((FrameworkElement)PresentationSource.FromVisual(bucketMenu).RootVisual, path);
    }
    private static void SaveVisual(FrameworkElement visual, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    internal void CloseForExit()
    {
        if (allowClose) return;
        CancelHeaderPress();
        allowClose = true;
        StopAnimation();
        hoverTimer.Stop(); pollTimer.Stop(); labelTimer.Stop();
        bucketMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        store.Changed -= StoreChanged;
        SystemEvents.DisplaySettingsChanged -= ScreenChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        source?.RemoveHook(WindowMessage);
        Close();
    }
}

internal sealed class CardViewModel(QuotaWindow? window) : INotifyPropertyChanged
{
    public string PeriodTitle => window?.PeriodTitle ?? "当前周期";
    public string PercentText => window?.PercentText ?? "—";
    public double Remaining => window?.RemainingPercent ?? 0;
    public SolidColorBrush Accent => IslandWindow.QuotaBrush(window);
    public string ResetLabel { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateTime()
    {
        var label = window?.ResetText(DateTimeOffset.UtcNow) ?? "等待账户数据";
        if (label == ResetLabel) return;
        ResetLabel = label;
        PropertyChanged?.Invoke(this, new(nameof(ResetLabel)));
    }
}
