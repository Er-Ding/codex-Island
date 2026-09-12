using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexIsland.Core;

/// <summary>Keep only a small plain text tab label, never the prompt context or HTML resources.</summary>
public static class TaskSideChatTitle
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    public static string? From(JsonObject state)
    {
        if (!state["sideConversation"].Boolean() && state["sideConversationParentNavigationPath"].Text() is null) return null;
        var turn = FirstTurn(state);
        var text = (turn?["params"] as JsonObject)?["input"].Objects().FirstOrDefault(item => item["type"].Text() == "text")?["text"].Text();
        if (text is null || Encoding.UTF8.GetByteCount(text) > 1_048_576) return null;
        try
        {
            var markers = Regex.Matches(text, "## My request(?: for Codex)?:", RegexOptions.None, RegexTimeout);
            var prompt = (markers.Count > 0 ? text[(markers[^1].Index + markers[^1].Length)..] : text).Trim();
            if (prompt.Length == 0 || Encoding.UTF8.GetByteCount(prompt) > 8_192) return null;
            // A conservative projection supports ordinary Markdown. The navigator must require an
            // exact accessible label; unsupported syntax fails closed rather than selecting a nearby tab.
            prompt = Regex.Replace(prompt, @"(?m)^\s*```[^\r\n]*\r?\n|(?m)^\s*```\s*$", "", RegexOptions.None, RegexTimeout);
            prompt = Regex.Replace(prompt, @"!?\[([^\]\r\n]*)\]\([^\r\n)]*\)", "$1", RegexOptions.None, RegexTimeout);
            prompt = Regex.Replace(prompt, @"(?m)^\s{0,3}(?:#{1,6}\s+|>\s*|[-+*]\s+|\d+[.)]\s+)", "", RegexOptions.None, RegexTimeout);
            prompt = Regex.Replace(prompt, @"(`+)(.*?)\1", "$2", RegexOptions.None, RegexTimeout);
            prompt = Regex.Replace(prompt, @"(\*\*|__|~~)(.*?)\1", "$2", RegexOptions.None, RegexTimeout);
            prompt = Regex.Replace(prompt, @"(?<!\w)(\*|_)(.*?)\1(?!\w)", "$2", RegexOptions.None, RegexTimeout);
            prompt = Regex.Replace(prompt, @"\\([\\`*_{}\[\]()#+.!>-])", "$1", RegexOptions.None, RegexTimeout);
            var title = TaskJson.Compact(WebUtility.HtmlDecode(prompt), 8192);
            return title.Length == 0 ? null : title;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static JsonObject? FirstTurn(JsonObject state)
    {
        if (state["turnHistory"] is JsonObject historyState && historyState["kind"].Text() == "canonical")
        {
            if (historyState["history"] is not JsonObject history || history["islands"].Objects().FirstOrDefault() is not { } island
                || island["olderBoundary"] is not JsonObject boundary || boundary["status"].Text() != "exhausted"
                || island["entries"].Objects().FirstOrDefault()?["value"].Text() is not { } key) return null;
            return (history["entitiesByKey"] as JsonObject)?[key] as JsonObject;
        }
        if (state["turnsPagination"] is JsonObject pagination && pagination["hasLoadedOldest"] is JsonValue value
            && value.TryGetValue<bool>(out var loaded) && !loaded) return null;
        return state["turns"].Objects().FirstOrDefault();
    }
}
