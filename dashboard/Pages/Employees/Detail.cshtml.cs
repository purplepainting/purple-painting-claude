using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Pages.Employees;

/// <summary>The panel shown when an employee row is expanded: settings form and recent jobs. HTML fragment.</summary>
public partial class DetailModel(TeamDirectory directory, LaborCostService laborCosts, PurpleDashboard.Auth.Permissions permissions, ILogger<DetailModel> logger) : PageModel
{
    public TeamMember? Member { get; private set; }
    public IReadOnlyList<LaborCostService.RecentJob> RecentJobs { get; private set; } = [];
    public string? Error { get; private set; }
    public bool CanEdit => directory.CanEdit && permissions.Can(PurpleDashboard.Auth.Perm.EmployeesEdit);
    /// <summary>See pay (QuickBooks base pay, JobTread rates).</summary>
    public bool ShowPay => permissions.Can(PurpleDashboard.Auth.Perm.PayView);
    /// <summary>Change QuickBooks base pay (the pay fields are left out of the form otherwise).</summary>
    public bool CanEditPay => CanEdit && permissions.Can(PurpleDashboard.Auth.Perm.PayEdit);
    public string DefaultLevel { get; private set; } = Levels.Painter;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (!MembershipId().IsMatch(id)) return NotFound();
        try
        {
            var team = await directory.GetAsync(ct);
            if (!team.ByMembership.TryGetValue(id, out var member)) return NotFound();
            Member = member;
            DefaultLevel = directory.DefaultLevel(member.Staff);
            RecentJobs = (await laborCosts.GetRecentJobsByPersonAsync(ct)).GetValueOrDefault(member.Staff.UserId) ?? [];
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading employee {MembershipId} failed", id);
            Error = "Couldn't load from JobTread. Close and reopen to try again.";
        }
        return Page();
    }

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex MembershipId();
}
