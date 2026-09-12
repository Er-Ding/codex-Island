using System.Text.Json.Nodes;

namespace CodexIsland.Core;

/// <summary>
/// Desktop metadata supplies identities to probe, never task execution state.
/// In Desktop 26.908 the "local:" client-id key is a UI scope, even for SSH tasks.
/// Actual hosts come from project/route metadata or a bounded probe of known devices.
/// </summary>
internal static class TaskThreadDiscovery
{
    internal readonly record struct Candidate(string HostId, string ThreadId, bool IsLocated = false);

    internal static IEnumerable<Candidate> FromState(JsonObject state)
    {
        var hosts = new HashSet<string>(StringComparer.Ordinal) { "local" };
        foreach (var connection in state["codex-managed-remote-connections"].Objects())
            if (Host(connection["hostId"].Text()) is { } host) hosts.Add(host);

        var unscoped = new HashSet<string>(StringComparer.Ordinal);
        var explicitCandidates = new HashSet<Candidate>();
        void Identity(string? value)
        {
            if (Thread(value) is { } id) unscoped.Add(id);
        }
        void Located(string? value, string? hostValue)
        {
            if (Thread(value) is not { } id) return;
            if (Host(hostValue) is { } host) { hosts.Add(host); explicitCandidates.Add(new(host, id)); }
            else unscoped.Add(id);
        }

        if (state["thread-project-assignments"] is JsonObject assignments)
            foreach (var assignment in assignments)
            {
                if (assignment.Value is not JsonObject project) continue;
                Located(assignment.Key, project["projectKind"].Text() == "local" ? "local" : project["hostId"].Text());
            }
        if (state["projectless-thread-ids"] is JsonArray projectless)
            foreach (var id in projectless) Identity(id.Text());

        if (state["electron-persisted-atom-state"] is JsonObject atoms)
            foreach (var atom in atoms)
            {
                const string clientPrefix = "thread-client-id-v1:";
                const string routesPrefix = "thread-tab-routes-v1:";
                if (atom.Key.StartsWith(clientPrefix, StringComparison.Ordinal))
                {
                    var scope = Uri.UnescapeDataString(atom.Key[clientPrefix.Length..]);
                    // Cloud/route scopes are not App Server conversation identities.
                    if (scope.StartsWith("local:", StringComparison.Ordinal)) Identity(scope[6..]);
                    continue;
                }
                if (!atom.Key.StartsWith(routesPrefix, StringComparison.Ordinal)
                    || atom.Value is not JsonObject tabs || tabs["version"].Integer() != 1) continue;
                var parent = Uri.UnescapeDataString(atom.Key[routesPrefix.Length..]);
                Identity(parent);
                foreach (var route in tabs["routes"].Objects())
                {
                    if (route["params"] is not JsonObject parameters) continue;
                    var host = parameters["hostId"].Text();
                    if (Host(host) is not null) Located(parent, host);
                    // Only known conversation fields; terminal session IDs and tab IDs are unrelated.
                    switch (route["kind"].Text())
                    {
                        case "review":
                            Located(parameters["conversationId"].Text(), host);
                            Located(parameters["lastTurnConversationId"].Text(), host);
                            break;
                        case "background-agent":
                            Located(parameters["conversationId"].Text(), host);
                            break;
                        case "subagents":
                            Located(parameters["selectedConversationId"].Text(), host);
                            break;
                        case "pull-request-fix-automation":
                            Located(parameters["targetConversationId"].Text(), host);
                            break;
                    }
                }
            }

        foreach (var entry in explicitCandidates) yield return entry with { IsLocated = true };
        // Repeated IDs on different hosts remain distinct until a real owner confirms a snapshot.
        foreach (var id in unscoped)
            foreach (var host in hosts)
                if (!explicitCandidates.Contains(new(host, id))) yield return new(host, id);
    }

    private static string? Thread(string? value) => Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : null;
    private static string? Host(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl) ? value : null;
}
