using System.Text.Json.Nodes;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>Small helpers for reading Pave responses, which use null freely.</summary>
public static class JobTreadJson
{
    public static string? Str(this JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static decimal? Dec(this JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    public static DateOnly? Date(this JsonNode? node, string key) =>
        DateOnly.TryParse(node.Str(key), out var d) ? d : null;

    public static IEnumerable<JsonNode> Nodes(this JsonNode? connection) =>
        connection?["nodes"] is JsonArray a ? a.OfType<JsonNode>() : [];

    public static IEnumerable<JsonNode> GroupRows(this JsonNode? connection) =>
        connection?["withValues"] is JsonArray a ? a.OfType<JsonNode>() : [];

    /// <summary>Custom field values keyed by custom field id.</summary>
    public static Dictionary<string, string> CustomFields(this JsonNode? job)
    {
        var result = new Dictionary<string, string>();
        foreach (var cfv in job?["customFieldValues"].Nodes() ?? [])
        {
            var id = cfv["customField"].Str("id");
            var value = cfv.Str("value");
            if (id is not null && !string.IsNullOrEmpty(value)) result[id] = value;
        }
        return result;
    }
}
