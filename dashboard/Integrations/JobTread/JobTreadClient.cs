using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PurpleDashboard.Integrations.JobTread;

public sealed class JobTreadClient(HttpClient http, IOptions<JobTreadOptions> options, ILogger<JobTreadClient> logger) : IJobTreadClient
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.GrantKey);

    public async Task<JsonNode> QueryAsync(string name, JsonObject query, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new JobTreadException("JobTread isn't connected yet: the JOBTREAD_API_KEY setting is missing.");

        var body = new JsonObject { ["query"] = query.DeepClone() };
        ((JsonObject)body["query"]!)["$"] = new JsonObject { ["grantKey"] = options.Value.GrantKey };

        var started = DateTime.UtcNow;
        using var response = await http.PostAsJsonAsync("pave", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("JobTread {Query} {Status} in {Ms} ms", name, (int)response.StatusCode, (DateTime.UtcNow - started).TotalMilliseconds);

        if (!response.IsSuccessStatusCode)
            throw new JobTreadException($"JobTread returned {(int)response.StatusCode}: {Trim(text)}");

        try
        {
            return JsonNode.Parse(text) ?? throw new JobTreadException("JobTread returned an empty response.");
        }
        catch (System.Text.Json.JsonException e)
        {
            // Pave reports query errors as plain text.
            throw new JobTreadException($"JobTread error: {Trim(text)}", e);
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
