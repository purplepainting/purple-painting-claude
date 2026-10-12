using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using static PurpleDashboard.Integrations.JobTread.JobTreadIds;

namespace PurpleDashboard.Modules.Scheduling;

/// <summary>
/// Reads the production schedule from JobTread tasks. Only tasks on a job are included
/// (office/admin tasks have no job). How the schedule is stored: docs/JOBTREAD.md.
/// </summary>
public sealed class ScheduleService(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    // JobTread rejects a query (413) when its largest possible result is too big: page size times
    // every nested list's size. Keep pages and nested sizes small (crews are 1-4 people).
    private const int PageSize = 25;

    /// <summary>Job tasks that overlap the date range (inclusive), internal jobs excluded.</summary>
    public async Task<IReadOnlyList<ScheduleTask>> GetTasksAsync(DateOnly from, DateOnly to, bool refresh = false, CancellationToken ct = default)
    {
        var key = $"schedule:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return (IReadOnlyList<ScheduleTask>)(await LoadAsync(from, to, ct)).Where(t => !t.IsInternal).ToList();
        }))!;
    }

    private async Task<List<ScheduleTask>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<ScheduleTask>();
        string? page = null;
        do
        {
            var where = JsonNode.Parse("""{ "and": [["isToDo", false], ["isGroup", false], [["job", "id"], "!=", null]] }""")!;
            var and = (JsonArray)where["and"]!;
            and.Add(new JsonArray("startDate", "<=", to.ToString("yyyy-MM-dd")));
            and.Add(new JsonArray("endDate", ">=", from.ToString("yyyy-MM-dd")));

            var args = new JsonObject { ["where"] = where, ["size"] = PageSize, ["sortBy"] = JsonNode.Parse("""[{ "field": "startDate" }]""") };
            if (page is not null) args["page"] = page;

            var nodes = JsonNode.Parse("""
            {
              "id": {}, "name": {}, "startDate": {}, "endDate": {}, "startTime": {}, "endTime": {}, "completed": {},
              "taskType": { "name": {} },
              "job": { "id": {}, "name": {}, "number": {}, "location": { "address": {} } },
              "assignedMemberships": { "$": { "size": 10 }, "nodes": { "user": { "name": {} } } }
            }
            """)!;
            nodes["job"]!["customFieldValues"] = new JsonObject
            {
                ["$"] = new JsonObject
                {
                    ["size"] = 2,
                    ["where"] = new JsonArray(new JsonArray("customField", "id"), "in", new JsonArray(JobFields.JobType, JobFields.HowBidCameIn)),
                },
                ["nodes"] = JsonNode.Parse("""{ "value": {}, "customField": { "id": {} } }"""),
            };

            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["tasks"] = new JsonObject { ["$"] = args, ["nextPage"] = new JsonObject(), ["nodes"] = nodes },
                },
            };

            var connection = (await jobTread.QueryAsync(page is null ? "schedule-week" : "schedule-week-more", query, ct))["organization"]?["tasks"];
            foreach (var task in connection.Nodes())
                if (ToTask(task) is { } t) result.Add(t);
            page = connection.Str("nextPage");
        } while (page is not null);

        return result;
    }

    private static ScheduleTask? ToTask(JsonNode task)
    {
        var job = task["job"];
        if (job.Str("id") is not { } jobId || task.Date("startDate") is not { } start) return null;
        var fields = job.CustomFields();
        return new ScheduleTask
        {
            Id = task.Str("id")!,
            Name = task.Str("name") ?? "",
            Start = start,
            End = task.Date("endDate") ?? start,
            StartTime = TimeOnly.TryParse(task.Str("startTime"), out var st) ? st : null,
            EndTime = TimeOnly.TryParse(task.Str("endTime"), out var et) ? et : null,
            Completed = task.Dec("completed") >= 1,
            TaskType = task["taskType"].Str("name"),
            JobId = jobId,
            JobName = job.Str("name") ?? "(no name)",
            JobNumber = job.Str("number"),
            JobType = fields.GetValueOrDefault(JobFields.JobType),
            Address = job?["location"].Str("address"),
            IsInternal = fields.GetValueOrDefault(JobFields.HowBidCameIn) == "Internal Job",
            People = task["assignedMemberships"].Nodes().Select(m => m["user"].Str("name")).OfType<string>().ToList(),
        };
    }
}

/// <summary>Number of jobs with work scheduled this week, for the Scheduling tile.</summary>
public sealed class JobsThisWeekMetric(ScheduleService schedule) : ITileMetric
{
    public string ModuleKey => "scheduling";

    public async Task<string?> GetValueAsync(CancellationToken ct)
    {
        var monday = LocalClock.WeekStart(LocalClock.Today);
        var tasks = await schedule.GetTasksAsync(monday, monday.AddDays(6), ct: ct);
        return tasks.Select(t => t.JobId).Distinct().Count().ToString();
    }
}
