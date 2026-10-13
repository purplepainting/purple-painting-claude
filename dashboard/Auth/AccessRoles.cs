namespace PurpleDashboard.Auth;

/// <summary>
/// Every permission key the dashboard checks. Keys are "area.view" / "area.edit" (plus a few single
/// actions such as "bonuses.rate"). An edit key implies its view key. Add a key here, give it to roles in
/// <see cref="AccessRoles.Table"/>, and list it in <see cref="Perm.Areas"/> so the access page shows it.
/// </summary>
public static class Perm
{
    public const string ProjectsView = "projects.view";
    public const string ProjectsEdit = "projects.edit";
    public const string SchedulingView = "scheduling.view";
    public const string SchedulingEdit = "scheduling.edit";
    public const string FinancialsView = "financials.view";
    public const string FinancialsEdit = "financials.edit";
    /// <summary>The bonuses page (everyone's bonus pools and payouts).</summary>
    public const string BonusesView = "bonuses.view";
    /// <summary>Approve / mark paid / change bonus amounts.</summary>
    public const string BonusesEdit = "bonuses.edit";
    /// <summary>Enter effort ratings for the monthly bonus (supervisors rate their crews).</summary>
    public const string BonusesRate = "bonuses.rate";
    public const string CommissionsView = "commissions.view";
    public const string CommissionsEdit = "commissions.edit";
    /// <summary>Sales &amp; Marketing door-to-door log.</summary>
    public const string SalesView = "sales.view";
    public const string SalesEdit = "sales.edit";
    /// <summary>Sales &amp; Marketing ads &amp; lead sources (spend, platform results).</summary>
    public const string AdsView = "sales.ads.view";
    public const string AdsEdit = "sales.ads.edit";
    public const string CustomersView = "customers.view";
    public const string CustomersEdit = "customers.edit";
    public const string VendorsView = "vendors.view";
    public const string VendorsEdit = "vendors.edit";
    /// <summary>The Employees list: levels, PM colors, notes, hours, jobs worked.</summary>
    public const string EmployeesView = "employees.view";
    public const string EmployeesEdit = "employees.edit";
    /// <summary>Pay rates: QuickBooks base pay, JobTread burdened rates, per-person labor cost.</summary>
    public const string PayView = "employees.pay.view";
    public const string PayEdit = "employees.pay.edit";
    public const string HiringView = "hiring.view";
    public const string HiringEdit = "hiring.edit";
    public const string EquipmentView = "equipment.view";
    public const string EquipmentEdit = "equipment.edit";
    public const string ChangeLogView = "changelog.view";
    /// <summary>See the company connections (QuickBooks, Google, Facebook). Everyone can manage their own JobTread sign-in.</summary>
    public const string ConnectionsView = "connections.view";
    public const string ConnectionsEdit = "connections.edit";
    /// <summary>App settings (admin pages under Settings).</summary>
    public const string SettingsView = "settings.view";
    public const string SettingsEdit = "settings.edit";
    /// <summary>Bonus rules (Settings → Bonuses).</summary>
    public const string SettingsBonuses = "settings.bonuses";
    /// <summary>Users &amp; access: roles and per-person overrides.</summary>
    public const string AccessView = "access.view";
    public const string AccessEdit = "access.edit";

    /// <summary>One row per area on the access page, in this order: label and its keys (view first).</summary>
    public static readonly IReadOnlyList<PermArea> Areas =
    [
        new("Projects", [new(ProjectsView, "View"), new(ProjectsEdit, "Edit")]),
        new("Scheduling", [new(SchedulingView, "View"), new(SchedulingEdit, "Edit / push")]),
        new("Financials", [new(FinancialsView, "View"), new(FinancialsEdit, "Edit")]),
        new("Bonuses", [new(BonusesView, "View all"), new(BonusesEdit, "Approve / pay"), new(BonusesRate, "Rate effort")]),
        new("Commissions", [new(CommissionsView, "View"), new(CommissionsEdit, "Edit")]),
        new("Door-to-door", [new(SalesView, "View"), new(SalesEdit, "Log")]),
        new("Ads & lead sources", [new(AdsView, "View"), new(AdsEdit, "Enter spend")]),
        new("Customers", [new(CustomersView, "View"), new(CustomersEdit, "Edit")]),
        new("Vendors & payables", [new(VendorsView, "View"), new(VendorsEdit, "Edit")]),
        new("Employees", [new(EmployeesView, "View"), new(EmployeesEdit, "Edit levels")]),
        new("Pay rates", [new(PayView, "View"), new(PayEdit, "Edit")]),
        new("Hiring", [new(HiringView, "View"), new(HiringEdit, "Edit")]),
        new("Equipment & sundries", [new(EquipmentView, "View"), new(EquipmentEdit, "Edit")]),
        new("Change log", [new(ChangeLogView, "View")]),
        new("Connections", [new(ConnectionsView, "View"), new(ConnectionsEdit, "Connect")]),
        new("Settings", [new(SettingsView, "View"), new(SettingsEdit, "Edit"), new(SettingsBonuses, "Bonus rules")]),
        new("Users & access", [new(AccessView, "View"), new(AccessEdit, "Change")]),
    ];

