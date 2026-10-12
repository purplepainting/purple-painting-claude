using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.People;
using static PurpleDashboard.Integrations.JobTread.JobTreadIds;

namespace PurpleDashboard.Modules.Projects;

/// <summary>
/// Reads projects from JobTread. Results are cached for a couple of minutes so pages stay fast
/// and JobTread isn't called on every click. Field meanings: docs/JOBTREAD.md.
/// </summary>
public sealed class ProjectsService(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options, PeopleService people)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private const int ListPageSize = 50;
    private const int ActivityBatchSize = 30;
    private const string ListCacheKey = "projects:list";

    private static readonly string[] WantedFields =
    [
        JobFields.ProductionStage, JobFields.JobType, JobFields.ProjectManager, JobFields.SalesPerson,
        JobFields.HowBidCameIn, JobFields.FolderLink, JobFields.JobWalkBidStage, JobFields.DigitalBidStage,
    ];

    /// <summary>All open (not closed) jobs.</summary>
    public async Task<IReadOnlyList<ProjectSummary>> GetOpenJobsAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (refresh) cache.Remove(ListCacheKey);
        return (await cache.GetOrCreateAsync(ListCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            var jobs = await LoadOpenJobsAsync(ct);
            var activity = await LoadActivityAsync(jobs.Where(j => j.Stage is not null).Select(j => j.Id).ToList(), ct);
            return (IReadOnlyList<ProjectSummary>)jobs
                .Select(j => activity.TryGetValue(j.Id, out var a)
                    ? j with { Start = a.Start, End = a.End, LastTimeEntry = a.LastTime, NextScheduled = a.Next }
                    : j)
                .ToList();
        }))!;
    }

    /// <summary>Actual cost by cost code for one job, plus the codes on its estimate.</summary>
    public async Task<ProjectDetail> GetDetailAsync(string jobId, bool refresh = false, CancellationToken ct = default)
    {
        var key = "projects:detail:" + jobId;
        if (refresh) cache.Remove(key);
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return await LoadDetailAsync(jobId, ct);
        }))!;
    }

    private async Task<List<ProjectSummary>> LoadOpenJobsAsync(CancellationToken ct)
    {
        var result = new List<ProjectSummary>();
        string? page = null;
        do
        {
            var args = new JsonObject { ["where"] = Json("""["closedOn", null]"""), ["size"] = ListPageSize };
            if (page is not null) args["page"] = page;

            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["jobs"] = new JsonObject
                    {
                        ["$"] = args,
                        ["nextPage"] = new JsonObject(),
                        ["nodes"] = Json("""
                        {
                          "id": {}, "name": {}, "number": {}, "actualCost": {}, "projectedPrice": {},
                          "location": { "address": {}, "account": { "name": {} } },
                          "billed": { "_": "documents", "$": { "where": { "and": [["type", "customerInvoice"], ["status", "in", ["approved", "pending"]]] } }, "sum": { "$": ["price"] } },
                          "unpaid": { "_": "documents", "$": { "where": { "and": [["type", "customerInvoice"], ["status", "in", ["approved", "pending"]]] } }, "sum": { "$": ["balance"] } },
                          "pendingOrders": { "_": "documents", "$": { "where": { "and": [["type", "customerOrder"], ["status", "pending"]] } }, "sum": { "$": ["price"] }, "count": {} },
                          "unsentChangeOrders": { "_": "documents", "$": { "where": { "and": [["type", "customerOrder"], ["status", "draft"], ["name", "like", "%Change Order%"]] } }, "sum": { "$": ["price"] }, "count": {} },
                          "retainageCostItem": { "held": { "_": "documentCostItems", "$": { "where": { "and": [[["document", "type"], "customerInvoice"], [["document", "status"], "in", ["approved", "pending"]]] } }, "sum": { "$": ["price"] } } }
                        }
                        """),
                    },
                },
            };
            var nodes = (JsonObject)query["organization"]!["jobs"]!["nodes"]!;
            nodes["customFieldValues"] = new JsonObject
            {
                ["$"] = new JsonObject { ["size"] = 25, ["where"] = new JsonArray(new JsonArray("customField", "id"), "in", new JsonArray(WantedFields.Select(f => (JsonNode)f!).ToArray())) },
                ["nodes"] = Json("""{ "value": {}, "customField": { "id": {} } }"""),
            };

            var response = await jobTread.QueryAsync(page is null ? "projects-list" : "projects-list-more", query, ct);
            var connection = response["organization"]?["jobs"]
                ?? throw new JobTreadException("Unexpected JobTread response: " + Trim(response.ToJsonString()));
            foreach (var job in connection.Nodes()) result.Add(ToSummary(job));
            page = connection.Str("nextPage");
        } while (page is not null);

        return result;
    }

    private static ProjectSummary ToSummary(JsonNode job)
    {
        var fields = job.CustomFields();
        return new ProjectSummary
        {
            Id = job.Str("id")!,
            Number = job.Str("number") ?? "",
            Name = job.Str("name") ?? "(no name)",
            Customer = job["location"]?["account"].Str("name"),
            Address = job["location"].Str("address"),
            Stage = fields.GetValueOrDefault(JobFields.ProductionStage),
            BidStage = fields.GetValueOrDefault(JobFields.JobWalkBidStage) ?? fields.GetValueOrDefault(JobFields.DigitalBidStage),
            JobType = fields.GetValueOrDefault(JobFields.JobType),
            ProjectManager = fields.GetValueOrDefault(JobFields.ProjectManager),
            SalesPerson = fields.GetValueOrDefault(JobFields.SalesPerson),
            HowBidCameIn = fields.GetValueOrDefault(JobFields.HowBidCameIn),
            FolderUrl = fields.GetValueOrDefault(JobFields.FolderLink),
            ApprovedContract = job.Dec("projectedPrice"),
            ActualCost = job.Dec("actualCost") ?? 0,
            PendingOrders = job["pendingOrders"].Dec("sum") ?? 0,
            UnsentChangeOrders = job["unsentChangeOrders"].Dec("sum") ?? 0,
            UnsentChangeOrderCount = (int)(job["unsentChangeOrders"].Dec("count") ?? 0),
            InvoicedNet = job["billed"].Dec("sum") ?? 0,
            Unpaid = job["unpaid"].Dec("sum") ?? 0,
            // Retention lines on invoices are negative; flip to the amount held back.
            RetentionHeld = -(job["retainageCostItem"]?["held"].Dec("sum") ?? 0),
        };
    }

    private sealed record JobActivity(DateOnly? Start, DateOnly? End, DateOnly? LastTime, DateOnly? Next);

    /// <summary>
    /// Per job: first/last scheduled task, most recent clock-in, and next scheduled day.
    /// One JobTread call per batch of jobs, using grouped queries.
    /// </summary>
    private async Task<Dictionary<string, JobActivity>> LoadActivityAsync(List<string> jobIds, CancellationToken ct)
    {
        var start = new Dictionary<string, DateOnly?>();
        var end = new Dictionary<string, DateOnly?>();
        var lastTime = new Dictionary<string, DateOnly?>();
        var next = new Dictionary<string, DateOnly?>();
        var today = LocalClock.Today.ToString("yyyy-MM-dd");

        foreach (var chunk in jobIds.Chunk(ActivityBatchSize))
        {
            JsonArray Ids() => new(chunk.Select(id => (JsonNode)id!).ToArray());
            JsonArray JobIn() => new(new JsonArray("job", "id"), "in", Ids());

            var scheduledWhere = Json("""{ "and": [["isToDo", false], ["isGroup", false]] }""");
            ((JsonArray)scheduledWhere["and"]!).Add(JobIn());
            var upcomingWhere = Json("""{ "and": [["isToDo", false], ["isGroup", false]] }""");
            ((JsonArray)upcomingWhere["and"]!).Add(new JsonArray("startDate", ">=", today));
            ((JsonArray)upcomingWhere["and"]!).Add(JobIn());

            JsonObject Grouped(string connection, JsonNode where, string aggs) => new()
            {
                ["_"] = connection,
                ["$"] = new JsonObject
                {
                    ["where"] = where,
                    ["size"] = 100,
                    ["group"] = Json($$"""{ "by": [["job", "id"]], "aggs": {{aggs}} }"""),
                },
                ["withValues"] = new JsonObject(),
            };

            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["dates"] = Grouped("tasks", scheduledWhere, """{ "start": { "min": "startDate" }, "end": { "max": "endDate" } }"""),
                    ["upcoming"] = Grouped("tasks", upcomingWhere, """{ "next": { "min": "startDate" } }"""),
                    ["lastTime"] = Grouped("timeEntries", JobIn(), """{ "last": { "max": "startedAt" } }"""),
                },
            };
            var org = (await jobTread.QueryAsync("projects-activity", query, ct))["organization"];

            foreach (var row in org?["dates"].GroupRows() ?? [])
                if (row["job"].Str("id") is { } id) { start[id] = row.Date("start"); end[id] = row.Date("end"); }
            foreach (var row in org?["upcoming"].GroupRows() ?? [])
                if (row["job"].Str("id") is { } id) next[id] = row.Date("next");
            foreach (var row in org?["lastTime"].GroupRows() ?? [])
                if (row["job"].Str("id") is { } id && DateTimeOffset.TryParse(row.Str("last"), out var at))
                    lastTime[id] = LocalClock.ToLocalDate(at);
        }

        return jobIds.ToDictionary(id => id, id => new JobActivity(
            start.GetValueOrDefault(id), end.GetValueOrDefault(id), lastTime.GetValueOrDefault(id), next.GetValueOrDefault(id)));
    }

    private async Task<ProjectDetail> LoadDetailAsync(string jobId, CancellationToken ct)
    {
        var query = new JsonObject
        {
            ["job"] = Json("""
            {
              "budget": {
                "_": "costItems",
                "$": { "where": [["document", "id"], null], "size": 100,
                       "group": { "by": [["costCode", "id"], ["costCode", "name"], ["costCode", "number"]], "aggs": { "n": { "count": "id" } } } },
                "withValues": {}
              },
              "time": {
                "_": "timeEntries",
                "$": { "size": 100,
                       "group": { "by": [["costItem", "costCode", "id"], ["costItem", "costCode", "name"], ["costItem", "costCode", "number"]], "aggs": { "cost": { "sum": "cost" }, "minutes": { "sum": "minutes" } } } },
                "withValues": {}
              },
              "crew": {
                "_": "timeEntries",
                "$": { "size": 100,
                       "group": { "by": [["user", "id"], ["user", "name"]], "aggs": { "minutes": { "sum": "minutes" }, "rate": { "max": "hourlyRate" }, "last": { "max": "startedAt" } } } },
                "withValues": {}
              },
              "bills": {
                "_": "documents",
                "$": { "where": { "and": [["type", "vendorBill"], ["status", "in", ["approved", "pending"]]] }, "size": 100 },
                "nodes": {
                  "costItems": {
                    "$": { "size": 100,
                           "group": { "by": [["costCode", "id"], ["costCode", "name"], ["costCode", "number"]], "aggs": { "cost": { "sum": "cost" } } } },
                    "withValues": {}
                  }
                }
              }
            }
            """),
        };
        ((JsonObject)query["job"]!)["$"] = new JsonObject { ["id"] = jobId };

        var job = (await jobTread.QueryAsync("projects-detail", query, ct))["job"]
            ?? throw new JobTreadException($"Job {jobId} wasn't found in JobTread.");

        var lines = new Dictionary<string, CostCodeTotals>();
        CostCodeTotals For(JsonNode? costCode)
        {
            var id = costCode.Str("id") ?? "none";
            if (!lines.TryGetValue(id, out var line))
                lines[id] = line = new CostCodeTotals(costCode.Str("number"), costCode.Str("name") ?? "No cost code");
            return line;
        }

        // The job's estimate lines only supply the list of codes on the job; their amounts aren't used.
        foreach (var row in job["budget"].GroupRows()) For(row["costCode"]);
        foreach (var row in job["time"].GroupRows())
        {
            var line = For(row["costItem"]?["costCode"]);
            line.Labor += row.Dec("cost") ?? 0;
            line.Minutes += row.Dec("minutes") ?? 0;
        }
        foreach (var bill in job["bills"].Nodes())
            foreach (var row in bill["costItems"].GroupRows()) For(row["costCode"]).Bills += row.Dec("cost") ?? 0;

        var costCodes = lines.Values
            .Select(l => new CostCodeLine(l.Number, l.Name, l.Labor, l.Bills, Math.Round(l.Minutes / 60m, 1)))
            .OrderBy(l => l.Number is null)
            .ThenBy(l => l.Number, StringComparer.Ordinal)
            .ToList();
        var supervisors = await people.GetSupervisorUserIdsAsync(ct);
        var crew = job["crew"].GroupRows()
            .Select(row =>
            {
                var id = row["user"].Str("id");
                DateOnly? last = DateTimeOffset.TryParse(row.Str("last"), out var at) ? LocalClock.ToLocalDate(at) : null;
                return new CrewMember(
                    row["user"].Str("name") ?? "Unknown",
                    Math.Round((row.Dec("minutes") ?? 0) / 60m, 1),
                    last,
                    id is not null && supervisors.Contains(id),
                    row.Dec("rate") ?? 0);
            })
            .ToList();

        return new ProjectDetail(jobId, costCodes, crew);
    }

    private static JsonNode Json(string json) => JsonNode.Parse(json)!;

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private sealed class CostCodeTotals(string? number, string name)
    {
        public string? Number { get; } = number;
        public string Name { get; } = name;
        public decimal Labor, Bills, Minutes;
    }
}

/// <summary>Active job count on the Projects tile.</summary>
public sealed class ActiveProjectsMetric(ProjectsService projects) : ITileMetric
{
    public string ModuleKey => "projects";

    public async Task<string?> GetValueAsync(CancellationToken ct)
    {
        var jobs = await projects.GetOpenJobsAsync(ct: ct);
        return jobs.Count(j => !j.IsInternal && ProjectViews.Default.Matches(j)).ToString();
    }
}
