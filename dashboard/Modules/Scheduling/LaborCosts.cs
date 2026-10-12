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
    public sealed record ActualDay(decimal Cost, Dictionary<string, decimal> MinutesByUser);

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
                if (!result.TryGetValue(k, out var day)) result[k] = day = new ActualDay(0, []);
                if (e.UserId is not null) day.MinutesByUser[e.UserId] = day.MinutesByUser.GetValueOrDefault(e.UserId) + e.Minutes;
                result[k] = day with { Cost = day.Cost + e.Cost };
            }
            return result;
        }))!;
    }

    /// <summary>Estimated cost of a scheduled task: crew × scheduled hours × burdened rate.</summary>
    public async Task<decimal?> EstimateAsync(ScheduleTask task, CancellationToken ct)
    {
        if (task.StartTime is not { } start || task.EndTime is not { } end || end <= start || task.Crew.Count == 0) return null;
        var hours = (decimal)(end - start).TotalHours;
        var rates = await GetRatesAsync(ct);
        var staff = (await people.GetStaffAsync(ct)).ToDictionary(s => s.MembershipId);

        decimal total = 0;
        foreach (var member in task.Crew)
        {
            if (!staff.TryGetValue(member.MembershipId, out var s)) continue;
            var rate = rates.ByUserJob.GetValueOrDefault((s.UserId, task.JobId)) is var r && r > 0 ? r : rates.ByUser.GetValueOrDefault(s.UserId);
            total += rate * hours;
        }
        return total;
    }

    private sealed record Rates(Dictionary<(string UserId, string JobId), decimal> ByUserJob, Dictionary<string, decimal> ByUser);

    /// <summary>Latest burdened rate per person (and per person on each job) from the last 30 days.</summary>
    private async Task<Rates> GetRatesAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync("labor:rates", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var byUserJob = new Dictionary<(string, string), (DateTimeOffset At, decimal Rate)>();
            var byUser = new Dictionary<string, (DateTimeOffset At, decimal Rate)>();
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            await foreach (var e in TimeEntriesAsync(since, DateTimeOffset.UtcNow.AddDays(1), ct))
            {
                if (e.UserId is null || e.Rate <= 0) continue;
                if (!byUser.TryGetValue(e.UserId, out var u) || e.StartedAt > u.At) byUser[e.UserId] = (e.StartedAt, e.Rate);
                if (e.JobId is not null && (!byUserJob.TryGetValue((e.UserId, e.JobId), out var uj) || e.StartedAt > uj.At)) byUserJob[(e.UserId, e.JobId)] = (e.StartedAt, e.Rate);
            }
            return new Rates(byUserJob.ToDictionary(x => x.Key, x => x.Value.Rate), byUser.ToDictionary(x => x.Key, x => x.Value.Rate));
        }))!;

    private sealed record Entry(string? JobId, string? UserId, DateTimeOffset StartedAt, decimal Cost, decimal Rate, decimal Minutes);

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
                        ["nodes"] = JsonNode.Parse("""{ "startedAt": {}, "cost": {}, "hourlyRate": {}, "minutes": {}, "job": { "id": {} }, "user": { "id": {} } }"""),
                    },
                },
            };
            var connection = (await jobTread.QueryAsync(page is null ? "labor-entries" : "labor-entries-more", query, ct))["organization"]?["timeEntries"];
            foreach (var n in connection.Nodes())
                if (DateTimeOffset.TryParse(n.Str("startedAt"), out var at))
                    yield return new Entry(n["job"].Str("id"), n["user"].Str("id"), at, n.Dec("cost") ?? 0, n.Dec("hourlyRate") ?? 0, n.Dec("minutes") ?? 0);
            page = connection.Str("nextPage");
        } while (page is not null);
    }
}
