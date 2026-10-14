namespace PurpleDashboard.Auth;

/// <summary>What a page or handler needs: any one of the permission keys, or one of the special rules.</summary>
public sealed record AccessRule(string Area, params string[] AnyOf)
{
    /// <summary>No check at all (the error page).</summary>
    public static readonly AccessRule Public = new("public", "*public");
    /// <summary>Anyone signed in (Settings, search, your own JobTread sign-in).</summary>
    public static readonly AccessRule SignedIn = new("the dashboard", "*signed-in");
    /// <summary>Admins only: used for anything not in the map (and logged as a warning).</summary>
    public static AccessRule AdminOnly(string what) => new(what, "*admin");

    public static AccessRule For(params string[] keys) => new(Perm.AreaOf(keys[0]), keys);

    public bool Allows(Permissions p) => AnyOf.Any(k => k switch
    {
        "*public" => true,
        "*signed-in" => p.Email is not null,
        "*admin" => p.IsAdmin,
        _ => p.Can(k),
    });
}

/// <summary>
/// The access map: which permission each page needs to open (by folder or page path) and which permission
/// every POST handler needs. Enforced for every request by <see cref="AccessFilter"/>.
///
/// Default deny: a page that matches nothing here, or a POST/PUT/DELETE handler not listed in
/// <see cref="Handlers"/>, needs Admin and logs a warning. So when you add a page or a handler, add its
/// line here (see "Users &amp; access" in dashboard/README.md).
/// </summary>
public static class AccessMap
{
    /// <summary>
    /// Page path (Razor view path, e.g. "/Employees/Index") prefix → what's needed to open it. The longest
    /// matching prefix wins, so "/Financials/Bonuses" (any page whose path starts with it) beats "/Financials/".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AccessRule> Pages = new Dictionary<string, AccessRule>(StringComparer.OrdinalIgnoreCase)
    {
        ["/Error"] = AccessRule.Public,
        ["/ComingSoon"] = AccessRule.SignedIn,
        ["/Search/"] = AccessRule.SignedIn,                 // results are filtered by permission
        ["/Settings/Index"] = AccessRule.SignedIn,          // tiles are filtered by permission
        ["/Settings/Access"] = AccessRule.For(Perm.AccessView),
        ["/Settings/Bonuses"] = AccessRule.For(Perm.SettingsBonuses),
        ["/Projects/"] = AccessRule.For(Perm.ProjectsView),
        ["/Scheduling/"] = AccessRule.For(Perm.SchedulingView),
        // Raters (supervisors, bonuses.rate) open it too, but only see the effort ratings screen.
        ["/Financials/Bonuses"] = new("Bonuses", Perm.BonusesView, Perm.BonusesRate),
        ["/Financials/"] = AccessRule.For(Perm.FinancialsView),
        ["/Customers/"] = AccessRule.For(Perm.CustomersView),
        ["/Sales/"] = new("Sales & Marketing", Perm.SalesView, Perm.CommissionsView, Perm.AdsView),
        ["/Vendors/"] = AccessRule.For(Perm.VendorsView),
        ["/Employees/Interviews/"] = AccessRule.For(Perm.HiringView),
        ["/Employees/"] = AccessRule.For(Perm.EmployeesView),
        ["/Equipment/"] = AccessRule.For(Perm.EquipmentView),
        ["/Changes/"] = AccessRule.For(Perm.ChangeLogView),
        // Everyone can open Connections to manage their own JobTread sign-in; company cards are filtered on the page.
        ["/Connections/Index"] = AccessRule.SignedIn,
        ["/Connections/JobTreadCallback"] = AccessRule.SignedIn,
        // QuickBooks / Google / Facebook connect pages and callbacks save the company connection.
        ["/Connections/"] = AccessRule.For(Perm.ConnectionsEdit),
    };

