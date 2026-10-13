using System.Text.Json.Nodes;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// Near-instant updates: polls the function app's change feed (DashboardChanges, fed by the JobTread
/// webhook) every 20 seconds and drops only the cached data a change touches, so the next page load is fresh
/// and everything else stays cached (see JobTreadFreshness for how long). Setting JOBTREAD_CHANGES_URL = the
/// DashboardChanges function URL including its ?code= key. Without it, JobTread data is cached 2 minutes.
/// </summary>
public sealed class JobTreadChangeWatcher(IHttpClientFactory httpFactory, IConfiguration config, ILogger<JobTreadChangeWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(20);

    /// <summary>When the last change arrived (for /health).</summary>
    public static DateTime? LastChangeAt { get; private set; }
    public static bool Enabled { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = config["JOBTREAD_CHANGES_URL"];
        if (string.IsNullOrWhiteSpace(url)) return;
        Enabled = true;
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sep = url.Contains('?') ? "&" : "?";
        var lastSeenCheck = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { return; }
            try
            {
                // Hourly: which kinds of events JobTread has sent this week (decides how long data can be cached).
                if (DateTime.UtcNow - lastSeenCheck > TimeSpan.FromHours(1))
                {
                    var seen = JsonNode.Parse(await http.GetStringAsync($"{url}{sep}seen=1", stoppingToken))?["seen"] as JsonObject;
                    if (seen is not null)
                        JobTreadFreshness.Saw(seen.Select(p => (p.Key, DateTimeOffset.FromUnixTimeMilliseconds(p.Value?.GetValue<long>() ?? 0).UtcDateTime)));
                    lastSeenCheck = DateTime.UtcNow;
                }

                var body = JsonNode.Parse(await http.GetStringAsync($"{url}{sep}since={since}", stoppingToken));
                JobTreadFreshness.PollSucceeded();
                var events = body?["events"] as JsonArray ?? [];
                if (events.Count == 0) continue;
                JobTreadFreshness.Saw(events.Select(e => (e?["type"]?.GetValue<string>() ?? "", DateTimeOffset.FromUnixTimeMilliseconds(e?["at"]?.GetValue<long>() ?? 0).UtcDateTime)));

                var newest = events.Max(e => e?["at"]?.GetValue<long>() ?? 0);
                if (newest > since) since = newest;
                var types = events.Select(e => e?["type"]?.GetValue<string>() ?? "").ToHashSet();
                // The feed returns at most 200 (newest first); in a bigger burst older kinds may be cut off, so refresh everything.
                if (events.Count >= 200) types.Add("burst");
                Apply(types);
                LastChangeAt = DateTime.UtcNow;
                logger.LogInformation("JobTread changes: {Types}", string.Join(", ", types));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogDebug(e, "Change feed poll failed; trying again in {Seconds}s", Every.TotalSeconds);
            }
        }
    }

    /// <summary>Which caches each kind of JobTread event touches; anything not listed drops everything, to be safe.</summary>
    public static void Apply(IEnumerable<string> types)
    {
        bool schedule = false, projects = false, labor = false, financials = false;
        foreach (var t in types)
        {
            switch (JobTreadFreshness.Kind(t))
            {
                // Nothing the dashboard shows.
                case "file" or "comment" or "dailyLog" or "formSubmission" or "documentRecipient": break;
                // Schedule, plus the start / end / next-day dates on the project list.
                case "task": schedule = true; projects = true; break;
                // Hours and labor cost: schedule actuals, job costs, burden.
                case "timeEntry": labor = true; projects = true; financials = true; schedule = true; break;
                // Invoices, bills, orders, payments: job money and JobTread-vs-QuickBooks.
                case "document" or "documentSent" or "documentPayment" or "payment": projects = true; financials = true; break;
                // Job name, stage, PM: shown on projects, schedule and reconciliation.
                case "job": projects = true; schedule = true; financials = true; break;
                // Customer name, address, "Left Review": projects and customers (both read the job list).
                case "account" or "location" or "contact": projects = true; break;
                default: projects = true; financials = true; schedule = true; labor = true; break;
            }
        }
        if (schedule) ScheduleService.ClearCache();
        if (projects) CacheSignals.Reset(CacheSignals.Projects);
        if (labor) CacheSignals.Reset(CacheSignals.Labor);
        if (financials) CacheSignals.Reset(CacheSignals.Financials);
    }
}
