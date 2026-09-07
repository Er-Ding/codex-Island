namespace CodexIsland.Core;

/// <summary>Call refresh on the UI context. Concurrent triggers coalesce into one read.</summary>
public sealed class QuotaStore(IQuotaClient client, bool isDemo = false, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly CancellationTokenSource lifetime = new();
    private int refreshing;
    private bool disposed;
    private TaskCompletionSource? idle;
    public QuotaSnapshot? Snapshot { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsRefreshing => Volatile.Read(ref refreshing) != 0;
    public bool IsDemo { get; } = isDemo;
    public int Failures { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public bool IsStale => ErrorMessage is not null || Snapshot is null || clock.GetUtcNow() - Snapshot.FetchedAt > TimeSpan.FromSeconds(90);
    public event Action? Changed;
    public Task WaitForIdleAsync() => idle?.Task ?? Task.CompletedTask;

    public void ClearSnapshot()
    {
        Snapshot = null;
        ErrorMessage = null;
        Failures = 0;
        NextAttempt = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

    public async Task RefreshAsync(bool scheduled = false)
    {
        if (disposed || (scheduled && clock.GetUtcNow() < NextAttempt)
            || Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle = completed;
        Changed?.Invoke();
        try
        {
            var latest = IsDemo ? QuotaSnapshot.Demo(clock.GetUtcNow()) : await client.FetchAsync(lifetime.Token);
            if (disposed) return;
            Snapshot = latest;
            ErrorMessage = null;
            Failures = 0;
            NextAttempt = clock.GetUtcNow().AddSeconds(30);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (disposed) return;
            ErrorMessage = e is QuotaException known ? known.Message : new QuotaException(QuotaError.Disconnected).Message;
            Failures = Math.Min(16, Failures + 1);
            NextAttempt = clock.GetUtcNow().AddSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(Failures, 4))));
            // Keep the previous snapshot, including its original fetched-at time.
        }
        finally
        {
            Interlocked.Exchange(ref refreshing, 0);
            completed.TrySetResult();
            if (!disposed) Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        await client.DisposeAsync();
        lifetime.Dispose();
    }
}
