using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using static PurpleDashboard.Integrations.JobTread.JobTreadIds;

namespace PurpleDashboard.Modules.Sales;

/// <summary>
/// New JobTread customer accounts created in a period, counted by the account's "Lead Source" (BySource, raw JobTread
/// values) plus the total created (Total; the difference has no Lead Source set).
/// </summary>
public sealed record JobTreadLeads(DateOnly From, DateOnly To, IReadOnlyDictionary<string, int> BySource, int Total)
{
    public int Tagged => BySource.Values.Sum();
    public int Untagged => Math.Max(0, Total - Tagged);
}

/// <summary>
/// Leads from JobTread for Ads &amp; lead sources: one grouped query (Lead Source values on accounts created in the
/// period, grouped by value) plus the count of customer accounts created. Read only. Cached like projects
/// (JobTreadFreshness, dropped when an account changes). Details: docs/JOBTREAD.md.
/// </summary>
public sealed class JobTreadLeadsService(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options)
{
    public async Task<JobTreadLeads> GetAsync(DateOnly from, DateOnly to, bool refresh, CancellationToken ct)
    {
        var key = $"sales:jt-leads:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = JobTreadFreshness.For(CacheSignals.Projects);
            entry.AddExpirationToken(CacheSignals.Token(CacheSignals.Projects));
            return await LoadAsync(from, to, ct);
        }))!;
    }

    private async Task<JobTreadLeads> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        // Pacific midnight boundaries, inclusive of the whole "to" day.
        string start = LocalClock.StartOfDayUtc(from).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), end = LocalClock.StartOfDayUtc(to.AddDays(1)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        JsonArray Created(JsonNode field) => new(
            new JsonArray(field.DeepClone(), ">=", start),
            new JsonArray(field.DeepClone(), "<", end));

        var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        string? page = null;
        do
        {
            var args = new JsonObject
            {
                ["where"] = new JsonObject { ["and"] = Created(new JsonArray("account", "createdAt")) },
                ["size"] = 100,
                ["group"] = JsonNode.Parse("""{ "by": ["value"], "aggs": { "n": { "count": "id" } } }"""),
            };
            if (page is not null) args["page"] = page;
            var query = new JsonObject
            {
                ["customField"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = CustomerFields.LeadSource },
                    ["byValue"] = new JsonObject { ["_"] = "customFieldValues", ["$"] = args, ["withValues"] = new JsonObject(), ["nextPage"] = new JsonObject() },
                },
            };
            if (page is null)
            {
                var accountsWhere = Created(JsonValue.Create("createdAt")!);
                accountsWhere.Insert(0, new JsonArray("type", "customer"));
                query["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["newAccounts"] = new JsonObject
                    {
                        ["_"] = "accounts",
                        ["$"] = new JsonObject { ["where"] = new JsonObject { ["and"] = accountsWhere } },
                        ["count"] = new JsonObject(),
                    },
                };
            }

            var response = await jobTread.QueryAsync(page is null ? "sales-leads" : "sales-leads-more", query, ct);
            var connection = response["customField"]?["byValue"]
                ?? throw new JobTreadException("Unexpected JobTread response for leads.");
            if (page is null) total = (int)(response["organization"]?["newAccounts"].Dec("count") ?? 0);
            foreach (var row in connection.GroupRows())
            {
                var value = row.Str("value")?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                bySource[value] = bySource.GetValueOrDefault(value) + (int)(row.Dec("n") ?? 0);
            }
            page = connection.Str("nextPage");
        } while (page is not null);

        return new JobTreadLeads(from, to, bySource, Math.Max(total, bySource.Values.Sum()));
    }
}
