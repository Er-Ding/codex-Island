using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexIsland.Core;

public interface IQuotaClient : IAsyncDisposable
{
    event Action? QuotaChanged;
    Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Only initialize and account/rateLimits/read are exposed. No auth files or conversations are read.</summary>
public sealed class QuotaClient(Func<string?>? executable = null, TimeSpan? requestTimeout = null,
    IReadOnlyList<string>? processArguments = null) : IQuotaClient
{
    private readonly SemaphoreSlim fetchGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Connection? connection;
    private bool disposed;
    public event Action? QuotaChanged;

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await fetchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (connection is null || !connection.IsAlive)
            {
                connection?.Dispose();
                var path = executable?.Invoke() ?? (executable is null ? CodexLocator.Find() : null);
                if (path is null) throw new QuotaException(QuotaError.MissingCodex);
                connection = new(path, processArguments ?? ["app-server", "--listen", "stdio://"],
                    requestTimeout ?? TimeSpan.FromSeconds(20), () => QuotaChanged?.Invoke());
                await connection.RequestAsync("initialize", new
                {
                    clientInfo = new { name = "codex_island_windows", title = "Codex Island", version = "0.4.1" }
                }, linked.Token).ConfigureAwait(false);
                await connection.SendAsync(new { method = "initialized", @params = new { } }, linked.Token).ConfigureAwait(false);
            }
            var result = await connection.RequestAsync("account/rateLimits/read", null, linked.Token).ConfigureAwait(false);
            return QuotaParser.Parse(result.GetRawText(), DateTimeOffset.UtcNow);
        }
        catch (Exception e)
        {
            connection?.Dispose();
            connection = null;
            if (e is QuotaException or OperationCanceledException) throw;
            throw new QuotaException(QuotaError.Disconnected);
        }
        finally { fetchGate.Release(); }
    }

    public async Task RestartAsync()
    {
        await fetchGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { connection?.Dispose(); connection = null; }
        finally { fetchGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await fetchGate.WaitAsync().ConfigureAwait(false);
        try { connection?.Dispose(); connection = null; }
        finally { fetchGate.Release(); }
        lifetime.Dispose();
    }

    private sealed class Connection : IDisposable
    {
        private readonly Process process;
        private readonly ProcessJob? job;
        private readonly CancellationTokenSource stop = new();
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
        private readonly TimeSpan timeout;
        private readonly Action changed;
        private int nextId;
        private int dead;
        private int disposedConnection;
        public bool IsAlive => Volatile.Read(ref dead) == 0 && !process.HasExited;

        public Connection(string path, IReadOnlyList<string> arguments, TimeSpan timeout, Action changed)
        {
            this.timeout = timeout;
            this.changed = changed;
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = new() { StartInfo = info };
            try { process.Start(); }
            catch { process.Dispose(); throw new QuotaException(QuotaError.Disconnected); }
            job = ProcessJob.TryAttach(process);
            _ = ReadAsync();
            _ = DrainErrorsAsync();
        }

        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                // A read can end just before a request is registered.
                if (!IsAlive) throw new QuotaException(QuotaError.Disconnected);
                await SendAsync(new { id, method, @params = parameters }, token).ConfigureAwait(false);
                try { return await completion.Task.WaitAsync(timeout, token).ConfigureAwait(false); }
                catch (TimeoutException) { throw new QuotaException(QuotaError.TimedOut); }
            }
            finally { pending.TryRemove(id, out _); }
        }

        public async Task SendAsync(object value, CancellationToken token)
        {
            var line = JsonSerializer.Serialize(value);
            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
            finally { writeGate.Release(); }
        }

        private async Task ReadAsync()
        {
            var failure = new QuotaException(QuotaError.Disconnected);
            try
            {
                var buffer = new JsonLineBuffer();
                var bytes = new byte[8192];
                while (await process.StandardOutput.BaseStream.ReadAsync(bytes, stop.Token).ConfigureAwait(false) is var count && count > 0)
                    foreach (var line in buffer.Append(bytes.AsSpan(0, count))) await ReceiveAsync(line).ConfigureAwait(false);
            }
            catch (QuotaException e) { failure = e; }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                Interlocked.Exchange(ref dead, 1);
                foreach (var entry in pending) entry.Value.TrySetException(failure);
            }
        }

        private async Task ReceiveAsync(byte[] line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { return; }
            using (document)
            {
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object) return;
                var hasId = message.TryGetProperty("id", out var id);
                if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number)
                    && !message.TryGetProperty("method", out _) && pending.TryRemove(number, out var waiter))
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        var text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                            && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                        waiter.TrySetException(QuotaException.FromServer(text));
                    }
                    else if (message.TryGetProperty("result", out var result)) waiter.TrySetResult(result.Clone());
                    else waiter.TrySetException(new QuotaException(QuotaError.InvalidResponse));
                }
                else if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
                {
                    if (hasId)
                        await SendAsync(new { id = id.Clone(), error = new { code = -32601, message = "Quota viewer does not support this method" } }, stop.Token).ConfigureAwait(false);
                    else if (method.GetString() == "account/rateLimits/updated") changed();
                }
            }
        }

        private async Task DrainErrorsAsync()
        {
            // Drain the pipe without persisting or displaying raw upstream output.
            try
            {
                var buffer = new byte[4096];
                while (await process.StandardError.BaseStream.ReadAsync(buffer, stop.Token).ConfigureAwait(false) > 0) { }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposedConnection, 1) != 0) return;
            Interlocked.Exchange(ref dead, 1);
            stop.Cancel();
            foreach (var entry in pending) entry.Value.TrySetException(new QuotaException(QuotaError.Disconnected));
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            job?.Dispose();
            process.Dispose();
            stop.Dispose();
        }
    }
}
