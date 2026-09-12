namespace CodexIsland.Core;

/// <summary>Background polling with coalesced refreshes. Consumers marshal Changed to their UI context.</summary>
public sealed class TaskActivityStore(ITaskActivityClient client, bool isDemo = false) : IAsyncDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource lifetime = new();
    private CancellationTokenSource? schedule;
    private Task? activeRefresh;
    private Task? polling;
    private bool disposed;
    public bool IsDemo { get; } = isDemo;
    public IReadOnlyList<TaskActivity> Tasks { get; private set; } = isDemo ? DemoTasks() : [];
    public string ConnectionText { get; private set; } = isDemo ? "演示模式不读取设备任务" : "正在连接 Codex Desktop…";
    public bool IsConnected { get; private set; }
    public bool IsRefreshing { get; private set; }
    public int ActiveCount => Tasks.Count(task => task.Status != TaskActivityStatus.Unknown);
    public TaskActivity? FeaturedTask => Tasks.FirstOrDefault(task => task.Status == TaskActivityStatus.Waiting)
        ?? Tasks.FirstOrDefault(task => task.Status == TaskActivityStatus.Running) ?? Tasks.FirstOrDefault();
    public event Action? Changed;

    public void Start()
    {
        if (disposed || IsDemo || schedule is not null) return;
        schedule = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = schedule.Token;
        polling = Task.Run(() => PollAsync(token));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public Task RefreshAsync()
    {
        lock (sync)
        {
            if (disposed || IsDemo) return Task.CompletedTask;
            if (activeRefresh is not null) return activeRefresh;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activeRefresh = completed.Task;
            var token = lifetime.Token;
            _ = Task.Run(() => ReadAsync(completed, token));
            return completed.Task;
        }
    }

    private async Task ReadAsync(TaskCompletionSource completed, CancellationToken cancellationToken)
    {
        try
        {
            IsRefreshing = true;
            Changed?.Invoke();
            var snapshot = await client.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (disposed || cancellationToken.IsCancellationRequested) return;
            Tasks = snapshot.Tasks;
            ConnectionText = snapshot.ConnectionText;
            IsConnected = snapshot.IsConnected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (disposed || cancellationToken.IsCancellationRequested) return;
            Tasks = Tasks.Select(task => task with { Status = TaskActivityStatus.Unknown, Detail = "连接已中断，等待重新同步" }).ToArray();
            ConnectionText = "无法连接 Codex Desktop，正在重试";
            IsConnected = false;
        }
        finally
        {
            lock (sync) { activeRefresh = null; IsRefreshing = false; }
            completed.TrySetResult();
            if (!disposed) Changed?.Invoke();
        }
    }

    public Task<TaskNavigationContext?> RefreshNavigationAsync(TaskNavigationContext context, CancellationToken cancellationToken = default)
        => disposed || IsDemo ? Task.FromResult<TaskNavigationContext?>(null) : client.RefreshSideChatNavigationAsync(context, cancellationToken);

    public void Stop()
    {
        schedule?.Cancel(); schedule?.Dispose(); schedule = null;
        lifetime.Cancel();
        client.Stop();
        IsRefreshing = false;
        if (!disposed)
        {
            lifetime.Dispose(); lifetime = new();
            Tasks = Tasks.Select(task => task with { Status = TaskActivityStatus.Unknown, Detail = "任务连接已暂停，等待重新同步" }).ToArray();
            IsConnected = false;
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        if (polling is not null) await polling.ConfigureAwait(false);
        if (activeRefresh is { } pending) await pending.ConfigureAwait(false);
        await client.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private static TaskActivity[] DemoTasks() =>
    [
        new("demo:waiting", "检查并确认这次改动", "本机", TaskActivityStatus.Waiting, "等待你在 Codex Desktop 中批准操作"),
        new("demo:running", "同步 Windows 任务概览", "开发设备", TaskActivityStatus.Running, "计划步骤 2/5 · 完成任务状态与进度展示"),
        new("demo:unknown", "整理项目文档", "远端设备", TaskActivityStatus.Unknown, "任务来源已断开，等待重新同步")
    ];
}
