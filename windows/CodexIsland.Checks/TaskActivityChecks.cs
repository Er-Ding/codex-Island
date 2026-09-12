using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using CodexIsland.Core;

namespace CodexIsland.Checks;

/// <summary>Deterministic, isolated checks. No real Desktop tasks or account data are read.</summary>
public static class TaskActivityChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Task activity check failed: " + name);
            passed.Add(name);
        }
        JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
        var running = Parse("""{"title":"同步功能","threadRuntimeStatus":{"type":"active","activeFlags":[]},"turns":[{"turnId":"turn-1","status":"inProgress","params":{"input":[{"type":"text","text":"PRIVATE INPUT"}]},"items":[{"type":"reasoning","text":"PRIVATE REASONING"},{"type":"commandExecution","status":"inProgress","command":"PRIVATE COMMAND","output":"PRIVATE OUTPUT"},{"type":"todo-list","plan":[{"step":"解析状态","status":"completed"},{"step":"连接任务","status":"inProgress"}]}]}]}""");
        Check(TaskActivityDecoder.Decode(running, "id", "设备") is { Status: TaskActivityStatus.Running, Detail: "计划步骤 1/2 · 连接任务" }, "running task exposes completed/current plan steps");
        var idle = running.DeepClone().AsObject(); idle["threadRuntimeStatus"]!["type"] = "idle";
        Check(TaskActivityDecoder.Decode(idle, "id", "设备") is null && TaskActivityDecoder.HasConfirmedEnd(idle), "confirmed idle overrides stale in-progress turn");
        var waiting = running.DeepClone().AsObject(); waiting["threadRuntimeStatus"]!["activeFlags"] = new JsonArray("waitingOnApproval");
        Check(TaskActivityDecoder.Decode(waiting, "id", "设备") is { Status: TaskActivityStatus.Waiting, Detail: "等待你在 Codex Desktop 中批准操作" }, "approval flag produces waiting state");
        waiting["threadRuntimeStatus"]!["activeFlags"] = new JsonArray("waitingOnUserInput");
        Check(TaskActivityDecoder.Decode(waiting, "id", "设备") is { Detail: "等待你在 Codex Desktop 中回复" }, "user input flag produces reply prompt");
        Check(TaskActivityDecoder.Decode(Parse("""{"title":"缓存标题"}"""), "id", "设备") is null, "cached title cannot create a running task");
        Check(TaskActivityDecoder.Decode(Parse("""{"requests":[{"method":"item/commandExecution/requestApproval"}]}"""), "id", "设备") is { Status: TaskActivityStatus.Waiting, Detail: "等待你在 Codex Desktop 中批准操作" }, "legacy pending request preserves approval meaning");
        var summary = TaskActivityProjection.Summary(running);
        Check(!summary.ToJsonString().Contains("PRIVATE", StringComparison.Ordinal), "summary drops user input, reasoning, commands and outputs");
        Check(summary["turns"]![0]!["items"]!.AsArray().Count == 3, "summary preserves array indices for incremental updates");
        var progressPatch = JsonNode.Parse("""[{"op":"replace","path":["turns",0,"items",2,"plan",1,"status"],"value":"completed"},{"op":"replace","path":["turns",0,"items",1,"output"],"value":"PRIVATE UPDATED OUTPUT"}]""")!.AsArray();
        var patched = TaskActivityProjection.Summary(TaskActivityProjection.Apply(summary, progressPatch));
        Check(TaskActivityDecoder.Decode(patched, "id", "设备")?.Detail == "计划步骤 2/2", "incremental plan patch updates progress");
        Check(!patched.ToJsonString().Contains("PRIVATE", StringComparison.Ordinal), "private output patch is ignored");
        Check(TaskActivityDecoder.Decode(summary, "id", "设备")?.Detail == "计划步骤 1/2 · 连接任务", "patch transaction leaves previous snapshot intact");
        var invalid = false;
        try { TaskActivityProjection.Apply(summary, JsonNode.Parse("""[{"op":"replace","path":["turns",7,"status"],"value":"completed"}]""")!.AsArray()); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "out-of-bounds patch requests resynchronization instead of corrupting state");
        var canonical = Parse("""{"turnHistory":{"kind":"canonical","history":{"islands":[{"newerBoundary":{"status":"exhausted"},"entries":[{"value":"last"}]}],"entitiesByKey":{"last":{"status":"inProgress","items":[{"type":"agentMessage","phase":"commentary","text":"准备验证结果"}]}}}}}""");
        Check(TaskActivityDecoder.Decode(canonical, "id", "设备")?.Detail == "准备验证结果", "canonical history resolves latest confirmed turn");
        canonical["turnHistory"]!["history"]!["islands"]![0]!["newerBoundary"]!["status"] = "loading";
        Check(TaskActivityDecoder.Decode(canonical, "id", "设备") is null, "incomplete canonical boundary is not execution evidence");
        var side = Parse("""{"sideConversation":true,"turns":[{"params":{"input":[{"type":"text","text":"PRIVATE CONTEXT\n## My request:\n**修复** [窗口](https://example.test) 的 `拖动`"}]}}]}""");
        Check(TaskSideChatTitle.From(side) == "修复 窗口 的 拖动", "side-chat tab title comes from first request plain text");
        side["turnsPagination"] = new JsonObject { ["hasLoadedOldest"] = false };
        Check(TaskSideChatTitle.From(side) is null, "partial oldest history cannot supply a side-chat tab title");
        const string threadId = "de9de181-ae20-409f-a725-99f2a24a9ee2";
        var navigation = new TaskNavigationContext("child", "remote", IsSideConversation: true, ParentNavigationPath: "/local/" + threadId + "?hostId=remote");
        Check(navigation.SideChatParentThreadId == threadId, "side-chat parent route retains matching host");
        Check((navigation with { ParentNavigationPath = "/local/" + threadId + "?hostId=other" }).SideChatParentThreadId is null, "side-chat parent cannot cross device identity");
        Check((navigation with { ParentNavigationPath = "https://example.test/local/" + threadId }).SideChatParentThreadId is null, "side-chat parent cannot become an arbitrary external URL");

        var fake = new FakeClient();
        await using (var store = new TaskActivityStore(fake))
        {
            var first = store.RefreshAsync();
            var second = store.RefreshAsync();
            Check(ReferenceEquals(first, second), "concurrent refreshes share one operation");
            fake.Completion.SetResult(new([new("id", "测试任务", "本机", TaskActivityStatus.Running, "正在执行")], "connected", true));
            await first.ConfigureAwait(false);
            Check(fake.Calls == 1 && store.ActiveCount == 1 && store.IsConnected, "coalesced refresh publishes a single confirmed snapshot");
            fake.Throw = true;
            await store.RefreshAsync().ConfigureAwait(false);
            Check(store.Tasks.Count == 1 && store.Tasks[0].Status == TaskActivityStatus.Unknown && store.ActiveCount == 0 && !store.IsConnected,
                "failed refresh retains the task and marks it unknown");
        }
        await using (var demo = new TaskActivityStore(new FakeClient(), isDemo: true))
            Check(demo.Tasks.Count == 3 && demo.ActiveCount == 2 && demo.FeaturedTask?.Status == TaskActivityStatus.Waiting,
                "demo provides three states and prioritizes waiting actions");
        await CheckPipeAsync(Check).ConfigureAwait(false);
        await TaskStartupChecks.RunAsync(Check).ConfigureAwait(false);
        return passed;
    }

    private sealed class FakeClient : ITaskActivityClient
    {
        public int Calls;
        public bool Throw;
        public TaskCompletionSource<TaskActivitySnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TaskActivitySnapshot> FetchAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Throw) throw new IOException("Synthetic disconnect");
            return await Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public Task<TaskNavigationContext?> RefreshSideChatNavigationAsync(TaskNavigationContext context, CancellationToken cancellationToken = default) => Task.FromResult<TaskNavigationContext?>(context);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task CheckPipeAsync(Action<bool, string> check)
    {
        const string threadId = "37e4fe02-173e-461f-b2b6-df51fa35b84c";
        var testRoot = Path.Combine(Path.GetTempPath(), "codex-island-task-check-" + Guid.NewGuid().ToString("N"));
        var pipeName = "codex-island-check-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(testRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var cancellationToken = timeout.Token;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(testRoot, "session_index.jsonl"), "{\"id\":\"" + threadId + "\",\"thread_name\":\"Synthetic task\"}\n", cancellationToken).ConfigureAwait(false);
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = server.WaitForConnectionAsync(cancellationToken);
            await using var client = new TaskActivityClient(testRoot, pipeName);
            var fetch = client.FetchAsync(cancellationToken);
            await accepted.ConfigureAwait(false);
            var initialize = await ReadFrameAsync(server, cancellationToken).ConfigureAwait(false);
            check(initialize["method"].Text() == "initialize" && initialize["sourceClientId"].Text() == "initializing-client"
                && initialize["version"].Integer() == 0, "named-pipe transport performs the Desktop initialization handshake");
            await WriteFrameAsync(server, new JsonObject { ["type"] = "response", ["requestId"] = initialize["requestId"]!.DeepClone(), ["resultType"] = "success", ["result"] = new JsonObject { ["clientId"] = "island-test" } }, cancellationToken).ConfigureAwait(false);
            var following = await ReadFrameAsync(server, cancellationToken).ConfigureAwait(false);
            check(following["method"].Text() == "thread-stream-following-changed" && following["params"]!["following"].Boolean()
                && following["params"]!["conversationId"].Text() == threadId, "discovered metadata only subscribes to an existing thread stream");
            await WriteFrameAsync(server, new JsonObject { ["type"] = "broadcast", ["method"] = "client-status-changed", ["params"] = new JsonObject { ["clientId"] = "owner", ["clientType"] = "desktop", ["status"] = "connected" } }, cancellationToken).ConfigureAwait(false);
            var state = JsonNode.Parse("""{"title":"Synthetic task","threadRuntimeStatus":{"type":"active","activeFlags":[]},"turns":[{"status":"inProgress","items":[{"type":"commandExecution","status":"inProgress"}]}]}""")!.AsObject();
            JsonObject Broadcast(JsonObject change) => new()
            {
                ["type"] = "broadcast", ["method"] = "thread-stream-state-changed", ["version"] = 11, ["sourceClientId"] = "owner",
                ["params"] = new JsonObject { ["conversationId"] = threadId, ["hostId"] = "local", ["change"] = change }
            };
            await WriteFrameAsync(server, Broadcast(new JsonObject { ["type"] = "snapshot", ["revision"] = 1, ["conversationState"] = state }), cancellationToken).ConfigureAwait(false);
            await fetch.ConfigureAwait(false);
            async Task<TaskActivitySnapshot> WaitSnapshot(Func<TaskActivitySnapshot, bool> predicate)
            {
                while (true)
                {
                    var snapshot = await client.FetchAsync(cancellationToken).ConfigureAwait(false);
                    if (predicate(snapshot)) return snapshot;
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }
            var initial = await WaitSnapshot(snapshot => snapshot.Tasks.Count == 1 && snapshot.IsConnected).ConfigureAwait(false);
            check(initial.Tasks[0] is { Status: TaskActivityStatus.Running, Detail: "正在执行命令", Navigation.OwnerClientType: "desktop" },
                "framed snapshot projects live status and owner navigation metadata");
            await WriteFrameAsync(server, Broadcast(new JsonObject
            {
                ["type"] = "patches", ["revision"] = 2, ["baseRevision"] = 1,
                ["patches"] = JsonNode.Parse("""[{"op":"replace","path":["threadRuntimeStatus","activeFlags"],"value":["waitingOnUserInput"]}]""")
            }), cancellationToken).ConfigureAwait(false);
            var changed = await WaitSnapshot(snapshot => snapshot.Tasks[0].Status == TaskActivityStatus.Waiting).ConfigureAwait(false);
            check(changed.Tasks[0].Detail == "等待你在 Codex Desktop 中回复", "framed revision patch transitions task into waiting state");
            await WriteFrameAsync(server, Broadcast(new JsonObject { ["type"] = "patches", ["revision"] = 4, ["baseRevision"] = 0, ["patches"] = new JsonArray() }), cancellationToken).ConfigureAwait(false);
            var refresh = await ReadFrameAsync(server, cancellationToken).ConfigureAwait(false);
            var unknown = await WaitSnapshot(snapshot => snapshot.Tasks[0].Status == TaskActivityStatus.Unknown).ConfigureAwait(false);
            check(refresh["method"].Text() == "thread-stream-following-changed" && unknown.Tasks.Count == 1,
                "revision gap retains task and requests a new confirmed snapshot");
            await WriteFrameAsync(server, Broadcast(new JsonObject { ["type"] = "snapshot", ["revision"] = 5, ["conversationState"] = JsonNode.Parse("""{"title":"Synthetic task","threadRuntimeStatus":{"type":"active","activeFlags":[]}}""") }), cancellationToken).ConfigureAwait(false);
            await WaitSnapshot(snapshot => snapshot.Tasks[0].Status == TaskActivityStatus.Running).ConfigureAwait(false);
            server.Disconnect();
            // Explicit Stop does not reconnect to the synthetic listener; cached execution is no longer confirmed.
            client.Stop();
            var stopped = await client.FetchAsync(cancellationToken).ConfigureAwait(false);
            check(stopped.Tasks.Count == 1 && stopped.Tasks[0].Status == TaskActivityStatus.Unknown && !stopped.IsConnected,
                "pipe disconnect retains known task without claiming it finished");
        }
        finally { Directory.Delete(testRoot, recursive: true); }
    }

    private static async Task<JsonObject> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Synthetic task frame limit");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(bytes)!.AsObject();
    }

    private static async Task WriteFrameAsync(Stream stream, JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
