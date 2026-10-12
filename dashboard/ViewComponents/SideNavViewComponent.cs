using Microsoft.AspNetCore.Mvc;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules;

namespace PurpleDashboard.ViewComponents;

/// <summary>
/// The left menu: one tile per module from ModuleRegistry, with its key number when it has one.
/// Numbers come from <see cref="MenuNumbers"/> (refreshed in the background), so the menu never slows a page down.
/// </summary>
public class SideNavViewComponent(MenuNumbers numbers) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var path = HttpContext.Request.Path.Value ?? "/";
        var active = ModuleRegistry.All.FirstOrDefault(m => path.StartsWith(m.Route, StringComparison.OrdinalIgnoreCase))?.Key
            ?? (path == "/" ? ModuleRegistry.Home.Key : null);

        return View(new SideNavModel(ModuleRegistry.All, numbers.Get(), active));
    }
}

public sealed record SideNavModel(IReadOnlyList<ModuleDefinition> Modules, IReadOnlyDictionary<string, string> Metrics, string? ActiveKey);
