using System.Text.Json;
using System.Windows;
using CodexIsland.Core;

namespace CodexIsland.Windows;

internal sealed record Options(bool Demo, bool Expanded, bool CheckQuota, bool DiagnoseScreen,
    string? CodexPath, string? SmokeDirectory, bool Help)
{
    internal static Options Parse(string[] args)
    {
        bool demo = false, expanded = false, check = false, diagnose = false, help = false;
        string? path = null, smoke = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo": demo = true; break;
                case "--expanded": expanded = true; break;
                case "--check-quota": check = true; break;
                case "--diagnose-screen": diagnose = true; break;
                case "--help" or "-h": help = true; break;
                case "--codex-path" when i + 1 < args.Length: path = args[++i]; break;
                case "--smoke-test" when i + 1 < args.Length: smoke = Path.GetFullPath(args[++i]); break;
                default: throw new ArgumentException("参数无效。使用 --help 查看可用参数。");
            }
        }
        return new(demo, expanded, check, diagnose, path, smoke, help);
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options? options = null;
        try
        {
            options = Options.Parse(args);
            if (options.Help || options.CheckQuota || options.DiagnoseScreen || options.SmokeDirectory is not null)
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
            if (options.Help)
            {
                Console.WriteLine("CodexIsland [--demo] [--expanded] [--codex-path PATH]\n  --check-quota       Read real account quotas and exit\n  --diagnose-screen   Print screen geometry and exit\n  --smoke-test DIR    Run isolated demo UI checks and save previews");
                return 0;
            }
            if (options.CheckQuota) return CheckQuotaAsync(options).GetAwaiter().GetResult();
            if (options.DiagnoseScreen)
            {
                Console.WriteLine(JsonSerializer.Serialize(NativeMethods.Displays().Select((d, i) => new
                {
                    screen = i + 1, primary = d.IsPrimary, bounds = d.Bounds, workArea = d.WorkArea, dpiScale = d.DpiScale
                }), new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (options.SmokeDirectory is not null) return new IslandApplication(options).Run();
            using var singleton = new Mutex(true, @"Local\CodexIsland.Windows.v1", out var first);
            if (!first)
            {
                // A desktop app or another window can have the same title. Only
                // this application handles our registered message, even when hidden.
                NativeMethods.PostMessage(new nint(0xffff), NativeMethods.ShowMessage, 0, 0);
                return 0;
            }
            try { return new IslandApplication(options).Run(); }
            finally { singleton.ReleaseMutex(); }
        }
        catch (Exception e)
        {
            if (options?.SmokeDirectory is { } output)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "failure.txt"), e.ToString());
            }
            Console.Error.WriteLine(e is QuotaException or ArgumentException ? e.Message : $"Codex Island 启动失败：{e.GetType().Name}");
            return 1;
        }
    }

    private static async Task<int> CheckQuotaAsync(Options options)
    {
        var settings = new SettingsStore().Load();
        await using var client = new QuotaClient(() => CodexLocator.Find(options.CodexPath ?? settings.CodexPath));
        var snapshot = await client.FetchAsync();
        Console.WriteLine("fetchedAt=" + snapshot.FetchedAt.ToString("O"));
        foreach (var bucket in snapshot.Buckets)
            foreach (var window in bucket.Windows) Console.WriteLine($"{bucket.Id}: {window.PeriodTitle} remaining={window.PercentText}");
        return 0;
    }
}

internal sealed class IslandApplication(Options options) : Application
{
    private QuotaClient? client;
    private QuotaStore? store;
    private IslandWindow? island;
    private TrayIcon? tray;
    private bool stopping;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new SettingsStore(options.SmokeDirectory is { } directory ? Path.Combine(directory, "settings.json") : null);
        var sessionPath = options.CodexPath ?? settings.Load().CodexPath;
        client = new QuotaClient(() => CodexLocator.Find(sessionPath));
        store = new QuotaStore(client, options.Demo || options.SmokeDirectory is not null);
        island = new IslandWindow(store, settings, options.Expanded, options.SmokeDirectory is null);
        MainWindow = island;
        client.QuotaChanged += island.QuotaNotification;
        island.ExitRequested += () => _ = ShutdownAsync();
        island.CodexPathChanged += async () =>
        {
            if (stopping) return;
            sessionPath = island.Settings.CodexPath;
            try
            {
                await client.RestartAsync();
                await store.WaitForIdleAsync();
                if (stopping) return;
                store.ClearSnapshot();
                await store.RefreshAsync();
            }
            catch (OperationCanceledException) when (stopping) { }
            catch (ObjectDisposedException) when (stopping) { }
        };
        island.Show();
        tray = new TrayIcon(island, store, () => _ = ShutdownAsync());
        await store.RefreshAsync();
        if (options.SmokeDirectory is { } output)
        {
            var code = 0;
            try { await SmokeChecks.RunAsync(island, store, settings, output); }
            catch (Exception error)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
                code = 1;
            }
            await ShutdownAsync(code);
        }
    }

    private async Task ShutdownAsync(int code = 0)
    {
        if (stopping) return;
        stopping = true;
        island?.CloseForExit();
        tray?.Dispose(); tray = null;
        if (store is not null) await store.DisposeAsync();
        Shutdown(code);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // Also covers Windows logoff/session shutdown, which bypasses the tray.
        tray?.Dispose();
        client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
