using Microsoft.AspNetCore.Mvc;
using PurpleDashboard.Auth;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules;

namespace PurpleDashboard.ViewComponents;

/// <summary>
/// The left menu: one tile per module from ModuleRegistry that the person may open (Auth/AccessMap.Modules),
/// with its key number when it has one and they may see it.
/// Numbers come from <see cref="MenuNumbers"/> (refreshed in the background), so the menu never slows a page down.
/// </summary>
public class SideNavViewComponent(MenuNumbers numbers, Permissions permissions) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var path = HttpContext.Request.Path.Value ?? "/";
        var active = ModuleRegistry.All.FirstOrDefault(m => path.StartsWith(m.Route, StringComparison.OrdinalIgnoreCase))?.Key
            ?? (path == "/" ? ModuleRegistry.Home.Key : null);

        var modules = ModuleRegistry.All.Where(m => AccessMap.CanSeeModule(permissions, m.Key)).ToList();
        var metrics = numbers.Get()
            .Where(kv => modules.Any(m => m.Key == kv.Key) && (!AccessMap.Metrics.TryGetValue(kv.Key, out var need) || permissions.Can(need)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        // Someone with Hiring but not the Employees list goes straight to Hiring.
        var links = modules.ToDictionary(m => m.Key, m =>
            m.Key == "employees" && !permissions.Can(Perm.EmployeesView) ? "/employees/interviews" : m.Route);

        return View(new SideNavModel(modules, metrics, active, links));
    }
}

public sealed record SideNavModel(IReadOnlyList<ModuleDefinition> Modules, IReadOnlyDictionary<string, string> Metrics, string? ActiveKey, IReadOnlyDictionary<string, string> Links);
