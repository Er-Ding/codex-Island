using System.Text.Json.Nodes;

namespace CodexIsland.Core;

/// <summary>Retains patch addresses but drops private reasoning, user inputs, tool arguments and outputs.</summary>
internal static class TaskActivityProjection
{
    private static readonly HashSet<string> StateFields = ["title", "generatedTitle", "resumeState", "threadRuntimeStatus", "requests", "turns", "turnHistory", "turnsPagination", "source", "cwd", "rolloutPath", "sideConversation", "sideConversationParentNavigationPath"];
    private static readonly HashSet<string> TurnFields = ["turnId", "status", "items"];
    private static readonly HashSet<string> ItemFields = ["type", "status", "phase", "text", "plan"];

    public static JsonObject Summary(JsonObject state)
    {
        var result = Select(state, StateFields);
        if (state["turnsPagination"] is JsonObject pagination) result["turnsPagination"] = Select(pagination, ["hasLoadedOldest"]);
        if (state["turns"] is JsonArray turns) result["turns"] = MapTurns(turns);
        if (state["requests"] is JsonArray requests) result["requests"] = new JsonArray(requests.Select(request => request is JsonObject value ? Select(value, ["method"]) : new JsonObject()).Cast<JsonNode?>().ToArray());
        if (state["turnHistory"] is JsonObject historyState && historyState["history"] is JsonObject history)
        {
            var reduced = Select(history, ["islands"]);
            if (history["entitiesByKey"] is JsonObject entities)
            {
                var reducedEntities = new JsonObject();
                foreach (var entry in entities) reducedEntities[entry.Key] = entry.Value is JsonObject turn ? SummaryTurn(turn) : new JsonObject();
                reduced["entitiesByKey"] = reducedEntities;
            }
            var reducedState = Select(historyState, ["kind"]);
            reducedState["history"] = reduced;
            result["turnHistory"] = reducedState;
        }
        return result;
    }

    private static JsonArray MapTurns(JsonArray turns) => new(turns.Select(turn => turn is JsonObject value ? SummaryTurn(value) : new JsonObject()).Cast<JsonNode?>().ToArray());

    private static JsonObject SummaryTurn(JsonObject turn)
    {
        var result = Select(turn, TurnFields);
        if (turn["items"] is not JsonArray items) return result;
        result["items"] = new JsonArray(items.Select(item =>
        {
            if (item is not JsonObject value) return new JsonObject();
            var reduced = Select(value, ItemFields);
            if (value["text"].Text() is { } text) reduced["text"] = value["type"].Text() == "agentMessage" ? TaskJson.Compact(text, 1000) : "";
            if (value["plan"] is JsonArray plan) reduced["plan"] = new JsonArray(plan.Select(step => step is JsonObject entry ? Select(entry, ["step", "status"]) : new JsonObject()).Cast<JsonNode?>().ToArray());
            return reduced;
        }).Cast<JsonNode?>().ToArray());
        return result;
    }

    private static JsonObject Select(JsonObject source, HashSet<string> fields)
    {
        var result = new JsonObject();
        foreach (var entry in source) if (fields.Contains(entry.Key)) result[entry.Key] = entry.Value?.DeepClone();
        return result;
    }

    public static bool NeedsPatch(JsonObject patch)
    {
        if (patch["path"] is not JsonArray path || path.Count == 0) return true;
        if (path[0].Text() is not { } root || !StateFields.Contains(root)) return false;
        if (root == "turnsPagination" && path.Count > 1) return path[1].Text() == "hasLoadedOldest";
        if (root == "requests" && path.Count > 2) return path[2].Text() == "method";
        int? turnOffset = null;
        if (root == "turns" && path.Count > 2) turnOffset = 2;
        if (root == "turnHistory" && path.Count > 2)
        {
            if (path[1].Text() != "history" || path[2].Text() is not ("islands" or "entitiesByKey")) return false;
            if (path[2].Text() == "entitiesByKey" && path.Count > 4) turnOffset = 4;
        }
        if (turnOffset is { } offset && path[offset].Text() is { } field)
        {
            if (!TurnFields.Contains(field)) return false;
            if (field == "items" && path.Count > offset + 2) return path[offset + 2].Text() is { } itemField && ItemFields.Contains(itemField);
        }
        return true;
    }

    public static JsonObject Apply(JsonObject state, JsonArray patches)
    {
        JsonNode? value = state.DeepClone();
        foreach (var entry in patches)
        {
            if (entry is not JsonObject patch) throw new InvalidDataException("Invalid task patch");
            if (!NeedsPatch(patch)) continue;
            var operation = patch["op"].Text();
            if (operation is not ("add" or "replace" or "remove") || patch["path"] is not JsonArray path
                || operation != "remove" && !patch.ContainsKey("value")) throw new InvalidDataException("Invalid task patch");
            value = Replace(value, path, 0, operation, patch["value"]);
        }
        return value as JsonObject ?? throw new InvalidDataException("Task state must remain an object");
    }

    private static JsonNode? Replace(JsonNode? value, JsonArray path, int depth, string operation, JsonNode? replacement)
    {
        if (depth == path.Count) return operation != "remove" ? replacement?.DeepClone() : throw new InvalidDataException("Cannot remove task root");
        if (depth > 256) throw new InvalidDataException("Task patch depth limit");
        var leaf = depth + 1 == path.Count;
        if (value is JsonObject obj && path[depth].Text() is { } key)
        {
            if (leaf)
            {
                if (operation == "remove") obj.Remove(key);
                else
                {
                    if (operation == "replace" && !obj.ContainsKey(key)) throw new InvalidDataException("Missing task field");
                    obj[key] = replacement?.DeepClone();
                }
            }
            else
            {
                if (!obj.ContainsKey(key)) throw new InvalidDataException("Missing task field");
                var child = obj[key];
                obj.Remove(key);
                obj[key] = Replace(child, path, depth + 1, operation, replacement);
            }
            return obj;
        }
        if (value is JsonArray array && path[depth].Integer() is { } index && index >= 0)
        {
            if (leaf && operation == "add" && index <= array.Count) array.Insert(index, replacement?.DeepClone());
            else
            {
                if (index >= array.Count) throw new InvalidDataException("Missing task array index");
                if (leaf && operation == "remove") array.RemoveAt(index);
                else
                {
                    var child = array[index];
                    array[index] = null;
                    array[index] = Replace(child, path, depth + 1, operation, replacement);
                }
            }
            return array;
        }
        throw new InvalidDataException("Invalid task patch path");
    }
}
