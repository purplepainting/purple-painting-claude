using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Modules.Scheduling;

/// <summary>Labor cost for one job on one day: actual from time entries, or an estimate for days ahead.</summary>
public sealed record DayCost(decimal Amount, bool IsEstimate);

/// <summary>
/// Labor costs for the schedule. Days up to today use actual time entries (JobTread's burdened cost).
/// Days ahead are estimated: each assigned person's scheduled hours × their burdened rate from recent
/// time entries (on that job if they've worked it, otherwise their latest rate). Rates stay on the server.
/// </summary>
public sealed class LaborCostService(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options, PeopleService people)
{
    /// <summary>What actually happened on a job on a day: cost and who clocked in (user id → minutes).</summary>
    public sealed record ActualDay(decimal Cost, Dictionary<string, decimal> MinutesByUser, Dictionary<string, decimal> CostByUser);

    /// <summary>Actual labor per (job id, local date) for the range, from time entries.</summary>
    public async Task<Dictionary<(string JobId, DateOnly Day), ActualDay>> GetActualAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var key = $"labor:actual:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            var result = new Dictionary<(string, DateOnly), ActualDay>();
            await foreach (var e in TimeEntriesAsync(LocalClock.StartOfDayUtc(from), LocalClock.StartOfDayUtc(to.AddDays(1)), ct))
            {
                if (e.JobId is null) continue;
                var k = (e.JobId, LocalClock.ToLocalDate(e.StartedAt));
                if (!result.TryGetValue(k, out var day)) result[k] = day = new ActualDay(0, [], []);
                if (e.UserId is not null)
                {
                    day.MinutesByUser[e.UserId] = day.MinutesByUser.GetValueOrDefault(e.UserId) + e.Minutes;
                    day.CostByUser[e.UserId] = day.CostByUser.GetValueOrDefault(e.UserId) + e.Cost;
                }
                result[k] = day with { Cost = day.Cost + e.Cost };
            }
            return result;
        }))!;
    }

    /// <summary>Actual labor per (user id, local date): cost and minutes, from the same time entries.</summary>
    public async Task<Dictionary<(string UserId, DateOnly Day), (decimal Cost, decimal Minutes)>> GetActualByPersonAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var key = $"labor:person:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            var result = new Dictionary<(string, DateOnly), (decimal, decimal)>();
            await foreach (var e in TimeEntriesAsync(LocalClock.StartOfDayUtc(from), LocalClock.StartOfDayUtc(to.AddDays(1)), ct))
            {
                if (e.UserId is null) continue;
                var k = (e.UserId, LocalClock.ToLocalDate(e.StartedAt));
                var cur = result.GetValueOrDefault(k);
                result[k] = (cur.Item1 + e.Cost, cur.Item2 + e.Minutes);
            }
            return result;
        }))!;
    }

    /// <summary>Estimated cost of a scheduled task: crew × scheduled hours × burdened rate.</summary>
    public async Task<decimal?> EstimateAsync(ScheduleTask task, CancellationToken ct)
    {
        decimal total = 0;
        foreach (var member in task.Crew) total += await EstimatePersonAsync(task, member.MembershipId, ct) ?? 0;
        return total > 0 ? total : null;
    }

    /// <summary>Estimated cost of one person on a scheduled task: scheduled hours × their burdened rate.</summary>
    public async Task<decimal?> EstimatePersonAsync(ScheduleTask task, string membershipId, CancellationToken ct)
    {
        if (task.StartTime is not { } start || task.EndTime is not { } end || end <= start) return null;
        var staff = (await people.GetStaffAsync(ct)).FirstOrDefault(s => s.MembershipId == membershipId);
        if (staff is null) return null;
        var rates = await GetRatesAsync(ct);
        var rate = rates.ByUserJob.GetValueOrDefault((staff.UserId, task.JobId)) is var r && r > 0 ? r : rates.ByUser.GetValueOrDefault(staff.UserId);
        return rate * (decimal)(end - start).TotalHours;
    }

    private sealed record Rates(Dictionary<(string UserId, string JobId), decimal> ByUserJob, Dictionary<string, decimal> ByUser);

    /// <summary>A job someone worked recently: hours and the last day they clocked in there.</summary>
    public sealed record RecentJob(string JobId, string JobName, decimal Hours, DateOnly LastDay);

    /// <summary>Jobs each person (user id) worked in the last 30 days, most recent first.</summary>
    public async Task<Dictionary<string, List<RecentJob>>> GetRecentJobsByPersonAsync(CancellationToken ct) =>
        (await GetRecentEntriesAsync(ct))
            .Where(e => e.UserId is not null && e.JobId is not null)
            .GroupBy(e => e.UserId!)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(e => e.JobId!)
                .Select(j => new RecentJob(j.Key, j.Select(e => e.JobName).FirstOrDefault(n => n is not null) ?? "Unknown job",
                    Math.Round(j.Sum(e => e.Minutes) / 60m, 1), j.Max(e => LocalClock.ToLocalDate(e.StartedAt))))
                .OrderByDescending(j => j.LastDay).ThenByDescending(j => j.Hours)
                .ToList());

    /// <summary>All time entries from the last 30 days, cached 30 minutes (rates and employee history).</summary>
    private async Task<IReadOnlyList<Entry>> GetRecentEntriesAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync("labor:recent", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var list = new List<Entry>();
            await foreach (var e in TimeEntriesAsync(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(1), ct)) list.Add(e);
            return (IReadOnlyList<Entry>)list;
        }))!;

    /// <summary>Latest burdened rate per person (and per person on each job) from the last 30 days.</summary>
    private async Task<Rates> GetRatesAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync("labor:rates", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var byUserJob = new Dictionary<(string, string), (DateTimeOffset At, decimal Rate)>();
            var byUser = new Dictionary<string, (DateTimeOffset At, decimal Rate)>();
            foreach (var e in await GetRecentEntriesAsync(ct))
            {
                if (e.UserId is null || e.Rate <= 0) continue;
                if (!byUser.TryGetValue(e.UserId, out var u) || e.StartedAt > u.At) byUser[e.UserId] = (e.StartedAt, e.Rate);
                if (e.JobId is not null && (!byUserJob.TryGetValue((e.UserId, e.JobId), out var uj) || e.StartedAt > uj.At)) byUserJob[(e.UserId, e.JobId)] = (e.StartedAt, e.Rate);
            }
            return new Rates(byUserJob.ToDictionary(x => x.Key, x => x.Value.Rate), byUser.ToDictionary(x => x.Key, x => x.Value.Rate));
        }))!;

    private sealed record Entry(string? JobId, string? UserId, DateTimeOffset StartedAt, decimal Cost, decimal Rate, decimal Minutes, string? JobName = null);

    /// <summary>Time entries started in [fromUtc, toUtc), 100 per page (no nested lists, so no size limit issue).</summary>
    private async IAsyncEnumerable<Entry> TimeEntriesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? page = null;
        do
        {
            var where = new JsonObject
            {
                ["and"] = new JsonArray(
                    new JsonArray("startedAt", ">=", fromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                    new JsonArray("startedAt", "<", toUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))),
            };
            var args = new JsonObject { ["where"] = where, ["size"] = 100 };
            if (page is not null) args["page"] = page;
            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["timeEntries"] = new JsonObject
                    {
                        ["$"] = args,
                        ["nextPage"] = new JsonObject(),
                        ["nodes"] = JsonNode.Parse("""{ "startedAt": {}, "cost": {}, "hourlyRate": {}, "minutes": {}, "job": { "id": {}, "name": {} }, "user": { "id": {} } }"""),
                    },
                },
            };
            var connection = (await jobTread.QueryAsync(page is null ? "labor-entries" : "labor-entries-more", query, ct))["organization"]?["timeEntries"];
            foreach (var n in connection.Nodes())
                if (DateTimeOffset.TryParse(n.Str("startedAt"), out var at))
                    yield return new Entry(n["job"].Str("id"), n["user"].Str("id"), at, n.Dec("cost") ?? 0, n.Dec("hourlyRate") ?? 0, n.Dec("minutes") ?? 0, n["job"].Str("name"));
            page = connection.Str("nextPage");
        } while (page is not null);
    }
}
