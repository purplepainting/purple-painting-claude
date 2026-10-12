using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Integrations.JobTread;
using static PurpleDashboard.Integrations.JobTread.JobTreadIds;

namespace PurpleDashboard.Modules.Projects;

/// <summary>
/// Reads projects from JobTread. Results are cached for a couple of minutes so pages stay fast
/// and JobTread isn't called on every click. Field meanings: docs/JOBTREAD.md.
/// </summary>
public sealed class ProjectsService(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
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
            var dates = await LoadJobDatesAsync(jobs.Where(j => j.Stage is not null).Select(j => j.Id).ToList(), ct);
            return (IReadOnlyList<ProjectSummary>)jobs
                .Select(j => dates.TryGetValue(j.Id, out var d) ? j with { Start = d.Start, End = d.End } : j)
                .ToList();
        }))!;
    }

    /// <summary>Budget and actual cost by cost code for one job.</summary>
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
            var args = new JsonObject { ["where"] = Json("""["closedOn", null]"""), ["size"] = 100 };
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
                          "budgetCost": { "_": "costItems", "$": { "where": [["document", "id"], null] }, "sum": { "$": ["cost"] } },
                          "billed": { "_": "documents", "$": { "where": { "and": [["type", "customerInvoice"], ["status", "in", ["approved", "pending"]]] } }, "sum": { "$": ["price"] } }
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
            Contract = job.Dec("projectedPrice"),
            BudgetCost = job["budgetCost"].Dec("sum") ?? 0,
            ActualCost = job.Dec("actualCost") ?? 0,
            Billed = job["billed"].Dec("sum") ?? 0,
        };
    }

    /// <summary>First and last scheduled task date per job, in chunks of 100 jobs.</summary>
    private async Task<Dictionary<string, (DateOnly? Start, DateOnly? End)>> LoadJobDatesAsync(List<string> jobIds, CancellationToken ct)
    {
        var result = new Dictionary<string, (DateOnly?, DateOnly?)>();
        foreach (var chunk in jobIds.Chunk(100))
        {
            var where = Json("""{ "and": [["isToDo", false], ["isGroup", false]] }""");
            ((JsonArray)where["and"]!).Add(new JsonArray(new JsonArray("job", "id"), "in", new JsonArray(chunk.Select(id => (JsonNode)id!).ToArray())));

            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["tasks"] = new JsonObject
                    {
                        ["$"] = new JsonObject
                        {
                            ["where"] = where,
                            ["size"] = 100,
                            ["group"] = Json("""{ "by": [["job", "id"]], "aggs": { "start": { "min": "startDate" }, "end": { "max": "endDate" } } }"""),
                        },
                        ["withValues"] = new JsonObject(),
                    },
                },
            };
            var response = await jobTread.QueryAsync("projects-dates", query, ct);
            foreach (var row in response["organization"]?["tasks"].GroupRows() ?? [])
            {
                var id = row["job"].Str("id");
                if (id is not null) result[id] = (row.Date("start"), row.Date("end"));
            }
        }
        return result;
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
                       "group": { "by": [["costCode", "id"], ["costCode", "name"], ["costCode", "number"]], "aggs": { "cost": { "sum": "cost" } } } },
                "withValues": {}
              },
              "time": {
                "_": "timeEntries",
                "$": { "size": 100,
                       "group": { "by": [["costItem", "costCode", "id"], ["costItem", "costCode", "name"], ["costItem", "costCode", "number"]], "aggs": { "cost": { "sum": "cost" }, "minutes": { "sum": "minutes" } } } },
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

        foreach (var row in job["budget"].GroupRows()) For(row["costCode"]).Budget += row.Dec("cost") ?? 0;
        foreach (var row in job["time"].GroupRows())
        {
            var line = For(row["costItem"]?["costCode"]);
            line.Labor += row.Dec("cost") ?? 0;
            line.Minutes += row.Dec("minutes") ?? 0;
        }
        foreach (var bill in job["bills"].Nodes())
            foreach (var row in bill["costItems"].GroupRows()) For(row["costCode"]).Bills += row.Dec("cost") ?? 0;

        var costCodes = lines.Values
            .Select(l => new CostCodeLine(l.Number, l.Name, l.Budget, l.Labor, l.Bills, Math.Round(l.Minutes / 60m, 1)))
            .OrderBy(l => l.Number is null)
            .ThenBy(l => l.Number, StringComparer.Ordinal)
            .ToList();
        return new ProjectDetail(jobId, costCodes);
    }

    private static JsonNode Json(string json) => JsonNode.Parse(json)!;

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private sealed class CostCodeTotals(string? number, string name)
    {
        public string? Number { get; } = number;
        public string Name { get; } = name;
        public decimal Budget, Labor, Bills, Minutes;
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
