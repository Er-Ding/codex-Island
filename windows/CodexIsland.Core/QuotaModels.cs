using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexIsland.Core;

public sealed record QuotaWindow(
    double UsedPercent, int? WindowDurationMins = null, double? ResetsAt = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public string PercentText => RemainingPercent is > 0 and < 1 ? "<1%"
        : Math.Floor(RemainingPercent).ToString(CultureInfo.InvariantCulture) + "%";
    public string PeriodTitle => WindowDurationMins switch
    {
        10080 => "本周",
        > 0 and var n when n % 1440 == 0 => $"{n / 1440}天",
        > 0 and var n when n % 60 == 0 => $"{n / 60}小时",
        > 0 and var n => $"{n}分钟",
        _ => "当前周期"
    };

    public string ResetText(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset) return "恢复时间暂未提供";
        var seconds = reset - now.ToUnixTimeSeconds();
        if (seconds <= 0) return "已到恢复时间，等待刷新";
        // Keep untrusted, implausibly large timestamps out of integer conversions.
        if (seconds > TimeSpan.FromDays(3650).TotalSeconds) return "恢复时间暂未提供";
        var minutes = Math.Max(1, (long)Math.Ceiling(seconds / 60));
        return minutes switch
        {
            >= 1440 => $"{minutes / 1440}天{minutes % 1440 / 60}小时后恢复",
            >= 60 => $"{minutes / 60}小时{minutes % 60}分钟后恢复",
            _ => $"{minutes}分钟后恢复"
        };
    }
}

public sealed record QuotaBucket(string Id, string Name, QuotaWindow? Primary, QuotaWindow? Secondary)
{
    public IReadOnlyList<QuotaWindow> Windows => new[] { Primary, Secondary }.OfType<QuotaWindow>().ToArray();
}

public sealed record QuotaSnapshot(IReadOnlyList<QuotaBucket> Buckets, DateTimeOffset FetchedAt, string? PlanName)
{
    public QuotaBucket MainBucket => Buckets.FirstOrDefault(b => b.Id == "codex") ?? Buckets[0];

    public static QuotaSnapshot Demo(DateTimeOffset now) => new([
        new("codex", "Codex", new(28, 300, now.AddHours(2.4).ToUnixTimeSeconds()),
            new(46, 10080, now.AddDays(2.7).ToUnixTimeSeconds())),
        new("additional", "额外模型额度 · 示例", new(82, 10080, now.AddDays(3).ToUnixTimeSeconds()), null)
    ], now, "演示");
}

public enum QuotaError { MissingCodex, Disconnected, TimedOut, InvalidResponse, NoQuota, Service }

public sealed class QuotaException(QuotaError kind, string? message = null) : Exception(message ?? kind switch
{
    QuotaError.MissingCodex => "未找到 Codex，请先在 Windows 安装并登录，或从托盘选择 Codex 程序。",
    QuotaError.Disconnected => "与 Codex 的连接已断开，稍后会重试。",
    QuotaError.TimedOut => "读取超时，请检查网络后刷新。",
    QuotaError.InvalidResponse => "暂时无法识别 Codex 返回的额度信息。",
    QuotaError.NoQuota => "账号暂未返回订阅额度，请确认已用 ChatGPT 账号登录 Windows 版 Codex。",
    _ => "暂时无法读取额度，请检查 Codex 登录状态和网络。"
})
{
    public QuotaError Kind { get; } = kind;

    // Raw server errors can contain account data. Only these messages reach the UI.
    public static QuotaException FromServer(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("api key") || text.Contains("apikey") || text.Contains("unsupported auth"))
            return new(QuotaError.Service, "当前登录方式未提供订阅额度，请用 ChatGPT 账号登录 Codex。");
        if (text.Contains("401") || text.Contains("auth") || text.Contains("login") || text.Contains("sign in"))
            return new(QuotaError.Service, "请先在 Windows 版 Codex 中登录 ChatGPT 账号，再点刷新。");
        if (text.Contains("429")) return new(QuotaError.Service, "查询过于频繁，稍后会自动重试。");
        return new(QuotaError.Service);
    }
}

public static class QuotaParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record WindowData([property: JsonRequired] double UsedPercent, int? WindowDurationMins, double? ResetsAt);
    private sealed record BucketData(string? LimitId, string? LimitName, WindowData? Primary, WindowData? Secondary, string? PlanType);
    private sealed record Response(BucketData? RateLimits, Dictionary<string, BucketData?>? RateLimitsByLimitId);

    public static QuotaSnapshot Parse(string json, DateTimeOffset now)
    {
        Response response;
        try { response = JsonSerializer.Deserialize<Response>(json, Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new QuotaException(QuotaError.InvalidResponse); }
        var all = response.RateLimitsByLimitId ?? [];
        if (response.RateLimits is { } legacy) all.TryAdd(legacy.LimitId ?? "codex", legacy);
        var keys = all.Keys.OrderBy(k => k == "codex" ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToArray();
        var buckets = new List<QuotaBucket>();
        foreach (var key in keys)
        {
            if (all[key] is not { } bucket || (bucket.Primary is null && bucket.Secondary is null)) continue;
            buckets.Add(new(key, bucket.LimitName ?? (key == "codex" ? "Codex" : key),
                Convert(bucket.Primary), Convert(bucket.Secondary)));
        }
        if (buckets.Count == 0) throw new QuotaException(QuotaError.NoQuota);
        var plan = keys.Select(k => all[k]?.PlanType).FirstOrDefault(p => p is not null);
        return new(buckets, now, PlanLabel(plan));
    }

    private static QuotaWindow? Convert(WindowData? data)
    {
        if (data is null) return null;
        if (!double.IsFinite(data.UsedPercent) || (data.ResetsAt is { } reset && !double.IsFinite(reset)))
            throw new QuotaException(QuotaError.InvalidResponse);
        return new(data.UsedPercent, data.WindowDurationMins, data.ResetsAt);
    }

    private static string? PlanLabel(string? plan) => plan?.ToLowerInvariant() switch
    {
        null => null, "plus" => "Plus", "pro" => "Pro", "free" => "免费版",
        "team" or "business" => "团队版", "enterprise" => "企业版", "edu" => "教育版", _ => "ChatGPT"
    };
}