    /// <summary>
    /// "{page}#{handler}" → what a handler needs. Every POST handler must be listed (handler "" is a plain OnPost).
    /// GET handlers are listed only when they change something (OAuth starts); others need the page's permission.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AccessRule> Handlers = new Dictionary<string, AccessRule>(StringComparer.OrdinalIgnoreCase)
    {
        ["/Error#"] = AccessRule.Public,

        ["/Scheduling/Index#Push"] = AccessRule.For(Perm.SchedulingEdit),

        ["/Customers/Index#Review"] = AccessRule.For(Perm.CustomersEdit),

        ["/Sales/Index#Rule"] = AccessRule.For(Perm.CommissionsEdit),
        ["/Sales/Index#Pay"] = AccessRule.For(Perm.CommissionsEdit),
        ["/Sales/Index#DeletePayment"] = AccessRule.For(Perm.CommissionsEdit),
        ["/Sales/Index#PayFromPayroll"] = AccessRule.For(Perm.CommissionsEdit),
        ["/Sales/Index#Lock"] = AccessRule.For(Perm.CommissionsEdit),
        ["/Sales/Index#Unlock"] = AccessRule.For(Perm.CommissionsEdit),
        ["/Sales/Index#Activity"] = AccessRule.For(Perm.SalesEdit),
        ["/Sales/Index#DeleteActivity"] = AccessRule.For(Perm.SalesEdit),
        ["/Sales/Index#Spend"] = AccessRule.For(Perm.AdsEdit),
        ["/Sales/Index#DeleteSpend"] = AccessRule.For(Perm.AdsEdit),

        ["/Financials/Bonuses#Pay"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#PayFromPayroll"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#DeletePayment"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Lock"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Unlock"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Adjust"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#DeleteAdjustment"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Release"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#UndoRelease"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Clawback"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#DeleteClawback"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Exclude"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#LeaveOutJob"] = AccessRule.For(Perm.BonusesEdit),
        ["/Financials/Bonuses#Rate"] = AccessRule.For(Perm.BonusesRate),
        ["/Settings/Bonuses#"] = AccessRule.For(Perm.SettingsBonuses),

        // Pay fields in this form also need employees.pay.edit (checked in the handler).
        ["/Employees/Index#Save"] = AccessRule.For(Perm.EmployeesEdit),

        ["/Employees/Interviews/Index#Add"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Index#Day"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Sheet#"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#Book"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#Move"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#Cancel"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#Reminder"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#Confirm"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#EditDay"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Day#DeleteDay"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#DeleteFile"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Edit"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Stage"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Note"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Book"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Trial"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#TrialSave"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Hire"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#Onboarding"] = AccessRule.For(Perm.HiringEdit),
        ["/Employees/Interviews/Candidate#NotHired"] = AccessRule.For(Perm.HiringEdit),

        ["/Equipment/Index#Item"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Receive"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Issue"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Count"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#ArchiveItem"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#UndoIssue"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Equipment"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Move"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Condition"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#LoadSundries"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#LoadEquipment"] = AccessRule.For(Perm.EquipmentEdit),
        ["/Equipment/Index#Retire"] = AccessRule.For(Perm.EquipmentEdit),

        // Company connections.
        ["/Connections/Index#Connect"] = AccessRule.For(Perm.ConnectionsEdit),            // GET: starts QuickBooks sign-in
        ["/Connections/Index#Disconnect"] = AccessRule.For(Perm.ConnectionsEdit),
        ["/Connections/Index#GoogleConnect"] = AccessRule.For(Perm.ConnectionsEdit),      // GET: starts Google sign-in
        ["/Connections/Index#GoogleDisconnect"] = AccessRule.For(Perm.ConnectionsEdit),
        ["/Connections/Index#GoogleAds"] = AccessRule.For(Perm.ConnectionsEdit),
        ["/Connections/Index#GoogleLocation"] = AccessRule.For(Perm.ConnectionsEdit),
        ["/Connections/Meta#Connect"] = AccessRule.For(Perm.ConnectionsEdit),             // GET: starts Facebook sign-in
        ["/Connections/Meta#Account"] = AccessRule.For(Perm.ConnectionsEdit),
        ["/Connections/Meta#Disconnect"] = AccessRule.For(Perm.ConnectionsEdit),
        // Your own JobTread sign-in (used for your schedule pushes): anyone signed in.
        ["/Connections/Index#JobTreadSignIn"] = AccessRule.SignedIn,                      // GET
        ["/Connections/Index#LinkJobTread"] = AccessRule.SignedIn,
        ["/Connections/Index#UnlinkJobTread"] = AccessRule.SignedIn,

        ["/Settings/Access#Save"] = AccessRule.For(Perm.AccessEdit),
    };

    /// <summary>Menu tiles: module key → what's needed to see the tile (any one).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Modules = new Dictionary<string, string[]>
    {
        ["projects"] = [Perm.ProjectsView],
        ["scheduling"] = [Perm.SchedulingView],
        ["financials"] = [Perm.FinancialsView],
        ["customers"] = [Perm.CustomersView],
        ["sales"] = [Perm.SalesView, Perm.CommissionsView, Perm.AdsView],
        ["vendors"] = [Perm.VendorsView],
        ["employees"] = [Perm.EmployeesView, Perm.HiringView],
        ["equipment"] = [Perm.EquipmentView],
    };

    /// <summary>Menu tile numbers that need more than the tile itself (Sales shows commissions owed).</summary>
    public static readonly IReadOnlyDictionary<string, string> Metrics = new Dictionary<string, string>
    {
        ["sales"] = Perm.CommissionsView,
    };

    public static bool CanSeeModule(Permissions p, string key) =>
        Modules.TryGetValue(key, out var keys) ? keys.Any(p.Can) : p.IsAdmin;

    /// <summary>The page rule for a Razor view path, or null when nothing matches (then Admin only).</summary>
    public static AccessRule? PageRule(string viewPath)
    {
        string? best = null;
        foreach (var prefix in Pages.Keys)
            if (viewPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (best is null || prefix.Length > best.Length))
                best = prefix;
        return best is null ? null : Pages[best];
    }

    public static AccessRule? HandlerRule(string viewPath, string? handler) =>
        Handlers.TryGetValue($"{viewPath}#{handler}", out var rule) ? rule : null;
}
