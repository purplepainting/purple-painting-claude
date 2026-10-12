using System.Text.Json.Nodes;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// Near-instant updates: polls the function app's change feed (DashboardChanges, fed by the JobTread
/// webhook) every 20 seconds and drops cached data for whatever changed, so the next page load is fresh.
/// Setting JOBTREAD_CHANGES_URL = the DashboardChanges function URL including its ?code= key. Without it,
/// pages refresh on their normal 2-minute cache.
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { return; }
            try
            {
                var sep = url.Contains('?') ? "&" : "?";
                var body = JsonNode.Parse(await http.GetStringAsync($"{url}{sep}since={since}", stoppingToken));
                var events = body?["events"] as JsonArray ?? [];
                if (events.Count == 0) continue;

                var newest = events.Max(e => e?["at"]?.GetValue<long>() ?? 0);
                if (newest > since) since = newest;
                var types = events.Select(e => e?["type"]?.GetValue<string>() ?? "").ToHashSet();
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

    /// <summary>Which caches each kind of JobTread event touches.</summary>
    public static void Apply(IEnumerable<string> types)
    {
        bool schedule = false, projects = false, labor = false, financials = false;
        foreach (var t in types)
        {
            if (t.StartsWith("file", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("task", StringComparison.OrdinalIgnoreCase)) { schedule = true; projects = true; }
            else if (t.StartsWith("timeEntry", StringComparison.OrdinalIgnoreCase)) { labor = true; projects = true; financials = true; schedule = true; }
            else { projects = true; financials = true; }
        }
        if (schedule) ScheduleService.ClearCache();
        if (projects) CacheSignals.Reset(CacheSignals.Projects);
        if (labor) CacheSignals.Reset(CacheSignals.Labor);
        if (financials) CacheSignals.Reset(CacheSignals.Financials);
    }
}
