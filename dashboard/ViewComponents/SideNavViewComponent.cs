using Microsoft.AspNetCore.Mvc;
using PurpleDashboard.Modules;

namespace PurpleDashboard.ViewComponents;

/// <summary>
/// The left menu: one tile per module from ModuleRegistry, with its key number when it has one.
/// Numbers come from cached data, so this is cheap after the first load.
/// </summary>
public class SideNavViewComponent(IEnumerable<ITileMetric> metrics, ILogger<SideNavViewComponent> logger) : ViewComponent
{
    private static readonly TimeSpan MetricTimeout = TimeSpan.FromSeconds(4);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(MetricTimeout);

        var values = new Dictionary<string, string>();
        foreach (var metric in metrics)
        {
            try
            {
                if (await metric.GetValueAsync(timeout.Token) is { } value) values[metric.ModuleKey] = value;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Menu number for {Module} failed", metric.ModuleKey);
            }
        }

        var path = HttpContext.Request.Path.Value ?? "/";
        var active = ModuleRegistry.All.FirstOrDefault(m => path.StartsWith(m.Route, StringComparison.OrdinalIgnoreCase))?.Key
            ?? (path == "/" ? ModuleRegistry.Home.Key : null);

        return View(new SideNavModel(ModuleRegistry.All, values, active));
    }
}

public sealed record SideNavModel(IReadOnlyList<ModuleDefinition> Modules, IReadOnlyDictionary<string, string> Metrics, string? ActiveKey);
