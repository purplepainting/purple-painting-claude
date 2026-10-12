namespace PurpleDashboard.Modules;

public enum ModuleStatus { ComingSoon, Live }

/// <param name="Key">URL segment and id, lowercase letters only (e.g. "projects" → /projects).</param>
/// <param name="Icon">Icon name from Pages/Shared/_Icon.cshtml.</param>
/// <param name="MetricLabel">What the tile's key number means, e.g. "Active jobs".</param>
public sealed record ModuleDefinition(
    string Key,
    string Name,
    string Description,
    string Source,
    string Icon,
    string MetricLabel,
    ModuleStatus Status = ModuleStatus.ComingSoon)
{
    public string Route => "/" + Key;
}

/// <summary>
/// The one place modules are listed. The left menu shows them in this order, and the first
/// one is the home page.
/// To build a module: add its pages under Pages/{Name}/ with @page "/{key}",
/// then set its Status to Live here. See dashboard/README.md.
/// </summary>
public static class ModuleRegistry
{
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new("projects",   "Projects",                "Active jobs, stages, budgets and saved views.",         "JobTread",              "clipboard", "Active jobs", ModuleStatus.Live),
        new("scheduling", "Scheduling",              "Who is on which job, by crew and week.",                "JobTread",              "calendar",  "Jobs this week", ModuleStatus.Live),
        new("financials", "Financials",              "JobTread vs QuickBooks, labor burden, overhead and bonuses.", "QuickBooks + JobTread", "chart",     "Net profit YTD", ModuleStatus.Live),
        new("customers",  "Customers",               "Contacts, jobs, balances and reviews.",                 "JobTread + QuickBooks", "users",     "Customers owing"),
        new("sales",      "Sales & Marketing",       "Commissions, door-to-door and ad spend vs lead sources.", "JobTread + Dashboard",  "megaphone", "Commissions owed", ModuleStatus.Live),
        new("vendors",    "Vendors & Payables",      "Open bills, due dates and vendor balances.",            "QuickBooks",            "receipt",   "Due in 14 days", ModuleStatus.Live),
        new("employees",  "Employees",               "Crew levels, PM colors and who worked where.",          "JobTread + Dashboard",  "id",        "Team members", ModuleStatus.Live),
        new("equipment",  "Equipment & Sundries",    "Shop sundries, issued to jobs, reorders; equipment by location and condition.", "Dashboard", "tools", "Low or out of stock", ModuleStatus.Live),
    ];

    /// <summary>The module shown at "/" (the first page after sign-in).</summary>
    public static ModuleDefinition Home => All[0];

    public static ModuleDefinition? Find(string key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
}
