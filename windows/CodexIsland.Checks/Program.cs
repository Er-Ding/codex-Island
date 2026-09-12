using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexIsland.Core;

namespace CodexIsland.Checks;

internal static class Program
{
    private static int passed, failed;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1788600000);
    private const string Weekly = """{"rateLimits":{"primary":{"usedPercent":88,"windowDurationMins":10080,"resetsAt":1788749228},"planType":"pro"}}""";

    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fake-server") return await FakeServer.RunAsync(args[1], args.ElementAtOrDefault(2));
        ModelChecks();
        PlacementChecks();
        HoverChecks();
        HeaderDragChecks();
        await StoreChecks();
        await TransportChecks();
        try
        {
            foreach (var check in await TaskActivityChecks.RunAsync())
            {
                passed++;
                Console.WriteLine("PASS: " + check);
            }
        }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL: " + error.Message); }
        Console.WriteLine($"{passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static void Assert(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL: {name}: {e.Message}"); }
    }
    private static async Task CheckAsync(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL: {name}: {e.Message}"); }
    }
    private static void Expect(QuotaError kind, Action operation)
    {
        try { operation(); }
        catch (QuotaException e) { Assert(e.Kind == kind); return; }
        throw new InvalidOperationException("Expected " + kind);
    }
    private static QuotaSnapshot Parse(string json) => QuotaParser.Parse(json, Now);

    private static void ModelChecks()
    {
        Check("weekly-only account keeps its actual period", () =>
        {
            var value = Parse(Weekly);
            Assert(value.MainBucket.Primary is { RemainingPercent: 12, PeriodTitle: "本周", WindowDurationMins: 10080 });
            Assert(value.MainBucket.Secondary is null && value.PlanName == "Pro" && value.FetchedAt == Now);
        });
        Check("multiple buckets stay separate and codex sorts first", () =>
        {
            var value = Parse("""{"rateLimitsByLimitId":{"extra":{"limitName":"其他模型","primary":{"usedPercent":0},"secondary":{"usedPercent":17}},"codex":{"primary":{"usedPercent":88}}}}""");
            Assert(value.Buckets.Count == 2 && value.Buckets[0].Id == "codex");
            Assert(value.MainBucket.Secondary is null && value.Buckets[1].Secondary?.RemainingPercent == 83);
        });
        Check("map beats conflicting legacy data", () =>
        {
            var value = Parse("""{"rateLimits":{"primary":{"usedPercent":5},"planType":"plus"},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":88},"planType":"pro"}}}""");
            Assert(value.MainBucket.Primary?.RemainingPercent == 12 && value.PlanName == "Pro");
        });
        Check("legacy data supplements a map without main quota", () =>
        {
            var value = Parse("""{"rateLimits":{"primary":{"usedPercent":88}},"rateLimitsByLimitId":{"extra":{"primary":{"usedPercent":0}}}}""");
            Assert(value.Buckets.Count == 2 && value.MainBucket.Id == "codex");
        });
        Check("missing quotas never become a full allowance", () =>
        {
            foreach (var json in new[] { "{}", """{"futureField":100}""", """{"rateLimits":null,"rateLimitsByLimitId":null}""", """{"rateLimits":{"primary":null,"secondary":null}}""" })
                Expect(QuotaError.NoQuota, () => Parse(json));
        });
        Check("missing or invalid usedPercent is rejected", () =>
        {
            foreach (var json in new[] { "not json", "null", """{"rateLimits":{"primary":{}}}""", """{"rateLimits":{"primary":{"usedPercent":null}}}""", """{"rateLimits":{"primary":{"usedPercent":"unknown"}}}""", """{"rateLimits":{"primary":{"usedPercent":1e999}}}""" })
                Expect(QuotaError.InvalidResponse, () => Parse(json));
        });
        Check("remaining percent clamps to 0..100 and displays fractions honestly", () =>
        {
            foreach (var (used, expected) in new[] { (-5d, 100d), (0, 100), (12.5, 87.5), (100, 0), (125, 0) })
                Assert(new QuotaWindow(used).RemainingPercent == expected);
            Assert(new QuotaWindow(99.5).PercentText == "<1%" && new QuotaWindow(12.1).PercentText == "87%");
        });
        Check("unknown period and reset time remain unknown", () =>
        {
            var value = Parse("""{"rateLimits":{"primary":{"usedPercent":30,"resetsAt":null}}}""").MainBucket.Primary!;
            Assert(value.PeriodTitle == "当前周期" && value.ResetText(Now) == "恢复时间暂未提供");
        });
        Check("past reset does not invent recovered quota", () =>
        {
            var value = new QuotaWindow(88, 10080, Now.AddSeconds(-1).ToUnixTimeSeconds());
            Assert(value.ResetText(Now) == "已到恢复时间，等待刷新" && value.RemainingPercent == 12);
        });
        Check("weekly reset uses Beijing date across midnight with a fixed 24-hour clock", () =>
        {
            var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
            var reset = new DateTimeOffset(2026, 9, 12, 20, 7, 0, TimeSpan.Zero);
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                Assert(new QuotaWindow(60, 10080, reset.ToUnixTimeSeconds()).ResetText(now)
                    == "北京时间 9月13日 04:07 刷新");
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        });
        Check("non-weekly resets retain their relative countdown", () =>
        {
            Assert(new QuotaWindow(60, 300, Now.AddMinutes(61).ToUnixTimeSeconds()).ResetText(Now) == "1小时1分钟后恢复");
            Assert(new QuotaWindow(60, 1440, Now.AddHours(25).ToUnixTimeSeconds()).ResetText(Now) == "1天1小时后恢复");
        });
        Check("weekly reset rejects missing, non-finite and implausible future timestamps", () =>
        {
            foreach (var reset in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                Now.AddDays(3651).ToUnixTimeSeconds() })
                Assert(new QuotaWindow(60, 10080, reset).ResetText(Now) == "恢复时间暂未提供");
            Assert(new QuotaWindow(60, 10080, DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                .ResetText(DateTimeOffset.MaxValue.AddHours(-1)) == "恢复时间暂未提供");
        });
        Check("secondary-only quota is still displayed", () => Assert(Parse("""{"rateLimits":{"secondary":{"usedPercent":27}}}""").MainBucket.Windows.Single().RemainingPercent == 73));
        Check("error messages do not expose upstream account data", () =>
        {
            foreach (var raw in new[] { "401 auth person@example.com", "429 secret", "apikey sk-secret", "server secret" })
                Assert(!QuotaException.FromServer(raw).Message.Contains("secret") && !QuotaException.FromServer(raw).Message.Contains('@'));
        });
        Check("line framing handles CRLF, many messages and partial UTF-8", () =>
        {
            var buffer = new JsonLineBuffer();
            var json = """{"label":"剩余"}""";
            var bytes = Encoding.UTF8.GetBytes(json + "\r\n\n{\"id\":2}\n");
            var split = Encoding.UTF8.GetByteCount("{\"label\":\"") + 1;
            Assert(buffer.Append(bytes.AsSpan(0, split)).Count == 0);
            var result = buffer.Append(bytes.AsSpan(split));
            Assert(result.Count == 2 && JsonDocument.Parse(result[0]).RootElement.GetProperty("label").GetString() == "剩余");
            Assert(buffer.Append("{\"id\":"u8).Count == 0);
            Assert(Encoding.UTF8.GetString(buffer.Append("3}\n"u8)[0]) == "{\"id\":3}");
        });
        Check("line bound applies per message, including unterminated messages", () =>
        {
            var buffer = new JsonLineBuffer(8);
            Assert(buffer.Append("1234567\n1234567\n"u8).Count == 2);
            Expect(QuotaError.InvalidResponse, () => buffer.Append(new byte[9]));
        });
    }

    private static void PlacementChecks()
    {
        var main = new DisplayInfo("main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, true);
        var left = new DisplayInfo("left", new(-2560, -300, 2560, 1440), new(-2560, -260, 2560, 1400), 1.5, false);
        Check("position restores relative to a negative-origin monitor's work area", () =>
        {
            var frame = new RectD(-2000, -200, 336, 54);
            var saved = SavedPosition.Capture(frame, left);
            Assert(saved.Restore(left, new(336, 54)) == frame && saved.TopOffset == 40);
        });
        Check("DPI changes convert the saved DIP offset once", () =>
        {
            var saved = SavedPosition.Capture(new(700, 80, 224, 36), main);
            var doubled = main with { Bounds = new(0, 0, 3840, 2160), WorkArea = new(0, 0, 3840, 2080), DpiScale = 2 };
            var restored = saved.Restore(doubled, new(448, 72));
            Assert(restored.Y == 160 && restored.CenterX == 1624);
        });
        Check("expansion near any edge stays in the work area", () =>
        {
            foreach (var origin in new[] { new PointD(-900, -200), new PointD(1850, 1000), new PointD(0, 1000), new PointD(1850, 0) })
            {
                var rect = IslandPlacement.Clamp(new(origin.X, origin.Y, 420, 336), main.WorkArea);
                Assert(rect.X >= 0 && rect.Y >= 0 && rect.Right <= 1920 && rect.Bottom <= 1040);
            }
        });
        Check("oversized windows and non-finite geometry safely clamp", () =>
        {
            Assert(IslandPlacement.Clamp(new(0, 0, 420, 336), new(-100, -50, 300, 200)) == new RectD(-100, -50, 300, 200));
            Assert(IslandPlacement.Clamp(new(double.NaN, double.PositiveInfinity, 224, 36), main.WorkArea) == new RectD(848, 0, 224, 36));
            Assert(IslandPlacement.Clamp(new(0, 0, 1, 1), default) == default);
        });
        Check("disconnected display uses a fallback without rewriting its saved ID", () =>
        {
            var saved = new SavedPosition("left", 0.5, 20);
            Assert(IslandPlacement.Select([main], saved.DisplayId) == main && saved.DisplayId == "left");
            Assert(IslandPlacement.Select([main, left], saved.DisplayId) == left);
        });
        Check("automatic size uses DIPs rather than physical 4K pixels", () =>
        {
            var retina = main with { WorkArea = new(0, 0, 3840, 2160), DpiScale = 2 };
            Assert(IslandPlacement.Scale(retina, IslandSize.Automatic) == 1);
            Assert(IslandPlacement.Scale(retina with { DpiScale = 1 }, IslandSize.Automatic) == 2);
            Assert(IslandPlacement.Scale(main, IslandSize.Percent150) == 1.5);
        });
        Check("settings round-trip preserves sizes when position resets", () =>
        {
            var store = TempSettings();
            var settings = new IslandSettings { Position = new("left", 0.3, 25), DisplaySizes = new() { ["left"] = IslandSize.Percent150 } };
            store.Save(settings);
            Assert(store.Load().Position == settings.Position);
            store.Save(store.Load() with { Position = null });
            Assert(store.Load().Position is null && store.Load().DisplaySizes["left"] == IslandSize.Percent150);
        });
        Check("corrupt settings, invalid offsets and unsupported versions fall back", () =>
        {
            var store = TempSettings();
            foreach (var json in new[] { "garbage", "null", """{"version":99}""", """{"position":{"displayId":"x","horizontalFraction":1.5,"topOffset":0}}""", """{"position":{"displayId":"x","horizontalFraction":0.5,"topOffset":-1},"displaySizes":null}""" })
            {
                File.WriteAllText(store.FilePath, json);
                Assert(store.Load().Position is null);
            }
        });
    }
    private static SettingsStore TempSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CodexIslandChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new(Path.Combine(folder, "settings.json"));
    }

    private static void HoverChecks()
    {
        Check("the entire rectangle including top and corners counts as inside", () =>
        {
            var region = new RectD(100, 0, 224, 36);
            for (var x = 100; x <= 324; x++) for (var y = 0; y <= 36; y++) Assert(region.Contains(new(x, y)));
            Assert(!region.Contains(new(99, 0)));
        });
        Check("hover expands immediately and debounces only exit", () =>
        {
            var state = new IslandInteraction();
            Assert(state.ObservePointer(true, 0) && state.IsExpanded);
            Assert(!state.ObservePointer(true, 0) && !state.ObservePointer(true, 0.05));
            Assert(!state.ObservePointer(false, 0.2) && !state.ObservePointer(false, 0.3));
            Assert(state.ObservePointer(false, 0.49) && !state.IsExpanded);
            Assert(state.ObservePointer(true, 0.5) && state.IsExpanded);
        });
        Check("reentry cancels pending collapse and the next exit gets a fresh delay", () =>
        {
            var state = new IslandInteraction();
            Assert(state.ObservePointer(true, 0));
            Assert(!state.ObservePointer(false, 1));
            Assert(!state.ObservePointer(true, 1.2) && state.IsExpanded);
            Assert(!state.ObservePointer(true, 2) && state.IsExpanded);
            Assert(!state.ObservePointer(false, 3));
            Assert(!state.ObservePointer(false, 3.27) && state.IsExpanded);
            Assert(state.ObservePointer(false, 3.29) && !state.IsExpanded);
        });
        Check("manual collapse waits for exit and reentry", () =>
        {
            var state = new IslandInteraction(); state.ToggleHeader(); state.Collapse();
            for (var i = 0; i < 20; i++) Assert(!state.ObservePointer(true, i));
            Assert(!state.ObservePointer(false, 21) && !state.IsExpanded);
            Assert(state.ObservePointer(true, 22) && state.IsExpanded);
        });
        Check("explicit expansion accepts a compact header press without pinning or hover reentry", () =>
        {
            var state = new IslandInteraction();
            state.Collapse();
            state.Expand();
            Assert(state.IsExpanded && !state.IsPinned);
            Assert(!state.ObservePointer(false, 0));
            Assert(state.ObservePointer(false, .3) && !state.IsExpanded);
        });
        Check("explicit expansion clears pending departure and preserves pinning", () =>
        {
            var state = new IslandInteraction();
            state.Expand();
            state.ObservePointer(false, 1);
            state.Expand();
            Assert(!state.ObservePointer(false, 2) && state.IsExpanded);
            state.TogglePin();
            state.Expand();
            Assert(state.IsPinned && state.IsExpanded);
            state.BeginAdjustment();
            state.Expand();
            Assert(state.IsAdjusting && !state.IsExpanded);
        });
        Check("pin blocks auto-collapse and unpin rechecks the pointer", () =>
        {
            var state = new IslandInteraction(); state.ToggleHeader();
            state.ObservePointer(false, 0); state.ObservePointer(false, 1);
            Assert(state.IsExpanded); state.TogglePin();
            state.ObservePointer(false, 2); Assert(state.ObservePointer(false, 2.3) && !state.IsExpanded);
        });
        Check("menus cancel a pending collapse", () =>
        {
            var state = new IslandInteraction(); Assert(state.ObservePointer(true, 0));
            state.ObservePointer(false, 1); state.SetMenuOpen(true); state.ObservePointer(false, 2);
            Assert(state.IsExpanded); state.SetMenuOpen(false); state.ObservePointer(false, 3);
            Assert(state.ObservePointer(false, 3.3) && !state.IsExpanded);
        });
        Check("menu interaction suspends immediate hover entry until the menu closes", () =>
        {
            var state = new IslandInteraction(); state.SetMenuOpen(true);
            Assert(!state.ObservePointer(true, 0) && !state.IsExpanded);
            Assert(!state.ObservePointer(false, 1) && !state.IsExpanded);
            Assert(!state.ObservePointer(true, 2) && !state.IsExpanded);
            state.SetMenuOpen(false);
            Assert(state.ObservePointer(true, 3) && state.IsExpanded);
        });
        Check("position adjustment and hidden windows suspend hover", () =>
        {
            var state = new IslandInteraction(); state.BeginAdjustment();
            state.ObservePointer(true, 0); state.ObservePointer(true, 1); Assert(!state.IsExpanded);
            state.EndAdjustment(); Assert(!state.ObservePointer(true, 2) && !state.IsExpanded);
            Assert(!state.ObservePointer(false, 3));
            Assert(state.ObservePointer(true, 4) && state.IsExpanded);
            state.SetVisible(false); state.ObservePointer(true, 5); Assert(!state.IsExpanded);
            state.SetVisible(true); Assert(state.ObservePointer(true, 6) && state.IsExpanded);
        });
        Check("expansion and collapse interpolate without overshoot", () =>
        {
            var a = new RectD(100, 0, 224, 36); var b = new RectD(2, 0, 420, 336);
            foreach (var p in new[] { 0d, 0.1, 0.5, 1d })
            {
                var rect = RectD.Interpolate(a, b, p);
                Assert(rect.Width is >= 224 and <= 420 && rect.Height is >= 36 and <= 336 && rect.CenterX == 212);
            }
            Assert(a.Union(b).Contains(new(10, 300)));
        });
    }

    private static void HeaderDragChecks()
    {
        Check("a stationary header press remains a click", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.IsPressed && !gesture.IsDragging);
            Assert(!gesture.Move(new(100, 200)));
            Assert(gesture.Release(new(100, 200)) == HeaderRelease.Click);
            Assert(!gesture.IsPressed && !gesture.IsDragging);
        });
        Check("the first one-pixel movement starts dragging immediately", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Move(new(101, 200)));
            Assert(gesture.IsPressed && gesture.IsDragging);
            Assert(!gesture.Move(new(130, 220)));
            Assert(gesture.Release(new(130, 220)) == HeaderRelease.Drag);
            Assert(!gesture.IsPressed && !gesture.IsDragging);
        });
        Check("either movement axis and either direction can start dragging", () =>
        {
            foreach (var offset in new[] { new PointD(1, 0), new PointD(-1, 0), new PointD(0, 1), new PointD(0, -1) })
            {
                var gesture = new HeaderDragGesture();
                gesture.Press(new(-500, -200));
                Assert(gesture.Move(new(-500 + offset.X, -200 + offset.Y)));
                Assert(gesture.Release(new(-500, -200)) == HeaderRelease.Drag);
            }
        });
        Check("repeated stationary samples never turn a held press into a drag", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            for (var sample = 0; sample < 1000; sample++)
                Assert(!gesture.Move(new(100, 200)) && !gesture.IsDragging);
            Assert(gesture.Release(new(100, 200)) == HeaderRelease.Click);
        });
        Check("any actual coordinate change starts a drag without a distance threshold", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Move(new(100.01, 200)));
            Assert(gesture.Release(new(100.01, 200)) == HeaderRelease.Drag);
        });
        Check("a press followed by a swipe and release is a drag", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Move(new(130, 200)));
            Assert(gesture.Release(new(130, 200)) == HeaderRelease.Drag);
        });
        Check("moving away and back before release remains a drag", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Move(new(99, 200)));
            Assert(!gesture.Move(new(100, 200)));
            Assert(gesture.Release(new(100, 200)) == HeaderRelease.Drag);
        });
        Check("repeated move samples report a drag start exactly once", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Move(new(101, 200)));
            Assert(!gesture.Move(new(101, 200)));
            Assert(!gesture.Move(new(102, 200)));
            Assert(gesture.IsDragging);
            Assert(gesture.Release(new(102, 200)) == HeaderRelease.Drag);
        });
        Check("release samples final movement when a move event was not observed", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Release(new(101, 200)) == HeaderRelease.Drag);
            Assert(!gesture.IsPressed && !gesture.IsDragging);
            Assert(gesture.Release(new(101, 200)) == HeaderRelease.None);
        });
        Check("cancel clears pending and active gestures and a new press works", () =>
        {
            var gesture = new HeaderDragGesture();
            Assert(!gesture.Move(new(100, 200)));
            Assert(gesture.Release(new(100, 200)) == HeaderRelease.None);
            foreach (var startDrag in new[] { false, true })
            {
                gesture.Press(new(100, 200));
                if (startDrag) Assert(gesture.Move(new(101, 200)));
                gesture.Cancel();
                Assert(!gesture.IsPressed && !gesture.IsDragging);
                Assert(!gesture.Move(new(120, 200)));
                Assert(gesture.Release(new(120, 200)) == HeaderRelease.None);
                gesture.Press(new(200, 300));
                Assert(gesture.Release(new(200, 300)) == HeaderRelease.Click);
            }
        });
        Check("a replacement press resets its origin and movement history", () =>
        {
            var gesture = new HeaderDragGesture();
            gesture.Press(new(100, 200));
            Assert(gesture.Move(new(101, 200)));
            gesture.Press(new(300, 400));
            Assert(gesture.IsPressed && !gesture.IsDragging);
            Assert(!gesture.Move(new(300, 400)));
            Assert(gesture.Release(new(300, 400)) == HeaderRelease.Click);
            Assert(gesture.Release(new(300, 400)) == HeaderRelease.None);
        });
    }

    private static async Task StoreChecks()
    {
        await CheckAsync("refresh coalesces concurrent triggers", async () =>
        {
            var completion = new TaskCompletionSource<QuotaSnapshot>();
            var client = new StubClient { Behavior = _ => completion.Task };
            await using var store = new QuotaStore(client);
            var first = store.RefreshAsync(); var second = store.RefreshAsync();
            Assert(client.Reads == 1 && store.IsRefreshing);
            var idle = store.WaitForIdleAsync(); Assert(!idle.IsCompleted);
            completion.SetResult(Parse(Weekly)); await Task.WhenAll(first, second);
            await idle;
            Assert(!store.IsRefreshing && store.Snapshot is not null);
        });
        await CheckAsync("failures retain old data and back off to a five-minute cap", async () =>
        {
            var clock = new TestClock(Now);
            var client = new StubClient();
            await using var store = new QuotaStore(client, clock: clock);
            await store.RefreshAsync(); var snapshot = store.Snapshot;
            client.Behavior = _ => Task.FromException<QuotaSnapshot>(new QuotaException(QuotaError.Disconnected));
            foreach (var expected in new[] { 60, 120, 240, 300, 300 })
            {
                await store.RefreshAsync();
                Assert(store.Snapshot == snapshot && store.IsStale && store.NextAttempt == clock.GetUtcNow().AddSeconds(expected));
            }
            var count = client.Reads; await store.RefreshAsync(scheduled: true); Assert(count == client.Reads);
            clock.Advance(TimeSpan.FromMinutes(6)); await store.RefreshAsync(scheduled: true); Assert(client.Reads == count + 1);
            client.Behavior = _ => Task.FromResult(Parse(Weekly)); await store.RefreshAsync();
            Assert(store.Failures == 0 && store.ErrorMessage is null && store.NextAttempt == clock.GetUtcNow().AddSeconds(30));
        });
        await CheckAsync("stale time and explicit demo behavior", async () =>
        {
            var clock = new TestClock(Now); var client = new StubClient();
            await using var store = new QuotaStore(client, clock: clock); await store.RefreshAsync();
            Assert(!store.IsStale); clock.Advance(TimeSpan.FromSeconds(91)); Assert(store.IsStale);
            await using var demo = new QuotaStore(client, isDemo: true, clock: clock); var count = client.Reads;
            await demo.RefreshAsync(); Assert(client.Reads == count && demo.Snapshot?.PlanName == "演示");
        });
        await CheckAsync("shutdown cancels an outstanding refresh", async () =>
        {
            var client = new StubClient { Behavior = async token => { await Task.Delay(Timeout.Infinite, token); return Parse(Weekly); } };
            var store = new QuotaStore(client); var pending = store.RefreshAsync();
            await store.DisposeAsync(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!store.IsRefreshing && client.Disposed);
        });
    }

    private static QuotaClient FakeClient(string scenario, string? pidFile = null, TimeSpan? timeout = null)
    {
        // Tests can run via the apphost or `dotnet <dll>`.
        var host = Environment.ProcessPath!;
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args.Add(typeof(Program).Assembly.Location);
        args.AddRange(["--fake-server", scenario]);
        if (pidFile is not null) args.Add(pidFile);
        return new(() => host, timeout ?? TimeSpan.FromSeconds(3), args);
    }
    private static async Task TransportChecks()
    {
        await CheckAsync("real pipe handshake, UTF-8, notification, and rejecting a server approval request", async () =>
        {
            await using var client = FakeClient("normal"); var notifications = 0;
            client.QuotaChanged += () => Interlocked.Increment(ref notifications);
            var first = await client.FetchAsync(); var second = await client.FetchAsync();
            Assert(first.MainBucket.Name == "中文额度" && second.MainBucket.Primary?.RemainingPercent == 61);
            Assert(notifications == 2);
        });
        await CheckAsync("server exit fails promptly and reconnects on the next read", async () =>
        {
            await using var client = FakeClient("exit");
            for (var i = 0; i < 2; i++)
            {
                try { await client.FetchAsync(); throw new Exception("Expected disconnect"); }
                catch (QuotaException e) { Assert(e.Kind == QuotaError.Disconnected); }
            }
        });
        await CheckAsync("request timeout kills only the owned child process", async () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "CodexIslandChecks-" + Guid.NewGuid().ToString("N") + ".pid");
            await using var client = FakeClient("timeout", file, TimeSpan.FromMilliseconds(600));
            try { await client.FetchAsync(); throw new Exception("Expected timeout"); }
            catch (QuotaException e) { Assert(e.Kind == QuotaError.TimedOut); }
            var pid = int.Parse(File.ReadAllText(file));
            await Task.Delay(100);
            try { using var process = Process.GetProcessById(pid); Assert(process.HasExited); }
            catch (ArgumentException) { }
            File.Delete(file);
        });
        await CheckAsync("cancelling a read closes its connection", async () =>
        {
            await using var client = FakeClient("timeout"); using var token = new CancellationTokenSource(400);
            try { await client.FetchAsync(token.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
        });
        await CheckAsync("oversized process output is rejected", async () =>
        {
            await using var client = FakeClient("oversize");
            try { await client.FetchAsync(); throw new Exception("Expected invalid response"); }
            catch (QuotaException e) { Assert(e.Kind == QuotaError.InvalidResponse); }
        });
        await CheckAsync("invalid explicit executable does not fall back silently", async () =>
        {
            Assert(CodexLocator.Find(@"C:\nonexistent-codex-island-check\codex.exe") is null);
            Assert(CodexLocator.Find(@"C:\nonexistent-codex-island-check\codex.cmd") is null);
            await using var client = new QuotaClient(() => null);
            try { await client.FetchAsync(); throw new Exception("Expected missing Codex"); }
            catch (QuotaException e) { Assert(e.Kind == QuotaError.MissingCodex); }
        });
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan time) => current += time;
    }
    private sealed class StubClient : IQuotaClient
    {
        public event Action? QuotaChanged { add { } remove { } }
        public Func<CancellationToken, Task<QuotaSnapshot>> Behavior { get; set; } = _ => Task.FromResult(Parse(Weekly));
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken = default) { Reads++; return Behavior(cancellationToken); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

internal static class FakeServer
{
    internal static async Task<int> RunAsync(string scenario, string? pidFile)
    {
        if (pidFile is not null) await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString());
        var initialized = false;
        var handshake = false;
        var reads = 0;
        int? awaitingReadId = null;
        async Task Send(object value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
            var output = Console.OpenStandardOutput();
            for (var offset = 0; offset < bytes.Length; offset += 7)
            {
                await output.WriteAsync(bytes.AsMemory(offset, Math.Min(7, bytes.Length - offset)));
                await output.FlushAsync();
            }
        }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("error", out var rejection))
            {
                if (rejection.GetProperty("code").GetInt32() != -32601 || awaitingReadId is null) return 12;
                await Send(new { id = awaitingReadId.Value, result = new { rateLimits = new { limitName = "中文额度", primary = new { usedPercent = 37 + ++reads, windowDurationMins = 10080 } } } });
                awaitingReadId = null;
                continue;
            }
            var method = message.GetProperty("method").GetString();
            if (method == "initialize")
            {
                if (handshake) return 13;
                handshake = true;
                await Send(new { id = message.GetProperty("id").GetInt32(), result = new { } });
            }
            else if (method == "initialized") { if (!handshake) return 14; initialized = true; }
            else if (method == "account/rateLimits/read" && initialized)
            {
                if (scenario == "exit") return 7;
                if (scenario == "timeout") { await Task.Delay(Timeout.Infinite); return 0; }
                if (scenario == "oversize")
                {
                    await Console.OpenStandardOutput().WriteAsync(new byte[4 * 1024 * 1024 + 1]);
                    await Console.OpenStandardOutput().FlushAsync();
                    await Task.Delay(Timeout.Infinite); return 0;
                }
                awaitingReadId = message.GetProperty("id").GetInt32();
                await Send(new { method = "account/rateLimits/updated", @params = new { } });
                await Send(new { id = 9999, method = "item/commandExecution/requestApproval", @params = new { } });
            }
            else return 15; // Any chat/auth request is a test failure.
        }
        return 0;
    }
}