    public static readonly IReadOnlySet<string> All = Areas.SelectMany(a => a.Keys).Select(k => k.Key).ToHashSet();

    /// <summary>"Financials" for "financials.view": the area name used in "You don't have access to …".</summary>
    public static string AreaOf(string key) =>
        Areas.FirstOrDefault(a => a.Keys.Any(k => k.Key == key))?.Label ?? key;

    /// <summary>"Financials: View".</summary>
    public static string Describe(string key) =>
        Areas.SelectMany(a => a.Keys.Select(k => (a.Label, k))).FirstOrDefault(x => x.k.Key == key) is { k: not null } m
            ? $"{m.Label}: {m.k.Label}" : key;

    /// <summary>The view key an edit key implies ("employees.pay.edit" → "employees.pay.view"), or null.</summary>
    public static string? ViewFor(string key) =>
        key.EndsWith(".edit", StringComparison.Ordinal) && All.Contains(key[..^5] + ".view") ? key[..^5] + ".view" : null;
}

public sealed record PermKey(string Key, string Label);
public sealed record PermArea(string Label, IReadOnlyList<PermKey> Keys);

/// <summary>
/// The four roles and what each can do (owner-approved, Oct 2026). This table is the one place role
/// defaults live; per-person overrides on Settings → Users &amp; access are applied on top.
/// </summary>
public static class AccessRoles
{
    public const string Admin = "admin";
    public const string Office = "office";
    public const string Supervisor = "supervisor";
    public const string ViewOnly = "viewonly";

    /// <summary>New people start here.</summary>
    public const string Default = ViewOnly;

    public static readonly IReadOnlyList<string> All = [Admin, Office, Supervisor, ViewOnly];

    public static string Label(string? role) => role switch
    {
        Admin => "Admin",
        Office => "Office",
        Supervisor => "Supervisor",
        ViewOnly => "View only",
        _ => role ?? "—",
    };

    public static string Describe(string role) => role switch
    {
        Admin => "Everything, including Settings, connections and who has access.",
        Office => "Runs the schedule, customers, hiring, equipment, vendors and projects. Sees financials, sales and pay but can't change pay, bonuses, commissions, ad spend or settings.",
        Supervisor => "Edits the schedule, hiring and equipment, rates effort for bonuses. Sees projects, customers, the team list and financials (read only). No pay rates, other people's bonuses, commissions, sales or vendors.",
        ViewOnly => "Sees what Office sees and changes nothing.",
        _ => "",
    };

    public static bool IsValid(string? role) => role is not null && All.Contains(role);

    private static readonly string[] OfficeViews =
    [
        Perm.ProjectsView, Perm.SchedulingView, Perm.FinancialsView, Perm.BonusesView, Perm.CommissionsView,
        Perm.SalesView, Perm.AdsView, Perm.CustomersView, Perm.VendorsView, Perm.EmployeesView, Perm.PayView,
        Perm.HiringView, Perm.EquipmentView, Perm.ChangeLogView, Perm.ConnectionsView,
    ];

    /// <summary>Role → permission keys. Edit keys imply their view keys (added by <see cref="Effective"/>).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Table = new Dictionary<string, IReadOnlySet<string>>
    {
        [Admin] = Perm.All,
        [Office] = new HashSet<string>(OfficeViews)
        {
            Perm.ProjectsEdit, Perm.SchedulingEdit, Perm.CustomersEdit, Perm.VendorsEdit, Perm.EmployeesEdit,
            Perm.HiringEdit, Perm.EquipmentEdit,
        },
        [Supervisor] = new HashSet<string>
        {
            Perm.ProjectsView, Perm.SchedulingEdit, Perm.FinancialsView, Perm.BonusesRate, Perm.CustomersView,
            Perm.EmployeesView, Perm.HiringEdit, Perm.EquipmentEdit,
        },
        [ViewOnly] = new HashSet<string>(OfficeViews),
    };

    public static IReadOnlySet<string> Defaults(string? role) =>
        WithImplied(Table.TryGetValue(role ?? "", out var set) ? set : Table[ViewOnly]);

    /// <summary>
    /// Role defaults plus overrides (key → true to grant, false to deny). An edit key brings its view key
    /// with it, unless the view key is denied, in which case the edit key is off too.
    /// </summary>
    public static IReadOnlySet<string> Effective(string? role, IReadOnlyDictionary<string, bool>? overrides)
    {
        var result = new HashSet<string>(Table.TryGetValue(role ?? "", out var set) ? set : Table[ViewOnly]);
        foreach (var (key, grant) in overrides ?? new Dictionary<string, bool>())
        {
            if (!Perm.All.Contains(key)) continue;
            if (grant) result.Add(key); else result.Remove(key);
        }
        foreach (var key in result.ToList())
        {
            if (Perm.ViewFor(key) is not { } view) continue;
            if (overrides?.TryGetValue(view, out var g) == true && !g) result.Remove(key);
            else result.Add(view);
        }
        return result;
    }

    private static HashSet<string> WithImplied(IEnumerable<string> keys)
    {
        var result = new HashSet<string>(keys);
        foreach (var key in result.ToList()) if (Perm.ViewFor(key) is { } view) result.Add(view);
        return result;
    }
}
