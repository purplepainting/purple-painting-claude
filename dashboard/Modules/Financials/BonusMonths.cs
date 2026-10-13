using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;

namespace PurpleDashboard.Modules.Financials;

/// <summary>One person's time on a job in a month (from JobTread time entries).</summary>
public sealed record MonthHours(string UserId, string Name, decimal Hours, decimal Cost);

/// <summary>
/// A job's JobTread activity in one calendar month (Pacific): invoices by issue date (approved and pending,
/// net of retention, like the rest of the app), vendor bills by issue date, time entries by start.
/// </summary>
public sealed class JobMonth
{
    public required string JobId { get; init; }
    public string? JobName { get; set; }
    public string? JobNumber { get; set; }
    public decimal Income { get; set; }
    public decimal Bills { get; set; }
    public decimal LaborCost => People.Sum(p => p.Cost);
    public decimal Costs => Bills + LaborCost;
    public decimal Profit => Income - Costs;
    public List<MonthHours> People { get; } = [];
}

/// <summary>
/// Reads per-job, per-month money and hours from JobTread for bonuses. One request per month with three grouped
/// aliases (invoices, bills, time by person), so nothing comes back as raw rows; any alias that has more than
/// 100 groups is paged on its own. Cached like Projects (JobTreadFreshness / CacheSignals.Projects).
/// </summary>
public sealed class BonusMonths(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options)
{
    private const int PageSize = 100;

    /// <summary>Activity per job for the month starting <paramref name="month"/> (the 1st).</summary>
    public async Task<IReadOnlyDictionary<string, JobMonth>> GetAsync(DateOnly month, bool refresh, CancellationToken ct)
    {
        var key = $"bonus:month:{month:yyyy-MM}";
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = JobTreadFreshness.For(CacheSignals.Projects);
            entry.AddExpirationToken(CacheSignals.Token(CacheSignals.Projects));
            return (IReadOnlyDictionary<string, JobMonth>)await LoadAsync(month, ct);
        }))!;
    }

    private async Task<Dictionary<string, JobMonth>> LoadAsync(DateOnly month, CancellationToken ct)
    {
        var from = month;
        var to = month.AddMonths(1).AddDays(-1);
        JsonArray Dates() => new(new JsonArray("issueDate", ">=", from.ToString("yyyy-MM-dd")), new JsonArray("issueDate", "<=", to.ToString("yyyy-MM-dd")));
        JsonObject DocWhere(string type)
        {
            var and = new JsonArray(new JsonArray("type", type), new JsonArray("status", "in", new JsonArray("approved", "pending")));
            foreach (var d in Dates()) and.Add(d!.DeepClone());
            return new JsonObject { ["and"] = and };
        }
        var timeWhere = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonArray("startedAt", ">=", LocalClock.StartOfDayUtc(from).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                new JsonArray("startedAt", "<", LocalClock.StartOfDayUtc(to.AddDays(1)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))),
        };

        var aliases = new Dictionary<string, Func<string?, JsonObject>>
        {
            ["inv"] = page => Grouped("documents", DocWhere("customerInvoice"), """[["job", "id"], ["job", "name"], ["job", "number"]]""", """{ "amount": { "sum": "price" } }""", page),
            ["bill"] = page => Grouped("documents", DocWhere("vendorBill"), """[["job", "id"], ["job", "name"], ["job", "number"]]""", """{ "amount": { "sum": "cost" } }""", page),
            ["time"] = page => Grouped("timeEntries", timeWhere, """[["job", "id"], ["job", "name"], ["job", "number"], ["user", "id"], ["user", "name"]]""", """{ "cost": { "sum": "cost" }, "minutes": { "sum": "minutes" } }""", page),
        };

        var jobs = new Dictionary<string, JobMonth>();
        JobMonth Job(JsonNode row)
        {
            var id = row["job"].Str("id") ?? "none";
            if (!jobs.TryGetValue(id, out var j)) jobs[id] = j = new JobMonth { JobId = id };
            j.JobName ??= row["job"].Str("name");
            j.JobNumber ??= row["job"].Str("number");
            return j;
        }
        void Read(string alias, JsonNode? connection)
        {
            foreach (var row in connection.GroupRows())
            {
                if (row["job"].Str("id") is null) continue; // time or bills not on a job
                var j = Job(row);
                switch (alias)
                {
                    case "inv": j.Income += row.Dec("amount") ?? 0; break;
                    case "bill": j.Bills += row.Dec("amount") ?? 0; break;
                    default:
                        var minutes = row.Dec("minutes") ?? 0;
                        var cost = row.Dec("cost") ?? 0;
                        if (minutes == 0 && cost == 0) break;
                        j.People.Add(new MonthHours(row["user"].Str("id") ?? "", row["user"].Str("name") ?? "Unknown", Math.Round(minutes / 60m, 2), cost));
                        break;
                }
            }
        }

        // First request: all three aliases. Then page any alias that said there's more, one alias per request.
        var org = new JsonObject { ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId } };
        foreach (var (alias, build) in aliases) org[alias] = build(null);
        var name = $"bonus-month-{month:yyyy-MM}";
        var first = (await jobTread.QueryAsync(name, new JsonObject { ["organization"] = org }, ct))["organization"]
            ?? throw new JobTreadException("Unexpected JobTread response for bonus months.");
        var pending = new List<(string Alias, string Page)>();
        foreach (var alias in aliases.Keys)
        {
            Read(alias, first[alias]);
            if (first[alias].Str("nextPage") is { } next) pending.Add((alias, next));
        }
        while (pending.Count > 0)
        {
            var (alias, page) = pending[0];
            pending.RemoveAt(0);
            var more = new JsonObject { ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId }, [alias] = aliases[alias](page) };
            var result = (await jobTread.QueryAsync($"{name}-{alias}-more", new JsonObject { ["organization"] = more }, ct))["organization"];
            Read(alias, result?[alias]);
            if (result?[alias].Str("nextPage") is { } next && next != page) pending.Add((alias, next));
        }
        return jobs;
    }

    private static JsonObject Grouped(string connection, JsonObject where, string by, string aggs, string? page)
    {
        var args = new JsonObject
        {
            ["where"] = where.DeepClone(),
            ["size"] = PageSize,
            ["group"] = new JsonObject { ["by"] = JsonNode.Parse(by), ["aggs"] = JsonNode.Parse(aggs) },
        };
        if (page is not null) args["page"] = page;
        return new JsonObject { ["_"] = connection, ["$"] = args, ["nextPage"] = new JsonObject(), ["withValues"] = new JsonObject() };
    }
}
