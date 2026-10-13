using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using static PurpleDashboard.Integrations.JobTread.JobTreadIds;

namespace PurpleDashboard.Modules.Sales;

/// <summary>One new JobTread customer account: id, name, created (Pacific date) and its Lead Source (null when blank).</summary>
public sealed record LeadAccount(string Id, string Name, DateOnly CreatedOn, string? LeadSource);

/// <summary>
/// New JobTread customer accounts created in a period (Accounts, newest first). The counts on Sales → Ads &amp; lead
/// sources come from this same list, so a drill-down always lists exactly the accounts behind a number: BySource = accounts
/// per raw Lead Source value, Total = all of them, Untagged = the ones with no Lead Source.
/// </summary>
public sealed record JobTreadLeads(DateOnly From, DateOnly To, IReadOnlyList<LeadAccount> Accounts)
{
    public IReadOnlyDictionary<string, int> BySource { get; } = Accounts.Where(a => a.LeadSource is not null)
        .GroupBy(a => a.LeadSource!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    public int Total => Accounts.Count;
    public int Tagged => Accounts.Count(a => a.LeadSource is not null);
    public int Untagged => Total - Tagged;
}

/// <summary>
/// Leads from JobTread for Ads &amp; lead sources: the customer accounts created in the period with their Lead Source, one
/// paged list (100 per page, newest first). Read only. Cached like projects (JobTreadFreshness, dropped when an account
/// changes). Details: docs/JOBTREAD.md.
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
        DateTimeOffset startAt = LocalClock.StartOfDayUtc(from), endAt = LocalClock.StartOfDayUtc(to.AddDays(1));
        string start = startAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), end = endAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var accounts = new List<LeadAccount>();
        var seen = new HashSet<string>();
        string? page = null;
        do
        {
            var args = new JsonObject
            {
                ["where"] = new JsonObject { ["and"] = new JsonArray(new JsonArray("type", "customer"), new JsonArray("createdAt", ">=", start), new JsonArray("createdAt", "<", end)) },
                ["size"] = 100,
                ["sortBy"] = JsonNode.Parse("""[{ "field": "createdAt", "order": "desc" }]"""),
            };
            if (page is not null) args["page"] = page;
            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["newAccounts"] = new JsonObject
                    {
                        ["_"] = "accounts",
                        ["$"] = args,
                        ["nextPage"] = new JsonObject(),
                        ["nodes"] = new JsonObject
                        {
                            ["id"] = new JsonObject(),
                            ["name"] = new JsonObject(),
                            ["createdAt"] = new JsonObject(),
                            ["leadSource"] = new JsonObject
                            {
                                ["_"] = "customFieldValues",
                                ["$"] = new JsonObject { ["where"] = new JsonArray(new JsonArray("customField", "id"), CustomerFields.LeadSource), ["size"] = 1 },
                                ["nodes"] = new JsonObject { ["value"] = new JsonObject() },
                            },
                        },
                    },
                },
            };

            var response = await jobTread.QueryAsync(page is null ? "sales-leads" : "sales-leads-more", query, ct);
            var connection = response["organization"]?["newAccounts"]
                ?? throw new JobTreadException("Unexpected JobTread response for leads.");
            foreach (var node in connection.Nodes())
            {
                var id = node.Str("id");
                if (id is null || !seen.Add(id) || !DateTimeOffset.TryParse(node.Str("createdAt"), out var created)) continue;
                // JobTread already filters on createdAt; checking again keeps saved sample responses honest too.
                if (created < startAt || created >= endAt) continue;
                var lead = node["leadSource"].Nodes().FirstOrDefault().Str("value")?.Trim();
                accounts.Add(new LeadAccount(id, node.Str("name") ?? "(no name)", LocalClock.ToLocalDate(created), string.IsNullOrEmpty(lead) ? null : lead));
            }
            page = connection.Str("nextPage");
        } while (page is not null);

        return new JobTreadLeads(from, to, accounts.OrderByDescending(a => a.CreatedOn).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
