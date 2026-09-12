using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexIsland.Core;

namespace CodexIsland.Windows;

public partial class IslandWindow
{
    private readonly TaskActivityStore activity;
    private readonly bool ownsActivity;
    private CancellationTokenSource? taskNavigationCancellation;
    private string? openingTaskId, taskNavigationMessage;
    private IReadOnlyList<TaskActivity> renderedTasks = [];

    internal TaskActivityStore Activity => activity;
    internal bool IsOpeningTask => openingTaskId is not null;

    private void ActivityChanged()
    {
        if (allowClose) return;
        if (Dispatcher.CheckAccess()) UpdateTasks();
        else Dispatcher.BeginInvoke(() => { if (!allowClose) UpdateTasks(); });
    }

    private void UpdateTasks()
    {
        var tasks = activity.Tasks;
        var featured = activity.FeaturedTask;
        var unconfirmed = !activity.IsConnected || tasks.Any(t => t.Status == TaskActivityStatus.Unknown);
        var count = activity.ActiveCount;
        var countText = count > 99 ? "99+" : count > 0 ? count.ToString() : unconfirmed ? "—" : "0";
        var accent = TaskAccent(featured?.Status ?? TaskActivityStatus.Unknown);
        TaskBadgeCount.Text = openingTaskId is not null ? "…" : countText;
        TaskBadgeCount.Foreground = TaskBadgeDot.Fill = accent;
        TaskBadge.ToolTip = featured is null ? activity.ConnectionText + "\n展开查看任务"
            : $"{countText} 个任务 · {featured.Title}\n{featured.DeviceName} · {TaskStatusText(featured.Status)}\n点击打开对应会话；按住移动可拖动";
        AutomationProperties.SetName(TaskBadge, (string)TaskBadge.ToolTip);
        TaskTotal.Text = countText;
        TaskConnection.Text = taskNavigationMessage ?? activity.ConnectionText;
        TaskConnection.ToolTip = TaskConnection.Text;
        TaskConnection.Foreground = Brush(openingTaskId is not null ? "#75E0B3"
            : taskNavigationMessage is not null || !activity.IsConnected ? "#FAB857" : "#98A3AA");
        TaskEmptyText.Text = activity.IsDemo ? "演示模式不读取设备任务"
            : activity.IsConnected ? "已加载会话中暂无进行中的任务"
            : activity.IsRefreshing ? "正在连接任务来源…" : "请保持 Codex Desktop 运行";
        TaskEmpty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskScroll.Visibility = tasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskRows.IsEnabled = openingTaskId is null;
        if (!renderedTasks.SequenceEqual(tasks))
        {
            renderedTasks = tasks.ToArray();
            TaskRows.ItemsSource = tasks.Select(t => new TaskRowViewModel(t)).ToArray();
        }
    }

    private void OpenTaskList()
    {
        interaction.Expand();
        ApplyState();
    }

    private async void TaskClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TaskRowViewModel row }) await OpenTaskAsync(row.Task);
        e.Handled = true;
    }

    internal async Task RefreshAllAsync() => await Task.WhenAll(store.RefreshAsync(), activity.RefreshAsync());

    private async Task OpenTaskAsync(TaskActivity task)
    {
        if (allowClose || openingTaskId is not null) return;
        if (activity.IsDemo)
        {
            taskNavigationMessage = "演示任务不会打开实际会话";
            UpdateTasks();
            return;
        }
        taskNavigationCancellation?.Cancel();
        taskNavigationCancellation?.Dispose();
        var cancellation = taskNavigationCancellation = new CancellationTokenSource();
        openingTaskId = task.Id;
        taskNavigationMessage = "正在定位会话…";
        UpdateTasks();
        try
        {
            // The caller freezes the task on mouse-down. Only missing navigation metadata is refreshed.
            if (task.Navigation is { IsSideConversation: true, SideTabTitle: null } context
                && await activity.RefreshNavigationAsync(context, cancellation.Token) is { } navigation)
                task = task with { Navigation = navigation };
            cancellation.Token.ThrowIfCancellationRequested();
            var target = DesktopThreadId(task.Navigation);
            var hosts = activity.Tasks.Where(t => DesktopThreadId(t.Navigation) == target)
                .Select(t => t.Navigation?.HostId).Where(h => h is not null).ToHashSet();
            if (task.Navigation is { } current) hosts.Add(current.HostId);
            taskNavigationMessage = await TaskNavigator.OpenAsync(task, hosts.Count > 1, cancellation.Token);
        }
        catch (OperationCanceledException) { taskNavigationMessage = "已取消定位会话"; }
        catch (Exception) { taskNavigationMessage = "暂时无法定位此会话，请在原应用中打开"; }
        finally
        {
            openingTaskId = null;
            if (!allowClose) UpdateTasks();
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation.Token);
            if (!allowClose && taskNavigationCancellation == cancellation)
            {
                taskNavigationMessage = null;
                UpdateTasks();
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string? DesktopThreadId(TaskNavigationContext? context)
    {
        return context?.SideChatParentThreadId ?? context?.ThreadId;
    }

    private void StopTaskInteraction()
    {
        taskNavigationCancellation?.Cancel();
        activity.Changed -= ActivityChanged;
        if (ownsActivity) _ = activity.DisposeAsync();
    }

    internal static SolidColorBrush TaskAccent(TaskActivityStatus status) => Brush(status switch
    {
        TaskActivityStatus.Running => "#75E0B3", TaskActivityStatus.Waiting => "#FAB857", _ => "#98A3AA"
    });
    internal static string TaskStatusText(TaskActivityStatus status) => status switch
    {
        TaskActivityStatus.Running => "进行中 ↗", TaskActivityStatus.Waiting => "等待操作 ↗", _ => "待同步 ↗"
    };
}

internal sealed record TaskRowViewModel(TaskActivity Task)
{
    public string Title => string.IsNullOrWhiteSpace(Task.Title) ? "未命名任务" : Task.Title;
    public string DeviceName => string.IsNullOrWhiteSpace(Task.DeviceName) ? "未知设备" : Task.DeviceName;
    public string Detail => Task.Detail;
    public string StatusText => IslandWindow.TaskStatusText(Task.Status);
    public SolidColorBrush Accent => IslandWindow.TaskAccent(Task.Status);
    public string HelpText => $"{Title}\n{DeviceName} · {StatusText}\n{Detail}\n点击定位此会话";
}
