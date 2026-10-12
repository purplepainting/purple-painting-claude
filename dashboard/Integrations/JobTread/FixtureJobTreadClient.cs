using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// Local development only. Answers each query from {FixtureDirectory}/{name}.json so pages can
/// be worked on without a grant key. Never used in Azure.
/// </summary>
public sealed class FixtureJobTreadClient(IOptions<JobTreadOptions> options) : IJobTreadClient
{
    public bool IsConfigured => true;

    public async Task<JsonNode> QueryAsync(string name, JsonObject query, CancellationToken ct = default)
    {
        var path = Path.Combine(options.Value.FixtureDirectory!, name + ".json");
        if (!File.Exists(path)) throw new JobTreadException($"No fixture for '{name}' at {path}.");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
    }
}
