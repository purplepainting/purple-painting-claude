using PurpleDashboard.Modules;

namespace PurpleDashboard.Infrastructure;

/// <summary>
/// The numbers on the menu tiles, kept in memory and refreshed in the background so a page never waits
/// on them. A page shows the last known numbers; when they're over a minute old the page starts a
/// refresh and the next page gets the new ones. Each metric runs in its own scope, in parallel, and a
/// failed metric keeps its last value.
/// </summary>
public sealed class MenuNumbers(IServiceScopeFactory scopes, PurpleDashboard.Data.DatabaseStatus database, ILogger<MenuNumbers> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);
    // Right after a restart the database is still connecting; numbers that need it come back blank, so try again soon.
    private static readonly TimeSpan RetryWhileStarting = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MetricTimeout = TimeSpan.FromSeconds(45);

    private volatile IReadOnlyDictionary<string, string> values = new Dictionary<string, string>();
    private DateTime refreshedAt = DateTime.MinValue;
    private int refreshing;

    /// <summary>Last known numbers by module key; starts a background refresh when they're stale.</summary>
    public IReadOnlyDictionary<string, string> Get()
    {
        if (DateTime.UtcNow - refreshedAt > MaxAge) StartRefresh();
        return values;
    }

    public void StartRefresh()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            var databaseReady = !database.Configured || database.Available;
            try { await RefreshAsync(); }
            finally
            {
                refreshedAt = databaseReady ? DateTime.UtcNow : DateTime.UtcNow - MaxAge + RetryWhileStarting;
                Interlocked.Exchange(ref refreshing, 0);
            }
        });
    }

    private async Task RefreshAsync()
    {
        int count;
        using (var scope = scopes.CreateScope()) count = scope.ServiceProvider.GetServices<ITileMetric>().Count();

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            using var scope = scopes.CreateScope();
            var metric = scope.ServiceProvider.GetServices<ITileMetric>().ElementAt(i);
            using var timeout = new CancellationTokenSource(MetricTimeout);
            try { return (metric.ModuleKey, Value: await metric.GetValueAsync(timeout.Token), Ok: true); }
            catch (Exception e)
            {
                logger.LogWarning(e, "Menu number for {Module} failed", metric.ModuleKey);
                return (metric.ModuleKey, Value: (string?)null, Ok: false);
            }
        }));

        var next = new Dictionary<string, string>(values);
        foreach (var (key, value, ok) in results)
        {
            if (!ok) continue;
            if (value is null) next.Remove(key); else next[key] = value;
        }
        values = next;
    }
}
