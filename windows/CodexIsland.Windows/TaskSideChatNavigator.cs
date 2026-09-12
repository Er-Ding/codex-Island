using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexIsland.Windows;

internal static class TaskSideChatNavigator
{
    private const string Missing = "父会话已请求打开，但未能定位侧边聊天页签，请在 Desktop 中手动选择";
    private const string FocusChanged = "窗口焦点已变化，已停止定位侧边聊天，请再次点击任务";
    private static readonly HashSet<string> RevealNames = ["Show tabs", "显示标签页"];

    internal static async Task<string> OpenAsync(Guid parent, string title, CancellationToken cancellationToken)
    {
        title = Normalize(title);
        if (title.Length == 0) return "此侧边聊天缺少页签名称，暂时无法定位";
        var initialWindow = TaskNavigationNative.GetForegroundWindow();
        if (!TaskNavigator.TryOpenDesktop(parent, cancellationToken)) return "无法打开 Codex Desktop，请确认应用可正常启动";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            // UIA can wait for another process. Keep it off the WPF dispatcher; late reads cannot
            // cause a selection because every action rechecks this linked cancellation token.
            return await Task.Run(() => SelectAsync(title, initialWindow, deadline.Token), deadline.Token)
                .WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) { return cancellationToken.IsCancellationRequested ? "已取消打开会话" : Missing; }
        catch (NavigationFocusChangedException) { return FocusChanged; }
        catch (Exception error) when (error is ElementNotAvailableException or ElementNotEnabledException
            or COMException or InvalidOperationException)
        { return "父会话已请求打开，但无法读取可访问页签，请在 Desktop 中手动选择"; }
        finally { deadline.Cancel(); }
    }

    private static async Task<string> SelectAsync(string title, IntPtr initialWindow, CancellationToken token)
    {
        IntPtr window = IntPtr.Zero;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(100, token);
            var foreground = TaskNavigationNative.GetForegroundWindow();
            if (TaskNavigationNative.IsDesktopWindow(foreground)) { window = foreground; break; }
            if (foreground != initialWindow && foreground != IntPtr.Zero) return FocusChanged;
        }
        if (window == IntPtr.Zero) return FocusChanged;
        var process = TaskNavigationNative.WindowProcess(window);
        await Task.Delay(200, token);
        string? previousTab = null;
        int[]? previousReveal = null;
        var revealed = false;
        while (true)
        {
            RequireFocus(window, process, token);
            var scan = Scan(window, title, token);
            if (scan.Ambiguous) return "父会话已请求打开，但有多个同名侧边聊天页签，无法唯一定位，请手动选择";
            if (scan.AmbiguousReveal) return "父会话已请求打开，但无法唯一确定显示标签页按钮，请手动展开侧栏后重试";
            if (scan.Tab is { } tab && scan.Document is { } document)
            {
                previousReveal = null;
                var identifier = tab.Current.AutomationId;
                if (previousTab == identifier)
                {
                    // A second complete scan proves uniqueness after the parent route has settled.
                    if (!OwnedBy(tab, document, token) || Normalize(tab.Current.Name) != title) { previousTab = null; continue; }
                    RequireFocus(window, process, token);
                    if (Selected(tab)) return "已选中对应的侧边聊天页签";
                    if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                        return "父会话已请求打开，但此版本未提供页签选择接口，请手动选择";
                    RequireFocus(window, process, token);
                    ((SelectionItemPattern)selection).Select();
                    for (var confirmation = 0; confirmation < 8; confirmation++)
                    {
                        await Task.Delay(100, token);
                        RequireFocus(window, process, token);
                        var verified = Scan(window, title, token);
                        if (verified.Ambiguous) return "父会话已请求打开，但有多个同名侧边聊天页签，请手动选择";
                        if (verified.Tab is { } current && current.Current.AutomationId == identifier && Selected(current))
                            return "已选中对应的侧边聊天页签";
                    }
                    return "父会话已请求打开，但尚未确认侧边聊天页签已选中，请手动检查";
                }
                previousTab = identifier;
            }
            else if (!revealed && scan.Reveal is { } reveal && scan.Document is { } root)
            {
                previousTab = null;
                var identity = reveal.GetRuntimeId();
                if (previousReveal is not null && identity.SequenceEqual(previousReveal)
                    && OwnedBy(reveal, root, token) && RevealNames.Contains(Normalize(reveal.Current.Name))
                    && IsUnpressed(reveal) && reveal.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    RequireFocus(window, process, token);
                    ((InvokePattern)invoke).Invoke();
                    revealed = true;
                }
                previousReveal = identity;
            }
            else { previousTab = null; previousReveal = null; }
            await Task.Delay(150, token);
        }
    }

    private sealed record ScanResult(AutomationElement? Document = null, AutomationElement? Tab = null,
        AutomationElement? Reveal = null, bool Ambiguous = false, bool AmbiguousReveal = false);

    private static ScanResult Scan(IntPtr window, string title, CancellationToken token)
    {
        var root = MainDocument(AutomationElement.FromHandle(window), token);
        if (root is null) return new();
        var matches = new List<AutomationElement>();
        var reveals = new List<AutomationElement>();
        // Ask the provider for controls directly; walking every text node in a long conversation
        // would exhaust the deadline before reaching the sidebar. Ownership checks below exclude
        // controls in embedded browser documents even when their labels happen to match.
        var controls = root.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));
        if (controls.Count >= 512) return new(); // Incomplete/oversized scans cannot establish uniqueness.
        foreach (AutomationElement node in controls)
        {
            token.ThrowIfCancellationRequested();
            var type = node.Current.ControlType;
            if (type == ControlType.TabItem || type == ControlType.RadioButton)
            {
                var id = node.Current.AutomationId;
                if (MatchesTab(id, node.Current.Name, title, true) && OwnedBy(node, root, token)) matches.Add(node);
            }
            if ((type == ControlType.Button || type == ControlType.CheckBox)
                && RevealNames.Contains(Normalize(node.Current.Name)) && OwnedBy(node, root, token)) reveals.Add(node);
            if (matches.Count > 1) return new(root, Ambiguous: true);
        }
        if (matches.Count == 1) return new(root, matches[0]);
        return reveals.Count > 1 ? new(root, AmbiguousReveal: true) : new(root, Reveal: reveals.SingleOrDefault());
    }

    private static AutomationElement? MainDocument(AutomationElement window, CancellationToken token)
    {
        var pending = new Stack<(AutomationElement Node, int Depth)>();
        pending.Push((window, 0));
        AutomationElement? found = null;
        var visited = 0;
        var walker = TreeWalker.ControlViewWalker;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 128 || item.Depth > 12) return null;
            if (item.Node.Current.ControlType == ControlType.Document)
            {
                // Chromium exposes the document URL through ValuePattern on Windows. If it is
                // available, require the app shell origin rather than an embedded browser document.
                if (item.Node.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                    && ((ValuePattern)value).Current.Value is { Length: > 0 } url
                    && !url.StartsWith("app://-/index.html", StringComparison.Ordinal)) continue;
                if (found is not null) return null;
                found = item.Node;
                continue;
            }
            for (var child = walker.GetFirstChild(item.Node); child is not null; child = walker.GetNextSibling(child))
            {
                token.ThrowIfCancellationRequested();
                if (visited + pending.Count >= 128) return null;
                pending.Push((child, item.Depth + 1));
            }
        }
        return found;
    }

    private static bool OwnedBy(AutomationElement element, AutomationElement document, CancellationToken token)
    {
        for (var count = 0; count < 64; count++)
        {
            token.ThrowIfCancellationRequested();
            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            if (parent is null) return false;
            if (parent.Current.ControlType == ControlType.Document) return Automation.Compare(parent, document);
            element = parent;
        }
        return false;
    }

    private static bool Selected(AutomationElement element) => element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
        && ((SelectionItemPattern)pattern).Current.IsSelected;

    private static bool IsUnpressed(AutomationElement element) => element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
        && ((TogglePattern)pattern).Current.ToggleState == ToggleState.Off;

    internal static bool MatchesTab(string automationId, string accessibleName, string expectedTitle, bool hasTabRole)
        => hasTabRole && automationId.StartsWith("app-shell-tab-", StringComparison.Ordinal)
            && automationId.Length > "app-shell-tab-".Length
            && !automationId.StartsWith("app-shell-tab-panel-", StringComparison.Ordinal)
            && Normalize(expectedTitle).Length > 0 && Normalize(accessibleName) == Normalize(expectedTitle);

    private static void RequireFocus(IntPtr window, int process, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TaskNavigationNative.GetForegroundWindow() != window || TaskNavigationNative.WindowProcess(window) != process)
            throw new NavigationFocusChangedException();
    }

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private sealed class NavigationFocusChangedException : InvalidOperationException { }
}
