using System.Text.Json.Nodes;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// The one way the dashboard talks to JobTread. Queries are Pave JSON (see docs/JOBTREAD.md).
/// Write methods (mutations) will be added here too, once each write-back is approved.
/// </summary>
public interface IJobTreadClient
{
    /// <param name="name">Short name for logs and local fixtures, e.g. "projects-list".</param>
    /// <param name="query">Pave query without the grant key; the client adds it.</param>
    Task<JsonNode> QueryAsync(string name, JsonObject query, CancellationToken ct = default);

    /// <summary>Same, but with someone's own grant key, so JobTread records the change as them.</summary>
    Task<JsonNode> QueryAsAsync(string name, JsonObject query, string grantKey, CancellationToken ct = default);

    bool IsConfigured { get; }
}

public sealed class JobTreadException(string message, Exception? inner = null) : Exception(message, inner);
