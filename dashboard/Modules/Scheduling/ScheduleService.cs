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
    // every nested list's size. Keep pages and nested sizes small (crews are 1-4 people; 3 job fields).
    private const int PageSize = 25;

    // Cancelled after a push so every cached schedule week is dropped at once.
    private static CancellationTokenSource resetSignal = new();

    /// <summary>Drop all cached schedule weeks (after changes are pushed to JobTread).</summary>
    public static void ClearCache()
    {
        var old = Interlocked.Exchange(ref resetSignal, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    /// <summary>Job tasks that overlap the date range (inclusive), internal jobs excluded.</summary>
    public async Task<IReadOnlyList<ScheduleTask>> GetTasksAsync(DateOnly from, DateOnly to, bool refresh = false, CancellationToken ct = default)
    {
        var key = $"schedule:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            entry.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(resetSignal.Token));
            var overlaps = new JsonArray(
                new JsonArray("startDate", "<=", to.ToString("yyyy-MM-dd")),
                new JsonArray("endDate", ">=", from.ToString("yyyy-MM-dd")));
            return (IReadOnlyList<ScheduleTask>)(await LoadAsync(overlaps, "schedule-week", ct)).Where(t => !t.IsInternal).ToList();
        }))!;
    }

    /// <summary>
    /// A job's tasks that start in [from, to], unfinished. For "+ Add day": a job whose schedule is
    /// already built out in JobTread can move one of its existing tasks instead of adding a new one.
    /// </summary>
    public async Task<IReadOnlyList<ScheduleTask>> GetJobTasksAsync(string jobId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var conditions = new JsonArray(
            new JsonArray(new JsonArray("job", "id"), "=", jobId),
            new JsonArray("startDate", ">=", from.ToString("yyyy-MM-dd")),
            new JsonArray("startDate", "<=", to.ToString("yyyy-MM-dd")));
        return (await LoadAsync(conditions, "schedule-job-tasks", ct, maxPages: 4)).Where(t => !t.Completed).ToList();
    }

    /// <summary>
    /// Every scheduled task on one job, including phase groups, for the job timeline. Cached 2 minutes and
    /// cleared on push like the weekly schedule.
    /// </summary>
    public async Task<IReadOnlyList<ScheduleTask>> GetJobTimelineAsync(string jobId, bool refresh, CancellationToken ct)
    {
        var key = $"schedule:timeline:{jobId}";
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            entry.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(resetSignal.Token));
            var conditions = new JsonArray(new JsonArray(new JsonArray("job", "id"), "=", jobId));
            return (IReadOnlyList<ScheduleTask>)await LoadAsync(conditions, "schedule-timeline", ct, maxPages: 8, includeGroups: true);
        }))!;
    }

    private async Task<List<ScheduleTask>> LoadAsync(JsonArray conditions, string queryName, CancellationToken ct, int maxPages = int.MaxValue, bool includeGroups = false)
    {
        var result = new List<ScheduleTask>();
        string? page = null;
        var pages = 0;
        do
        {
            var where = JsonNode.Parse(includeGroups
                ? """{ "and": [["isToDo", false], [["job", "id"], "!=", null]] }"""
                : """{ "and": [["isToDo", false], ["isGroup", false], [["job", "id"], "!=", null]] }""")!;
            var and = (JsonArray)where["and"]!;
            foreach (var c in conditions) and.Add(c!.DeepClone());

            var args = new JsonObject { ["where"] = where, ["size"] = PageSize, ["sortBy"] = JsonNode.Parse("""[{ "field": "startDate" }]""") };
            if (page is not null) args["page"] = page;

            var nodes = JsonNode.Parse("""
            {
              "id": {}, "name": {}, "startDate": {}, "endDate": {}, "startTime": {}, "endTime": {}, "completed": {}, "isGroup": {},
              "taskType": { "id": {}, "name": {} },
              "parentTask": { "id": {} },
              "dependents": { "_": "dependentTasks", "count": {} },
              "job": { "id": {}, "name": {}, "number": {}, "location": { "address": {} } },
              "assignedMemberships": { "$": { "size": 10 }, "nodes": { "id": {}, "user": { "name": {} } } }
            }
            """)!;
            nodes["job"]!["customFieldValues"] = new JsonObject
            {
                ["$"] = new JsonObject
                {
                    ["size"] = 3,
                    ["where"] = new JsonArray(new JsonArray("customField", "id"), "in", new JsonArray(JobFields.JobType, JobFields.HowBidCameIn, JobFields.ProjectManager)),
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

            var connection = (await jobTread.QueryAsync(page is null ? queryName : queryName + "-more", query, ct))["organization"]?["tasks"];
            foreach (var task in connection.Nodes())
                if (ToTask(task) is { } t) result.Add(t);
            page = connection.Str("nextPage");
        } while (page is not null && ++pages < maxPages);

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
            IsGroup = task["isGroup"]?.GetValue<bool>() == true,
            TaskType = task["taskType"].Str("name"),
            JobId = jobId,
            JobName = job.Str("name") ?? "(no name)",
            JobNumber = job.Str("number"),
            JobType = fields.GetValueOrDefault(JobFields.JobType),
            ProjectManager = fields.GetValueOrDefault(JobFields.ProjectManager),
            Address = job?["location"].Str("address"),
            IsInternal = fields.GetValueOrDefault(JobFields.HowBidCameIn) == "Internal Job",
            Crew = task["assignedMemberships"].Nodes()
                .Select(m => (Id: m.Str("id"), Name: m["user"].Str("name")))
                .Where(m => m.Id is not null && m.Name is not null)
                .Select(m => new CrewAssignment(m.Id!, m.Name!))
                .ToList(),
            ParentTaskId = task["parentTask"].Str("id"),
            TaskTypeId = task["taskType"].Str("id"),
            Dependents = (int)(task["dependents"].Dec("count") ?? 0),
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
