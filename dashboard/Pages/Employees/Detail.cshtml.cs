using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Financials;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Pages.Employees;

/// <summary>The panel shown when an employee row is expanded: settings form and recent jobs. HTML fragment.</summary>
public partial class DetailModel(TeamDirectory directory, LaborCostService laborCosts, BonusService bonuses, PayrollPayments payroll, PurpleDashboard.Auth.Permissions permissions, ILogger<DetailModel> logger) : PageModel
{
    /// <summary>Their bonus ledger (same math as Financials → Bonuses), or null when it couldn't load.</summary>
    public BonusLedger? Bonus { get; private set; }
    /// <summary>Bonus Only paycheck numbers by month (QuickBooks), when QuickBooks is connected.</summary>
    public Dictionary<DateOnly, List<PayrollLine>> Checks { get; } = [];
    /// <summary>Month the status chips are for: last month (the one being paid now).</summary>
    public DateOnly StatusMonth { get; private set; }

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
            if (permissions.Can(PurpleDashboard.Auth.Perm.BonusesView)) await LoadBonusAsync(member, ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading employee {MembershipId} failed", id);
            Error = "Couldn't load from JobTread. Close and reopen to try again.";
        }
        return Page();
    }

    // Shown only with bonuses.view (supervisors only have bonuses.rate). Showing people their own is a possible later step.
    private async Task LoadBonusAsync(TeamMember member, CancellationToken ct)
    {
        try
        {
            Bonus = await bonuses.GetAsync(false, ct);
            var last = BonusService.CurrentMonth.AddMonths(-1);
            StatusMonth = Bonus.Months.Contains(last) ? last : Bonus.Months[^1];
            var accounts = bonuses.Rules.BonusAccounts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var m in Bonus.Months.Where(m => m.Year == StatusMonth.Year))
            {
                var view = await payroll.GetAsync(accounts, m, m.AddMonths(1).AddDays(-1), "Bonus", "Bonus rules → bonus account", ct);
                if (!view.Ok) break;
                var mine = view.Lines.Where(l => PayrollPayments.MatchPerson(l, [member.Staff], [member.Staff.Name]) == member.Staff.Name).ToList();
                if (mine.Count > 0) Checks[m] = mine;
            }
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading bonuses for {Name} failed", member.Staff.Name);
            Bonus = null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex MembershipId();
}
