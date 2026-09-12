using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using CodexIsland.Core;

namespace CodexIsland.Checks;

/// <summary>Cold-start checks use only temporary metadata and a synthetic Desktop pipe.</summary>
internal static class TaskStartupChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckPersistedDiscoveryAsync(check).ConfigureAwait(false);
        await CheckTopLevelDiscoveryAsync(check).ConfigureAwait(false);
        await CheckUnansweredSubscriptionAsync(check).ConfigureAwait(false);
        await CheckQuietReconnectAsync(check).ConfigureAwait(false);
        await CheckUnannouncedStreamAsync(check).ConfigureAwait(false);
        await CheckSubscriptionRotationAsync(check).ConfigureAwait(false);
    }

    private static async Task CheckPersistedDiscoveryAsync(Action<bool, string> check)
    {
        const string sharedId = "a781c31a-d090-40cd-8298-8faf48a2a188";
        const string idleId = "678c8c31-8299-43bf-9887-e5c0581a39e3";
        const string childId = "b4abc3e8-4eaf-41ae-b55f-a8c1791f3552";
        const string remoteHost = "ssh:synthetic/remote host";
        await using var test = new PipeFixture();
        var atoms = new JsonObject
        {
            ["thread-client-id-v1:" + Uri.EscapeDataString("local:" + sharedId)] = "synthetic-owner",
            ["thread-client-id-v1:" + Uri.EscapeDataString("local:" + idleId)] = "synthetic-owner",
            ["thread-tab-routes-v1:" + Uri.EscapeDataString(sharedId)] = new JsonObject
            {
                ["version"] = 1,
                ["routes"] = new JsonArray(
                    new JsonObject
                    {
                        ["kind"] = "review", ["payloadVersion"] = 1, ["tabId"] = "review",
                        ["params"] = new JsonObject { ["conversationId"] = sharedId, ["hostId"] = remoteHost, ["cwd"] = "/synthetic/workspace" }
                    },
                    new JsonObject
                    {
                        ["kind"] = "background-agent", ["payloadVersion"] = 1, ["tabId"] = "background-agent:" + childId,
                        ["params"] = new JsonObject { ["conversationId"] = childId }
                    })
            }
        };
        await test.WriteStateAsync(new JsonObject
        {
            ["electron-persisted-atom-state"] = atoms,
            ["codex-managed-remote-connections"] = new JsonArray(new JsonObject
            {
                ["hostId"] = remoteHost, ["displayName"] = "Synthetic remote", ["alias"] = "synthetic-remote"
            })
        }).ConfigureAwait(false);
        await test.ConnectAsync().ConfigureAwait(false);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "local:" + sharedId, remoteHost + ":" + sharedId, "local:" + idleId, remoteHost + ":" + childId
        };
        while (expected.Count > 0)
        {
            var following = await test.ReadFollowingAsync("persisted metadata startup discovery").ConfigureAwait(false);
            var host = following["params"]!["hostId"].Text()!;
            var thread = following["params"]!["conversationId"].Text()!;
            if (!expected.Remove(host + ":" + thread)) continue;
            await test.SendSnapshotAsync(host, thread, thread == idleId ? "idle" : "active",
                waiting: thread == childId).ConfigureAwait(false);
        }
        var snapshot = await test.WaitSnapshotAsync(value => value.IsConnected && value.Tasks.Count == 3).ConfigureAwait(false);
        check(snapshot.Tasks.Count(value => value.Navigation?.ThreadId == sharedId) == 2
            && snapshot.Tasks.Any(value => value.Navigation is { HostId: "local", ThreadId: sharedId })
            && snapshot.Tasks.Any(value => value.Navigation?.HostId == remoteHost && value.Navigation.ThreadId == sharedId),
            "startup discovers existing local and remote tasks from persisted client keys without a session index");
        check(snapshot.Tasks.Single(value => value.Navigation?.ThreadId == childId) is
            { Status: TaskActivityStatus.Waiting, DeviceName: "Synthetic remote" } child && child.Navigation?.HostId == remoteHost,
            "startup discovers an existing remote background task from a persisted tab route with its parent host identity");
        check(snapshot.Tasks.All(value => value.Navigation?.ThreadId != idleId),
            "cached idle threads are subscribed but never counted as active tasks");
    }

    private static async Task CheckTopLevelDiscoveryAsync(Action<bool, string> check)
    {
        const string localId = "ff2957cc-9098-4fc7-8077-031f67f560b0";
        const string remoteId = "07e930c0-63a8-4c8f-8853-c0dd29fbcce9";
        const string remoteHost = "ssh:synthetic-assignment";
        await using var test = new PipeFixture();
        await test.WriteStateAsync(new JsonObject
        {
            ["projectless-thread-ids"] = new JsonArray(localId),
            ["thread-project-assignments"] = new JsonObject
            {
                [remoteId] = new JsonObject { ["projectKind"] = "remote", ["projectId"] = "synthetic-project", ["hostId"] = remoteHost }
            }
        }).ConfigureAwait(false);
        await test.ConnectAsync().ConfigureAwait(false);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "local:" + localId, remoteHost + ":" + remoteId };
        while (expected.Count > 0)
        {
            var following = await test.ReadFollowingAsync("top-level metadata without persisted atoms").ConfigureAwait(false);
            var host = following["params"]!["hostId"].Text()!;
            var thread = following["params"]!["conversationId"].Text()!;
            if (expected.Remove(host + ":" + thread)) await test.SendSnapshotAsync(host, thread, "active").ConfigureAwait(false);
        }
        var snapshot = await test.WaitSnapshotAsync(value => value.Tasks.Count == 2).ConfigureAwait(false);
        check(snapshot.Tasks.Any(value => value.Navigation is { HostId: "local", ThreadId: localId }),
            "startup discovers projectless tasks when no persisted atom state exists");
        check(snapshot.Tasks.Any(value => value.Navigation is { HostId: remoteHost, ThreadId: remoteId }),
            "startup discovers the assigned remote host when no persisted atom state exists");
    }

    private static async Task CheckUnansweredSubscriptionAsync(Action<bool, string> check)
    {
        const string threadId = "697fd0b7-969a-48db-b00c-5d329d234c2e";
        await using var test = new PipeFixture();
        await test.WriteIndexAsync(threadId).ConfigureAwait(false);
        await test.ConnectAsync().ConfigureAwait(false);
        var first = await test.ReadFollowingAsync("initial subscription").ConfigureAwait(false);
        check(first["params"]!["conversationId"].Text() == threadId,
            "startup requests an existing task snapshot before receiving task broadcasts");
        test.Clock.Advance(TimeSpan.FromSeconds(4));
        await test.Client.FetchAsync(test.Token).ConfigureAwait(false);
        var retry = await test.ReadFollowingAsync("retry after an owner missed the first subscription").ConfigureAwait(false);
        check(retry["params"]!["conversationId"].Text() == threadId,
            "an unanswered startup subscription is retried without waiting for a new task event");
        await test.SendSnapshotAsync("local", threadId, "active").ConfigureAwait(false);
        var snapshot = await test.WaitSnapshotAsync(value => value.Tasks.Count == 1).ConfigureAwait(false);
        check(snapshot.Tasks[0].Status == TaskActivityStatus.Running && snapshot.IsConnected,
            "a delayed owner snapshot restores the pre-existing running task");
    }

    private static async Task CheckQuietReconnectAsync(Action<bool, string> check)
    {
        const string threadId = "4737013f-5335-4420-aef8-977f4c528238";
        await using var test = new PipeFixture();
        await test.ConnectAsync().ConfigureAwait(false);
        await test.SendAsync(new JsonObject
        {
            ["type"] = "broadcast", ["method"] = "thread-stream-following-status-requested", ["version"] = 1,
            ["sourceClientId"] = "synthetic-owner",
            ["params"] = new JsonObject { ["conversationId"] = threadId, ["hostId"] = "local" }
        }).ConfigureAwait(false);
        await test.ReadFollowingAsync("live task subscription").ConfigureAwait(false);
        await test.SendSnapshotAsync("local", threadId, "active").ConfigureAwait(false);
        await test.WaitSnapshotAsync(value => value.Tasks.Count == 1).ConfigureAwait(false);
        test.Clock.Advance(TimeSpan.FromSeconds(16));
        await test.Client.FetchAsync(test.Token).ConfigureAwait(false);
        test.Client.Stop();
        test.Server.Disconnect();
        test.Clock.Advance(TimeSpan.FromSeconds(16));
        await test.ConnectAsync().ConfigureAwait(false);
        var following = await test.ReadFollowingAsync("quiet owner subscription after reconnect").ConfigureAwait(false);
        check(following["params"]!["conversationId"].Text() == threadId,
            "cache rescans retain live-discovered tasks for reconnection even when no index contains them");
        await test.SendSnapshotAsync("local", threadId, "active", waiting: true).ConfigureAwait(false);
        var restored = await test.WaitSnapshotAsync(value => value.Tasks.Count == 1 && value.Tasks[0].Status == TaskActivityStatus.Waiting).ConfigureAwait(false);
        check(restored.IsConnected,
            "reconnection refreshes a quiet existing task without another owner announcement");
    }

    private static async Task CheckUnannouncedStreamAsync(Action<bool, string> check)
    {
        const string threadId = "fa8f6142-fc5f-4386-bcc2-9d45e37b3f66";
        await using var test = new PipeFixture();
        await test.ConnectAsync().ConfigureAwait(false);
        await test.SendSnapshotAsync("local", threadId, "active").ConfigureAwait(false);
        var following = await test.ReadFollowingAsync("previously unknown stream snapshot").ConfigureAwait(false);
        var snapshot = await test.WaitSnapshotAsync(value => value.Tasks.Count == 1).ConfigureAwait(false);
        check(following["params"]!["conversationId"].Text() == threadId && snapshot.Tasks[0].Status == TaskActivityStatus.Running,
            "a valid snapshot discovers and follows a task even when its initial owner announcement was missed");
    }

    private static async Task CheckSubscriptionRotationAsync(Action<bool, string> check)
    {
        const string activeId = "00000000-0000-4000-8000-000000000001";
        await using var test = new PipeFixture();
        var atoms = new JsonObject();
        for (var index = 1; index <= 1300; index++)
        {
            var id = "00000000-0000-4000-8000-" + index.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
            atoms["thread-client-id-v1:" + Uri.EscapeDataString("local:" + id)] = "synthetic-owner";
        }
        await test.WriteStateAsync(new JsonObject { ["electron-persisted-atom-state"] = atoms }).ConfigureAwait(false);
        await test.ConnectAsync().ConfigureAwait(false);
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        var firstProbeAt = test.Clock.GetUtcNow();
        string? retiredId = null, retiredSnapshotId = null;
        var initialIdle = true;
        for (var batch = 0; batch < 6; batch++)
        {
            if (batch > 0)
            {
                test.Clock.Advance(TimeSpan.FromSeconds(16));
                var beforeBatch = await test.Client.FetchAsync(test.Token).ConfigureAwait(false);
                initialIdle &= beforeBatch.Tasks.Count == 0;
            }
            for (var index = 0; index < 256; index++)
            {
                var following = await test.ReadFollowingAsync("fair rotation through 1300 startup candidates").ConfigureAwait(false);
                var thread = following["params"]!["conversationId"].Text()!;
                if (batch == 4 && index == 0) retiredId = thread;
                if (batch == 4 && index == 1) retiredSnapshotId = thread;
                discovered.Add(thread);
                await test.SendSnapshotAsync("local", thread, thread == activeId ? "active" : "idle").ConfigureAwait(false);
            }
            await test.SynchronizeAsync().ConfigureAwait(false);
        }
        var snapshot = await test.WaitSnapshotAsync(value => value.Tasks.Count == 1).ConfigureAwait(false);
        check(initialIdle && discovered.Count == 1300 && test.Clock.GetUtcNow() - firstProbeAt > TimeSpan.FromSeconds(60)
            && snapshot.Tasks[0].Navigation?.ThreadId == activeId && snapshot.Tasks[0].Status == TaskActivityStatus.Running,
            "startup fairly probes 1300 candidates after 60 seconds without recycled history starving older running tasks");

        await test.SendAsync(new JsonObject
        {
            ["type"] = "broadcast", ["method"] = "thread-stream-following-status-requested", ["version"] = 1,
            ["sourceClientId"] = "synthetic-owner",
            ["params"] = new JsonObject { ["conversationId"] = retiredId, ["hostId"] = "local" }
        }).ConfigureAwait(false);
        var restoredSubscription = await test.ReadFollowingAsync("live owner announcement during historical cooldown").ConfigureAwait(false);
        await test.SendSnapshotAsync("local", retiredId!, "active").ConfigureAwait(false);
        var restored = await test.WaitSnapshotAsync(value => value.Tasks.Count == 2).ConfigureAwait(false);
        check(restoredSubscription["params"]!["conversationId"].Text() == retiredId
            && restored.Tasks.Any(value => value.Navigation?.ThreadId == retiredId && value.Status == TaskActivityStatus.Running),
            "a live owner announcement immediately restores a retired task during the 60-second historical cooldown");

        await test.SendSnapshotAsync("local", retiredSnapshotId!, "active", waiting: true).ConfigureAwait(false);
        var snapshotSubscription = await test.ReadFollowingAsync("live snapshot during historical cooldown").ConfigureAwait(false);
        var liveSnapshot = await test.WaitSnapshotAsync(value => value.Tasks.Count == 3).ConfigureAwait(false);
        check(snapshotSubscription["params"]!["conversationId"].Text() == retiredSnapshotId
            && liveSnapshot.Tasks.Any(value => value.Navigation?.ThreadId == retiredSnapshotId && value.Status == TaskActivityStatus.Waiting),
            "a live snapshot immediately restores a retired task during the 60-second historical cooldown");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "codex-island-startup-check-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        public ManualTimeProvider Clock { get; } = new();
        public NamedPipeServerStream Server { get; }
        public TaskActivityClient Client { get; }
        public CancellationToken Token => timeout.Token;

        public PipeFixture()
        {
            Directory.CreateDirectory(directory);
            var pipeName = "codex-island-startup-check-" + Guid.NewGuid().ToString("N");
            Server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Client = new TaskActivityClient(directory, pipeName, Clock);
        }

        public Task WriteStateAsync(JsonObject state) => File.WriteAllTextAsync(Path.Combine(directory, ".codex-global-state.json"), state.ToJsonString(), Token);
        public Task WriteIndexAsync(string threadId) => File.WriteAllTextAsync(Path.Combine(directory, "session_index.jsonl"),
            new JsonObject { ["id"] = threadId, ["thread_name"] = "Synthetic startup task" }.ToJsonString() + "\n", Token);

        public async Task ConnectAsync()
        {
            var accepted = Server.WaitForConnectionAsync(Token);
            var fetch = Client.FetchAsync(Token);
            await accepted.ConfigureAwait(false);
            var initialize = await ReadFrameAsync(Token).ConfigureAwait(false);
            if (initialize["method"].Text() != "initialize") throw new InvalidOperationException("Synthetic Desktop expected initialize");
            await SendAsync(new JsonObject
            {
                ["type"] = "response", ["requestId"] = initialize["requestId"]!.DeepClone(), ["resultType"] = "success",
                ["result"] = new JsonObject { ["clientId"] = "synthetic-island" }
            }).ConfigureAwait(false);
            await fetch.ConfigureAwait(false);
        }

        public async Task<JsonObject> ReadFollowingAsync(string purpose)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (true)
                {
                    var frame = await ReadFrameAsync(readTimeout.Token).ConfigureAwait(false);
                    if (frame["method"].Text() == "thread-stream-following-changed" && frame["params"]!["following"].Boolean()) return frame;
                }
            }
            catch (OperationCanceledException error)
            {
                throw new InvalidOperationException("Missing task subscription: " + purpose, error);
            }
        }

        public async Task SynchronizeAsync()
        {
            var requestId = Guid.NewGuid().ToString("N");
            await SendAsync(new JsonObject { ["type"] = "client-discovery-request", ["requestId"] = requestId }).ConfigureAwait(false);
            var reply = await ReadFrameAsync(Token).ConfigureAwait(false);
            if (reply["type"].Text() != "client-discovery-response" || reply["requestId"].Text() != requestId)
                throw new InvalidOperationException("Synthetic stream processing barrier received an unexpected frame");
        }

        public async Task<TaskActivitySnapshot> WaitSnapshotAsync(Func<TaskActivitySnapshot, bool> predicate)
        {
            while (true)
            {
                var snapshot = await Client.FetchAsync(Token).ConfigureAwait(false);
                if (predicate(snapshot)) return snapshot;
                await Task.Delay(10, Token).ConfigureAwait(false);
            }
        }

        public Task SendSnapshotAsync(string host, string thread, string runtimeStatus, bool waiting = false) => SendAsync(new JsonObject
        {
            ["type"] = "broadcast", ["method"] = "thread-stream-state-changed", ["version"] = 11, ["sourceClientId"] = "synthetic-owner",
            ["params"] = new JsonObject
            {
                ["conversationId"] = thread, ["hostId"] = host,
                ["change"] = new JsonObject
                {
                    ["type"] = "snapshot", ["revision"] = 1,
                    ["conversationState"] = new JsonObject
                    {
                        ["title"] = "Synthetic startup task",
                        ["threadRuntimeStatus"] = new JsonObject
                        {
                            ["type"] = runtimeStatus,
                            ["activeFlags"] = waiting ? new JsonArray("waitingOnUserInput") : new JsonArray()
                        }
                    }
                }
            }
        });

        public async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            await Server.WriteAsync(prefix, Token).ConfigureAwait(false);
            await Server.WriteAsync(bytes, Token).ConfigureAwait(false);
            await Server.FlushAsync(Token).ConfigureAwait(false);
        }

        private async Task<JsonObject> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var prefix = new byte[4];
            await Server.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Synthetic startup frame limit");
            var bytes = new byte[length];
            await Server.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(bytes)!.AsObject();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await Server.DisposeAsync().ConfigureAwait(false);
            timeout.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
