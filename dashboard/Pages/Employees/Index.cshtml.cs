using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Pages.Employees;

/// <summary>
/// Everyone in JobTread with their crew level and PM color. Levels and colors set here are what the
/// schedule and projects pages use; anyone not set falls back to appsettings.
/// </summary>
public partial class IndexModel(TeamDirectory directory, LaborCostService laborCosts, DatabaseStatus database, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"crew", "supervisors" or empty for everyone.</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>Membership id of the row to open (after saving).</summary>
    [BindProperty(SupportsGet = true)] public string? Open { get; set; }

    public IReadOnlyList<TeamMember> Crew { get; private set; } = [];
    public IReadOnlyList<TeamMember> Supervisors { get; private set; } = [];
    public int Total { get; private set; }
    public Dictionary<string, decimal> HoursThisWeek { get; } = [];
    public string? Error { get; private set; }
    public bool CanEdit => directory.CanEdit;
    public DatabaseStatus Database => database;

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Employees";
        ViewData["ModuleName"] = "Employees";

        Team team;
        try
        {
            team = await directory.GetAsync(ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading staff from JobTread failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return;
        }

        try
        {
            var today = LocalClock.Today;
            foreach (var (key, value) in await laborCosts.GetActualByPersonAsync(LocalClock.WeekStart(today), today, ct))
                HoursThisWeek[key.UserId] = HoursThisWeek.GetValueOrDefault(key.UserId) + value.Minutes / 60m;
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading hours this week failed; list shows without hours");
        }

        Total = team.Members.Count;
        var shown = team.Members.Where(Matches).ToList();
        Crew = Show == "supervisors" ? [] : shown.Where(m => !m.IsSupervisor)
            .OrderBy(m => Levels.Rank(m.Level)).ThenBy(m => m.Staff.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Supervisors = Show == "crew" ? [] : shown.Where(m => m.IsSupervisor)
            .OrderBy(m => m.Staff.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IActionResult> OnPostSaveAsync(string membershipId, string? level, string? color, bool defaultColor, string? notes, string? qbBasePay, string? qbPayType, CancellationToken ct)
    {
        if (!directory.CanEdit) return BadRequest("The dashboard database isn't set up, so changes can't be saved yet.");

        var team = await directory.GetAsync(ct);
        if (!team.ByMembership.ContainsKey(membershipId)) return NotFound();
        level = Levels.All.Contains(level ?? "") ? level : null;
        color = !defaultColor && color is not null && HexColor().IsMatch(color) ? color.ToUpperInvariant() : null;
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 2000)];

        decimal? pay = decimal.TryParse(qbBasePay?.Replace("$", "").Replace(",", "").Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0 ? Math.Round(p, 2) : null;
        var payType = qbPayType == "Salary" ? "Salary" : "Hourly";

        await directory.SaveAsync(membershipId, level, color, notes, pay, payType, me.Email ?? me.DisplayName ?? "unknown", ct);
        return RedirectToPage(new { show = Show, q = Q, open = membershipId });
    }

    /// <summary>"$41.66/hr" or "$62,000/yr".</summary>
    public static string Pay(decimal? amount, string? type) => amount is null ? "—"
        : type == "Salary" ? amount.Value.ToString("$#,0", System.Globalization.CultureInfo.InvariantCulture) + "/yr"
        : amount.Value.ToString("$#,0.00", System.Globalization.CultureInfo.InvariantCulture) + "/hr";

    private bool Matches(TeamMember m) =>
        string.IsNullOrWhiteSpace(Q)
        || m.Staff.Name.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase)
        || m.Staff.Role.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase)
        || m.LevelLabel.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase);

    public decimal Hours(TeamMember m) => Math.Round(HoursThisWeek.GetValueOrDefault(m.Staff.UserId), 1);

    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    public Dictionary<string, string?> RouteFor(string? show) => new() { ["show"] = show, ["q"] = Q };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
