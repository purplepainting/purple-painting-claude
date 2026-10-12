using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Modules;

namespace PurpleDashboard.Pages;

public class IndexModel(IEnumerable<ITileMetric> metrics, ILogger<IndexModel> logger) : PageModel
{
    private static readonly TimeSpan MetricTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Key number per module key. Missing or failed metrics show "—".</summary>
    public Dictionary<string, string> Metrics { get; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetricTimeout);

        var results = await Task.WhenAll(metrics.Select(async m =>
        {
            try { return (m.ModuleKey, Value: await m.GetValueAsync(timeout.Token)); }
            catch (Exception e)
            {
                logger.LogWarning(e, "Tile metric for {Module} failed", m.ModuleKey);
                return (m.ModuleKey, Value: (string?)null);
            }
        }));
        foreach (var (key, value) in results)
            if (value is not null) Metrics[key] = value;
    }
}
