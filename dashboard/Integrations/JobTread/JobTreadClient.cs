using System.Text;
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

        // Send with a Content-Length (not chunked); JobTread rejects some chunked requests as too large.
        var json = body.ToJsonString();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var started = DateTime.UtcNow;
        using var response = await http.PostAsync("pave", content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("JobTread {Query} {Status} in {Ms} ms ({Bytes} bytes sent)", name, (int)response.StatusCode, (DateTime.UtcNow - started).TotalMilliseconds, json.Length);

        if (!response.IsSuccessStatusCode)
            throw new JobTreadException($"JobTread returned {(int)response.StatusCode} for {name} ({json.Length:N0} bytes sent): {Trim(text)}");

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
