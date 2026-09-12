using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodexIsland.Core;

/// <summary>
/// Observes streams already loaded by Desktop through its existing Windows named pipe.
/// It never starts a router, resumes a task, sends input, or opens an SSH connection.
/// Desktop 26.908 uses codex-ipc, four-byte little endian JSON frames, and stream version 11.
/// </summary>
public sealed class TaskActivityClient : ITaskActivityClient
{
    private const int MaximumFrameSize = 32 * 1024 * 1024;
    private const int MaximumStoredBytes = 16 * 1024 * 1024;
    private const int MaximumSubscriptions = 256;
    private const int MaximumMetadataCandidates = 8192;
    private const int StreamVersion = 11;
    private readonly object sync = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly string codexHome;
    private readonly string pipeName;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<ThreadKey, string> candidates = [];
    private readonly HashSet<ThreadKey> preferredCandidates = [];
    private readonly HashSet<ThreadKey> locatedCandidates = [];
    private readonly HashSet<ThreadKey> subscriptions = [];
    private readonly Dictionary<ThreadKey, SubscriptionProbe> probes = [];
    private readonly Dictionary<ThreadKey, DateTimeOffset> lastProbed = [];
    private readonly Dictionary<ThreadKey, ThreadStream> streams = [];
    private readonly Dictionary<ThreadKey, TaskActivity> activities = [];
    private readonly Dictionary<ThreadKey, DateTimeOffset> deferred = [];
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal) { ["local"] = "本机" };
    private readonly Dictionary<string, string> clientTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sshAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> remoteHosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> confirmedHosts = new(StringComparer.Ordinal);
    private NamedPipeClientStream? pipe;
    private CancellationTokenSource? connectionLifetime;
    private Channel<JsonObject>? outbound;
    private string? clientId, initializeId;
    private DateTimeOffset discoveryDate, connectedAt;
    private bool protocolMismatch, sourcesSeen, limitedCoverage, disposed;
    private int generation;
    private Task[] connectionTasks = [];

    private readonly record struct ThreadKey(string Host, string Thread)
    {
        public string Id => Host + ":" + Thread;
    }
    private sealed record ThreadStream(string Owner, int Revision, JsonObject State, int Bytes, string? SideTabTitle);
    private sealed record SubscriptionProbe(DateTimeOffset StartedAt, DateTimeOffset RetryAt, int Attempts, bool AwaitingSnapshot);
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public TaskActivityClient(string? codexHome = null, string pipeName = "codex-ipc")
        : this(codexHome, pipeName, TimeProvider.System) { }

    internal TaskActivityClient(string? codexHome, string pipeName, TimeProvider timeProvider)
    {
        this.codexHome = codexHome ?? TaskJson.Nonempty(Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim())
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        this.pipeName = pipeName;
        this.timeProvider = timeProvider;
    }

    public async Task<TaskActivitySnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int attemptGeneration;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                DiscoverThreadsIfNeeded();
                if (pipe is not null && clientId is null && Now - connectedAt > TimeSpan.FromSeconds(5)) Disconnect();
                if (pipe is not null) { MaintainSubscriptions(); return Snapshot(); }
                attemptGeneration = generation;
            }
            var nextPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            try { await nextPipe.ConnectAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                nextPipe.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync) return Snapshot();
            }
            lock (sync)
            {
                if (disposed || attemptGeneration != generation) { nextPipe.Dispose(); return Snapshot(); }
                pipe = nextPipe;
                connectedAt = Now;
                protocolMismatch = false;
                var lifetime = connectionLifetime = new CancellationTokenSource();
                var queue = outbound = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(1024)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
                initializeId = Guid.NewGuid().ToString("D");
                Queue(new JsonObject
                {
                    ["type"] = "request", ["requestId"] = initializeId, ["sourceClientId"] = "initializing-client",
                    ["version"] = 0, ["method"] = "initialize", ["params"] = new JsonObject { ["clientType"] = "codex-island" }
                });
                connectionTasks = [ReadLoopAsync(nextPipe, lifetime.Token), WriteLoopAsync(nextPipe, queue.Reader, lifetime.Token)];
            }
        }
        finally { connectionGate.Release(); }
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        lock (sync) return Snapshot();
    }

    public async Task<TaskNavigationContext?> RefreshSideChatNavigationAsync(TaskNavigationContext context, CancellationToken cancellationToken = default)
    {
        var key = new ThreadKey(context.HostId, context.ThreadId);
        lock (sync)
        {
            if (disposed || clientId is null || !streams.ContainsKey(key)) return null;
            Subscribe(key, refresh: true, priority: true);
        }
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        lock (sync) return streams.TryGetValue(key, out var stream) ? NavigationContext(key, stream.State) : null;
    }

    private async Task ReadLoopAsync(NamedPipeClientStream source, CancellationToken cancellationToken)
    {
        try
        {
            var lengthBytes = new byte[4];
            while (true)
            {
                await source.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length <= 0 || length > MaximumFrameSize) throw new JsonException("Task frame size is unsupported");
                var bytes = new byte[length];
                await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                var message = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 256 }) as JsonObject
                    ?? throw new JsonException("Task frame must be an object");
                lock (sync)
                {
                    if (pipe != source) return;
                    Handle(message);
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException)
        {
            lock (sync)
            {
                if (pipe != source) return;
                if (error is JsonException or InvalidOperationException) protocolMismatch = true;
                Disconnect();
            }
        }
    }

    private async Task WriteLoopAsync(NamedPipeClientStream destination, ChannelReader<JsonObject> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
                if (payload.Length > MaximumFrameSize) throw new IOException("Task frame exceeds limit");
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            lock (sync) if (pipe == destination) Disconnect();
        }
    }

    private void Queue(JsonObject message)
    {
        if (outbound is not null && !outbound.Writer.TryWrite(message)) Disconnect();
    }

    private void Handle(JsonObject message)
    {
        var type = message["type"].Text();
        if (type == "client-discovery-request" && message["requestId"].Text() is { } requestId)
        {
            Queue(new JsonObject { ["type"] = "client-discovery-response", ["requestId"] = requestId, ["response"] = new JsonObject { ["canHandle"] = false } });
            return;
        }
        if (type == "response" && initializeId is not null && message["requestId"].Text() == initializeId)
        {
            if (message["resultType"].Text() == "success" && message["result"] is JsonObject result && result["clientId"].Text() is { } id)
            {
                clientId = id;
                initializeId = null;
                SubscribeCandidates();
            }
            else { protocolMismatch = true; Disconnect(); }
            return;
        }
        if (type != "broadcast" || message["params"] is not JsonObject parameters || message["method"].Text() is not { } method) return;
        if (message["targetClientIds"] is JsonArray targets && clientId is not null && !targets.Any(target => target.Text() == clientId)) return;
        if (method == "ipc-connection-reset") { Disconnect(); return; }
        if (method == "client-status-changed" && parameters["clientId"].Text() is { } owner)
        {
            if (parameters["status"].Text() == "connected" && TaskJson.Nonempty(parameters["clientType"].Text()) is { } ownerType)
            {
                clientTypes[owner] = ownerType;
                foreach (var key in streams.Where(entry => entry.Value.Owner == owner).Select(entry => entry.Key).ToArray()) UpdateNavigation(key);
            }
            else if (parameters["status"].Text() == "disconnected")
            {
                clientTypes.Remove(owner);
                foreach (var key in streams.Where(entry => entry.Value.Owner == owner).Select(entry => entry.Key).ToArray())
                {
                    streams.Remove(key);
                    MarkUnknown(key, "任务来源已断开，等待重新同步");
                    if (!streams.Keys.Any(candidate => candidate.Host == key.Host)) confirmedHosts.Remove(key.Host);
                }
            }
            return;
        }
        if (TaskJson.Nonempty(parameters["conversationId"].Text()) is not { } thread || TaskJson.Nonempty(parameters["hostId"].Text()) is not { } host) return;
        if (!Guid.TryParseExact(thread, "D", out _) || host.Length > 512 || host.Any(char.IsControl)) return;
        var threadKey = new ThreadKey(host, thread);
        switch (method)
        {
            case "thread-stream-following-status-requested":
            case "thread-stream-following-changed":
                if (message["version"].Integer() != 1 || method == "thread-stream-following-changed" && !parameters["following"].Boolean()) return;
                candidates.TryAdd(threadKey, "");
                Subscribe(threadKey, refresh: method == "thread-stream-following-status-requested", priority: true);
                break;
            case "thread-stream-state-changed":
                if (TaskJson.Nonempty(message["sourceClientId"].Text()) is null || parameters["change"] is not JsonObject) return;
                if (message["version"].Integer() != StreamVersion)
                {
                    protocolMismatch = true;
                    MarkUnknown(threadKey, "Desktop 任务协议已更新，暂时无法读取");
                    return;
                }
                // An existing owner may broadcast before any local index contains the thread.
                // Joining its stream is read-only; a patch without a baseline requests a snapshot.
                candidates.TryAdd(threadKey, "");
                Subscribe(threadKey, priority: true);
                if (!subscriptions.Contains(threadKey)) return;
                ReceiveState(parameters, threadKey, message["sourceClientId"].Text() ?? "");
                break;
            case "thread-archived":
                activities.Remove(threadKey);
                streams.Remove(threadKey);
                Unsubscribe(threadKey);
                break;
        }
    }

    private void Subscribe(ThreadKey key, bool refresh = false, bool priority = false)
    {
        if (clientId is null || !refresh && subscriptions.Contains(key)) return;
        // A live owner announcement takes precedence over the historical-probe cooldown.
        if (priority) deferred.Remove(key);
        else if (deferred.TryGetValue(key, out var retry) && retry > Now) return;
        if (!subscriptions.Contains(key) && subscriptions.Count >= MaximumSubscriptions)
        {
            limitedCoverage = true;
            if (!priority) return;
            var unused = subscriptions.Where(candidate => !activities.ContainsKey(candidate)).OrderBy(candidate => streams.ContainsKey(candidate)).Cast<ThreadKey?>().FirstOrDefault();
            if (unused is not { } retired) return;
            streams.Remove(retired);
            deferred[retired] = Now.AddSeconds(60);
            Unsubscribe(retired);
        }
        if (subscriptions.Add(key)) lastProbed[key] = Now;
        var previous = probes.GetValueOrDefault(key);
        var attempts = Math.Min((previous?.Attempts ?? 0) + 1, 5);
        probes[key] = new(previous?.StartedAt ?? Now, Now.AddSeconds(Math.Min(30, 3 * (1 << (attempts - 1)))), attempts, true);
        Following(key, true);
    }

    private void Unsubscribe(ThreadKey key)
    {
        probes.Remove(key);
        if (subscriptions.Remove(key) && clientId is not null) Following(key, false);
    }

    private void SubscribeCandidates()
    {
        foreach (var key in candidates.Keys.OrderByDescending(activities.ContainsKey)
            .ThenBy(key => lastProbed.GetValueOrDefault(key, DateTimeOffset.MinValue))
            .ThenByDescending(locatedCandidates.Contains).ThenByDescending(preferredCandidates.Contains)
            .ThenByDescending(key => key.Thread, StringComparer.Ordinal).ThenBy(key => key.Host, StringComparer.Ordinal).ToArray()) Subscribe(key);
    }

    private void MaintainSubscriptions()
    {
        if (clientId is null) return;
        foreach (var key in deferred.Where(entry => entry.Value <= Now).Select(entry => entry.Key).ToArray()) deferred.Remove(key);
        // Large histories must not permanently occupy every slot ahead of older running tasks.
        if (subscriptions.Count >= MaximumSubscriptions && candidates.Keys.Any(key => !subscriptions.Contains(key) && !deferred.ContainsKey(key)))
        {
            foreach (var key in subscriptions.Where(key => !activities.ContainsKey(key)
                && probes.TryGetValue(key, out var probe) && Now - probe.StartedAt >= TimeSpan.FromSeconds(15)).ToArray())
            {
                streams.Remove(key);
                deferred[key] = Now.AddSeconds(60);
                Unsubscribe(key);
            }
        }
        SubscribeCandidates();
        foreach (var key in subscriptions.Where(key => probes.TryGetValue(key, out var probe)
            && (!streams.ContainsKey(key) || probe.AwaitingSnapshot) && probe.RetryAt <= Now).ToArray()) Subscribe(key, refresh: true);
    }
    private void Following(ThreadKey key, bool value) => Queue(new JsonObject
    {
        ["type"] = "broadcast", ["method"] = "thread-stream-following-changed", ["version"] = 1, ["sourceClientId"] = clientId,
        ["params"] = new JsonObject { ["conversationId"] = key.Thread, ["hostId"] = key.Host, ["following"] = value }
    });

    private void ReceiveState(JsonObject parameters, ThreadKey key, string owner)
    {
        if (owner.Length == 0 || parameters["change"] is not JsonObject change || change["revision"].Integer() is not { } revision) return;
        streams.TryGetValue(key, out var current);
        JsonObject summary;
        string? sideTabTitle;
        if (change["type"].Text() == "snapshot" && change["conversationState"] is JsonObject state)
        {
            if (current?.Owner == owner && current.Revision > revision) return;
            if (probes.TryGetValue(key, out var probe)) probes[key] = probe with { AwaitingSnapshot = false, Attempts = 0 };
            sideTabTitle = (current?.Owner == owner ? current.SideTabTitle : null) ?? TaskSideChatTitle.From(state);
            summary = TaskActivityProjection.Summary(state);
        }
        else if (change["type"].Text() == "patches")
        {
            if (current is null || current.Owner != owner || change["baseRevision"].Integer() != current.Revision || change["patches"] is not JsonArray patches)
            {
                MarkUnknown(key, "正在重新同步任务进展"); Subscribe(key, refresh: true); return;
            }
            try
            {
                var next = TaskActivityProjection.Apply(current.State, patches);
                sideTabTitle = current.SideTabTitle ?? TaskSideChatTitle.From(next);
                summary = TaskActivityProjection.Summary(next);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                MarkUnknown(key, "正在重新同步任务进展"); Subscribe(key, refresh: true); return;
            }
        }
        else return;
        var bytes = Encoding.UTF8.GetByteCount(summary.ToJsonString()) + Encoding.UTF8.GetByteCount(sideTabTitle ?? "");
        streams[key] = new(owner, revision, summary, bytes, sideTabTitle);
        sourcesSeen = true;
        confirmedHosts.Add(key.Host);
        ProjectActivity(summary, key);
        TrimHistory(key);
    }

    private void ProjectActivity(JsonObject state, ThreadKey key)
    {
        UpdateNavigation(key);
        if (state["resumeState"].Text() is "needs_resume" or "resuming" || (state["threadRuntimeStatus"] as JsonObject)?["type"].Text() == "notLoaded")
        { MarkUnknown(key, "任务来源待同步，请检查 Desktop 中的设备连接"); return; }
        var activity = TaskActivityDecoder.Decode(state, key.Id, names.GetValueOrDefault(key.Host, key.Host == "local" ? "本机" : "远端设备"), candidates.GetValueOrDefault(key));
        if (activity is not null) activities[key] = activity with { Navigation = NavigationContext(key, state) };
        else if (TaskActivityDecoder.HasConfirmedEnd(state)) activities.Remove(key);
        else MarkUnknown(key, "正在等待任务来源确认状态");
    }

    private TaskNavigationContext NavigationContext(ThreadKey key, JsonObject state)
    {
        var ownerType = streams.TryGetValue(key, out var stream) ? clientTypes.GetValueOrDefault(stream.Owner) : null;
        var parent = TaskJson.Nonempty(state["sideConversationParentNavigationPath"].Text());
        return new(key.Thread, key.Host, ownerType, TaskJson.Nonempty(state["source"].Text()), TaskJson.Nonempty(state["cwd"].Text()),
            TaskJson.Nonempty(state["rolloutPath"].Text()), key.Host == "local" ? null : sshAliases.GetValueOrDefault(key.Host),
            state["sideConversation"].Boolean() || parent is not null, parent, stream?.SideTabTitle);
    }

    private void UpdateNavigation(ThreadKey key)
    {
        if (activities.TryGetValue(key, out var task) && streams.TryGetValue(key, out var stream))
            activities[key] = task with { Navigation = NavigationContext(key, stream.State), DeviceName = names.GetValueOrDefault(key.Host, task.DeviceName) };
    }

    private void MarkUnknown(ThreadKey key, string detail)
    {
        if (activities.TryGetValue(key, out var task)) activities[key] = task with { Status = TaskActivityStatus.Unknown, Detail = detail };
    }

    private TaskActivitySnapshot Snapshot()
    {
        var connected = clientId is not null && sourcesSeen && !protocolMismatch;
        var text = protocolMismatch ? "Desktop 任务接口暂不兼容，请更新后重试"
            : clientId is null ? "请打开 Codex Desktop，任务连接会自动恢复"
            : !sourcesSeen ? "已连接任务通道，等待 Desktop 已加载任务同步"
            : limitedCoverage || activities.Values.Any(task => task.Status == TaskActivityStatus.Unknown) ? "Desktop 已加载任务 · 部分任务待同步"
            : remoteHosts.Except(confirmedHosts).Any() ? "Desktop 已加载任务 · 部分远端尚未同步"
            : "Desktop 已加载任务 · 自动同步";
        var tasks = activities.Values.OrderBy(task => task.Status switch { TaskActivityStatus.Waiting => 0, TaskActivityStatus.Running => 1, _ => 2 }).ThenBy(task => task.Id, StringComparer.Ordinal).ToArray();
        return new(tasks, text, connected);
    }

    private void DiscoverThreadsIfNeeded()
    {
        if (Now - discoveryDate < TimeSpan.FromSeconds(15)) return;
        discoveryDate = Now;
        var found = new Dictionary<ThreadKey, string>();
        preferredCandidates.Clear();
        locatedCandidates.Clear();
        try
        {
            using var file = new FileStream(Path.Combine(codexHome, "session_index.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - 256 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            foreach (var line in reader.ReadToEnd().Split('\n').TakeLast(160))
            {
                try { if (JsonNode.Parse(line) is JsonObject value && TaskJson.Nonempty(value["id"].Text()) is { } id) found[new("local", id)] = value["thread_name"].Text() ?? ""; }
                catch (JsonException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(codexHome, "thread-writer-locks"), "*.lock").Take(4096))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (Guid.TryParse(id, out _))
                {
                    var key = new ThreadKey("local", id);
                    found.TryAdd(key, "");
                    preferredCandidates.Add(key);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try
        {
            using var file = new FileStream(Path.Combine(codexHome, ".codex-global-state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length <= 16 * 1024 * 1024 && JsonNode.Parse(file) is JsonObject state)
            {
                remoteHosts.Clear(); sshAliases.Clear();
                foreach (var remote in state["codex-managed-remote-connections"].Objects())
                {
                    if (TaskJson.Nonempty(remote["hostId"].Text()) is not { } host) continue;
                    remoteHosts.Add(host);
                    names[host] = Clean(remote["displayName"].Text()) ?? Clean(remote["alias"].Text()) ?? "远端设备";
                    if (host != "local" && TaskJson.Nonempty(remote["alias"].Text()) is { } alias) sshAliases[host] = alias;
                }
                var count = 0;
                foreach (var entry in TaskThreadDiscovery.FromState(state))
                {
                    if (++count > MaximumMetadataCandidates) { limitedCoverage = true; break; }
                    var key = new ThreadKey(entry.HostId, entry.ThreadId);
                    found.TryAdd(key, "");
                    preferredCandidates.Add(key);
                    if (entry.IsLocated) locatedCandidates.Add(key);
                }
                if (state["electron-persisted-atom-state"] is JsonObject atoms)
                {
                    string[] prefixes = ["remote-thread-summaries-v2:", "remote-thread-summaries:"];
                    foreach (var atom in atoms)
                    {
                        var prefix = prefixes.FirstOrDefault(value => atom.Key.StartsWith(value, StringComparison.Ordinal));
                        if (prefix is null) continue;
                        var host = atom.Key[prefix.Length..];
                        foreach (var entry in atom.Value.Objects().TakeLast(64))
                            if (TaskJson.Nonempty((entry["id"] ?? entry["conversationId"] ?? entry["threadId"]).Text()) is { } id)
                                found[new(host, id)] = (entry["title"] ?? entry["name"]).Text() ?? "";
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        // Live-only and side conversations need not be written to session_index at all.
        // Preserve their identities for reconnect; cached metadata is never execution evidence.
        foreach (var key in subscriptions.Concat(activities.Keys).Distinct()) found.TryAdd(key, candidates.GetValueOrDefault(key, ""));
        candidates.Clear();
        foreach (var entry in found) candidates[entry.Key] = entry.Value;
        foreach (var key in lastProbed.Keys.Where(key => !candidates.ContainsKey(key)).ToArray()) lastProbed.Remove(key);
        foreach (var key in activities.Keys.ToArray()) UpdateNavigation(key);
        MaintainSubscriptions();
    }

    private static string? Clean(string? value) => value is null ? null : TaskJson.Nonempty(TaskJson.Compact(value, 180));

    private void TrimHistory(ThreadKey key)
    {
        var total = streams.Values.Sum(stream => (long)stream.Bytes);
        if (total <= MaximumStoredBytes) return;
        limitedCoverage = true;
        foreach (var candidate in streams.Keys.Where(candidate => candidate != key && !activities.ContainsKey(candidate)).ToArray())
        {
            if (total <= MaximumStoredBytes) break;
            total -= streams[candidate].Bytes;
            streams.Remove(candidate); deferred[candidate] = Now.AddSeconds(60); Unsubscribe(candidate);
        }
        if (total > MaximumStoredBytes)
        {
            streams.Remove(key); deferred[key] = Now.AddSeconds(60);
            MarkUnknown(key, "任务历史较大，等待重新同步"); Unsubscribe(key);
        }
    }

    private void Disconnect()
    {
        generation++;
        var oldPipe = pipe; pipe = null;
        connectionLifetime?.Cancel(); connectionLifetime?.Dispose(); connectionLifetime = null;
        outbound?.Writer.TryComplete(); outbound = null;
        oldPipe?.Dispose();
        clientId = initializeId = null;
        subscriptions.Clear(); probes.Clear(); lastProbed.Clear(); deferred.Clear(); streams.Clear(); clientTypes.Clear(); confirmedHosts.Clear();
        sourcesSeen = limitedCoverage = false;
        foreach (var key in activities.Keys.ToArray()) MarkUnknown(key, "任务连接已中断，等待重新同步");
    }

    public void Stop() { lock (sync) Disconnect(); }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            Disconnect(); pending = connectionTasks;
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
