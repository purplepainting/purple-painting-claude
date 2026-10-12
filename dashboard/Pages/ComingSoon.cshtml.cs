using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Modules;

namespace PurpleDashboard.Pages;

/// <summary>
/// Placeholder for modules that aren't built yet. A real module page with a
/// literal route (e.g. @page "/projects") takes priority over this one.
/// </summary>
public class ComingSoonModel : PageModel
{
    public ModuleDefinition Module { get; private set; } = default!;

    public IActionResult OnGet(string module)
    {
        var found = ModuleRegistry.Find(module);
        if (found is null) return NotFound();

        Module = found;
        ViewData["Title"] = found.Name;
        ViewData["ModuleName"] = found.Name;
        return Page();
    }
}
