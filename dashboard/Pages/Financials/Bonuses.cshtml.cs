using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Financials;

namespace PurpleDashboard.Pages.Financials;

/// <summary>Bonuses per job (10% of profit to supervisors, 10% to the field, split by hours), approve and mark paid.</summary>
public class BonusesModel(BonusService bonuses, FinancialsService financials, QuickBooksClient quickBooks, DatabaseStatus database, CurrentUser me, ILogger<BonusesModel> logger) : PageModel
{
    /// <summary>"ready" (default), "approved", "projected", "paid", "all".</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }

    public List<JobBonus> All { get; private set; } = [];
    public IEnumerable<JobBonus> Shown => (Show ?? "ready") switch
    {
        "approved" => All.Where(b => b.Status == BonusStatus.Approved),
        "projected" => All.Where(b => b.Status == BonusStatus.Projected),
        "paid" => All.Where(b => b.Status == BonusStatus.Paid),
        "all" => All,
        _ => All.Where(b => b.Status == BonusStatus.Ready),
    };
    public BonusOptions Rules => bonuses.Rules;
    public bool CanEdit => database.Available;
    public string? Error { get; private set; }
    [TempData] public string? Message { get; set; }

    /// <summary>Bonus Payroll booked in QuickBooks this year (6114), to check against what's marked paid here.</summary>
    public decimal? QbBonusesYtd { get; private set; }
    public decimal PaidYtd => All.Where(b => b.Payout?.PaidAt is { } d && d.Year == LocalClock.Today.Year).Sum(b => b.Total);

    public record PersonTotal(string Name, string Role, decimal Ready, decimal Approved, decimal PaidYtd);
    public List<PersonTotal> People { get; private set; } = [];

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        ViewData["Title"] = "Bonuses";
        ViewData["ModuleName"] = "Financials";
        try
        {
            All = await bonuses.GetAsync(refresh, ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading bonuses failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return;
        }

        var year = LocalClock.Today.Year;
        People = All.SelectMany(b => b.Lines.Select(l => (b, l)))
            .GroupBy(x => (x.l.Name, x.l.Role))
            .Select(g => new PersonTotal(g.Key.Name, g.Key.Role,
                g.Where(x => x.b.Status == BonusStatus.Ready).Sum(x => x.l.Amount),
                g.Where(x => x.b.Status == BonusStatus.Approved).Sum(x => x.l.Amount),
                g.Where(x => x.b.Status == BonusStatus.Paid && x.b.Payout?.PaidAt?.Year == year).Sum(x => x.l.Amount)))
            .Where(p => p.Ready + p.Approved + p.PaidYtd > 0)
            .OrderBy(p => p.Role).ThenByDescending(p => p.Ready + p.Approved + p.PaidYtd)
            .ToList();

        try
        {
            if (await quickBooks.GetConnectionAsync(ct) is not null)
            {
                var report = await financials.GetAsync(new DateOnly(year, 1, 1), LocalClock.Today, "Accrual", false, ct);
                QbBonusesYtd = report.Overhead.FirstOrDefault(c => c.Category == "Bonuses")?.Total ?? 0;
            }
        }
        catch (Exception e) when (e is QuickBooksException or JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "QuickBooks bonus check failed");
        }
    }

    public async Task<IActionResult> OnPostApproveAsync(string jobId, CancellationToken ct)
    {
        if (!database.Available) return BadRequest("The dashboard database isn't available.");
        var bonus = (await bonuses.GetAsync(false, ct)).FirstOrDefault(b => b.JobId == jobId && b.Status == BonusStatus.Ready);
        if (bonus is null) { Message = "That job isn't ready for a bonus anymore. Reload and check."; return RedirectToPage(new { show = Show }); }
        await bonuses.ApproveAsync(bonus, User(), ct);
        Message = $"Approved the bonus for {bonus.JobName} ({Format.Money(bonus.Total)}).";
        return RedirectToPage(new { show = Show });
    }

    public async Task<IActionResult> OnPostStatusAsync(string jobId, string action, CancellationToken ct)
    {
        if (!database.Available) return BadRequest("The dashboard database isn't available.");
        await bonuses.SetStatusAsync(jobId, action, User(), ct);
        return RedirectToPage(new { show = Show });
    }

    private string User() => me.Email ?? me.DisplayName ?? "unknown";

    public int Count(BonusStatus s) => All.Count(b => b.Status == s);
    public decimal Sum(BonusStatus s) => All.Where(b => b.Status == s).Sum(b => b.Total);
}
