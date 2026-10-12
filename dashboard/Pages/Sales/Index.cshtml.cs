using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Projects;
using PurpleDashboard.Modules.Sales;

namespace PurpleDashboard.Pages.Sales;

public class IndexModel(SalesService sales, ChangeLogWriter changeLog, DatabaseStatus database, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"commissions" (default), "doors", "ads".</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>Commissions filter: "owed" (default), "all".</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    /// <summary>Door-to-door and ads period: "30" days, "90" (default), "ytd".</summary>
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }

    public string CurrentTab => Tab is "doors" or "ads" ? Tab : "commissions";
    public bool CanEdit => database.Available;
    public string? Error { get; private set; }
    [TempData] public string? Message { get; set; }

    public List<JobCommission> Commissions { get; private set; } = [];
    public List<PersonCommissions> People { get; private set; } = [];
    public List<CommissionPayment> Payments { get; private set; } = [];
    public List<SalesActivity> Activity { get; private set; } = [];
    public List<AdSpend> Spend { get; private set; } = [];
    public List<LeadSource> Sources { get; private set; } = [];
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        ViewData["Title"] = "Sales & Marketing";
        ViewData["ModuleName"] = "Sales & Marketing";
        var today = LocalClock.Today;
        To = today;
        From = Period switch { "30" => today.AddDays(-30), "ytd" => new DateOnly(today.Year, 1, 1), _ => today.AddDays(-90) };

        try
        {
            if (CurrentTab == "commissions")
            {
                Commissions = await sales.CommissionsAsync(refresh, ct);
                People = SalesService.ByPerson(Commissions);
                Payments = await sales.PaymentsAsync(ct);
            }
            else if (CurrentTab == "doors")
            {
                Activity = await sales.ActivityAsync(From, To, ct);
            }
            else
            {
                Spend = await sales.SpendAsync(From.ToString("yyyy-MM"), To.ToString("yyyy-MM"), ct);
                var jobs = (await sales.SoldJobsAsync(refresh, ct)).Where(j => j.ApprovedContract > 0).ToList();
                Sources = SalesService.LeadSources(jobs, Spend);
            }
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading sales failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
        }
    }

    public IEnumerable<JobCommission> ShownCommissions => Show == "all" ? Commissions : Commissions.Where(c => c.Owed >= 1 || c.Rule is null);

    public async Task<IActionResult> OnPostRuleAsync(string person, string? percent, string? basis, string? notes, CancellationToken ct)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(person)) return RedirectToPage(new { tab = Tab });
        if (!decimal.TryParse(percent, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) || p < 0 || p > 100)
        {
            Message = "Commission % should be a number from 0 to 100."; return RedirectToPage(new { tab = Tab });
        }
        await sales.SaveRuleAsync(person.Trim(), p, basis ?? "collected", string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "commissions", "rule", person, new { percent = p, basis, notes }, "Done")], ct);
        Message = $"Saved {person}'s commission: {p}% {SalesService.BasisLabel(basis ?? "collected")}.";
        return RedirectToPage(new { tab = Tab, show = Show });
    }

    public async Task<IActionResult> OnPostPayAsync(string jobId, string? jobName, string person, string? amount, DateOnly? paidOn, string? note, CancellationToken ct)
    {
        if (!CanEdit) return RedirectToPage(new { tab = Tab });
        if (!decimal.TryParse(amount?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) || a == 0)
        {
            Message = "Enter the amount paid."; return RedirectToPage(new { tab = Tab, show = Show });
        }
        await sales.AddPaymentAsync(jobId, jobName, person, Math.Round(a, 2), paidOn ?? LocalClock.Today, note, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "commissions", "pay", $"{person} · {jobName}", new { jobId, amount = a, paidOn, note }, "Done")], ct);
        Message = $"Recorded {Format.Money(a)} paid to {person} for {jobName}.";
        return RedirectToPage(new { tab = Tab, show = Show });
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int id, CancellationToken ct)
    {
        if (CanEdit) { await sales.DeletePaymentAsync(id, ct); await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "commissions", "delete-payment", id.ToString(), null, "Done")], ct); }
        return RedirectToPage(new { tab = Tab, show = Show });
    }

    public async Task<IActionResult> OnPostActivityAsync(DateOnly? date, string? rep, int doors, int leads, int sold, string? soldAmount, string? notes, CancellationToken ct)
    {
        if (!CanEdit) return RedirectToPage(new { tab = "doors" });
        if (string.IsNullOrWhiteSpace(rep)) { Message = "Enter the rep's name."; return RedirectToPage(new { tab = "doors", period = Period }); }
        decimal.TryParse(soldAmount?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt);
        await sales.AddActivityAsync(new SalesActivity
        {
            Date = date ?? LocalClock.Today, Rep = rep.Trim(), Doors = Math.Max(0, doors), Leads = Math.Max(0, leads), Sold = Math.Max(0, sold),
            SoldAmount = Math.Max(0, amt), Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), CreatedAt = DateTime.UtcNow, CreatedBy = User(),
        }, ct);
        Message = "Logged.";
        return RedirectToPage(new { tab = "doors", period = Period });
    }

    public async Task<IActionResult> OnPostDeleteActivityAsync(int id, CancellationToken ct)
    {
        if (CanEdit) await sales.DeleteActivityAsync(id, ct);
        return RedirectToPage(new { tab = "doors", period = Period });
    }

    public async Task<IActionResult> OnPostSpendAsync(string? month, string? channel, string? amount, int? leads, CancellationToken ct)
    {
        if (!CanEdit) return RedirectToPage(new { tab = "ads" });
        if (string.IsNullOrWhiteSpace(month) || month.Length != 7 || string.IsNullOrWhiteSpace(channel)
            || !decimal.TryParse(amount?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a))
        {
            Message = "Enter a month, a channel and an amount."; return RedirectToPage(new { tab = "ads", period = Period });
        }
        await sales.SaveSpendAsync(month, channel.Trim(), Math.Round(a, 2), leads, User(), ct);
        Message = $"Saved {channel} spend for {month}.";
        return RedirectToPage(new { tab = "ads", period = Period });
    }

    public async Task<IActionResult> OnPostDeleteSpendAsync(int id, CancellationToken ct)
    {
        if (CanEdit) await sales.DeleteSpendAsync(id, ct);
        return RedirectToPage(new { tab = "ads", period = Period });
    }

    private string User() => me.Email ?? me.DisplayName ?? "unknown";

    public static string Rate(CommissionRule? r) => r is null ? "No rate set" : $"{r.Percent:0.##}% {SalesService.BasisLabel(r.Basis)}";
}
