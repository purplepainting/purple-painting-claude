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

public class IndexModel(SalesService sales, CommissionPayroll commissionPayroll,GoogleAdsService googleAds, MetaAdsService metaAds, QuickBooksAdSpendService qbAds, JobTreadLeadsService jtLeads, ChangeLogWriter changeLog, DatabaseStatus database, CurrentUser me, Permissions permissions, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"commissions" (default), "doors", "ads".</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>Commissions filter: "owed" (default), "all".</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    /// <summary>Door-to-door and ads period: "30" days, "90" (default), "ytd", "12" (last 12 months, ads only).</summary>
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }

    /// <summary>The tabs this person may see, in order: key, label, view permission.</summary>
    public IReadOnlyList<(string Key, string Label)> Tabs =>
        new[] { ("commissions", "Commissions", Perm.CommissionsView), ("doors", "Door-to-door", Perm.SalesView), ("ads", "Ads & lead sources", Perm.AdsView) }
            .Where(t => permissions.Can(t.Item3)).Select(t => (t.Item1, t.Item2)).ToList();

    /// <summary>The tab asked for when allowed, else the first allowed one.</summary>
    public string CurrentTab
    {
        get
        {
            var asked = Tab is "doors" or "ads" ? Tab : "commissions";
            return Tabs.Any(t => t.Key == asked) ? asked : Tabs.FirstOrDefault().Key ?? "commissions";
        }
    }
    /// <summary>The database is up (saving possible at all).</summary>
    public bool CanEdit => database.Available;
    public bool CanEditCommissions => database.Available && permissions.Can(Perm.CommissionsEdit);
    public bool CanEditDoors => database.Available && permissions.Can(Perm.SalesEdit);
    public bool CanEditAds => database.Available && permissions.Can(Perm.AdsEdit);
    public string? Error { get; private set; }
    [TempData] public string? Message { get; set; }
    /// <summary>Job id of the commission row to keep open (after recording or deleting a payment there).</summary>
    [BindProperty(SupportsGet = true)] public string? Open { get; set; }

    /// <summary>Payroll checks month ("yyyy-MM"); default last month.</summary>
    [BindProperty(SupportsGet = true)] public string? Month { get; set; }
    public DateOnly CheckMonth => CommissionPayroll.ParseMonth(Month);
    /// <summary>Commission paychecks (6490) vs recorded payments for <see cref="CheckMonth"/> (commissions tab).</summary>
    public CommissionPayrollView? Checks { get; private set; }
    /// <summary>Paid &amp; locked commission months (all people and months).</summary>
    public List<PayLock> Locks { get; private set; } = [];
    /// <summary>A payment dated then for this person sits in a paid &amp; locked month (can't be added or deleted).</summary>
    public bool IsLocked(string person, DateOnly date) =>
        Locks.Any(l => l.Month == CommissionPayroll.Key(CommissionPayroll.MonthOf(date)) && l.Person.Equals(person, StringComparison.OrdinalIgnoreCase));
    public DateTime? QbAsOf => PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.DataAsOf;
    public List<JobCommission> Commissions { get; private set; } = [];
    public List<PersonCommissions> People { get; private set; } = [];
    public List<CommissionPayment> Payments { get; private set; } = [];
    public List<SalesActivity> Activity { get; private set; } = [];
    public List<AdSpend> Spend { get; private set; } = [];
    /// <summary>Facebook/Instagram ads from the Meta API (ads tab). Null on other tabs.</summary>
    public MetaAdsView? Meta { get; private set; }
    /// <summary>Refresh link in the Facebook/Instagram section: drop cached Meta data.</summary>
    [BindProperty(SupportsGet = true)] public bool MetaRefresh { get; set; }
    public List<LeadSource> Sources { get; private set; } = [];
    /// <summary>New JobTread customer accounts in the period by Lead Source (ads tab).</summary>
    public JobTreadLeads? JtLeads { get; private set; }
    /// <summary>Spend counted for the period: Google Ads and Facebook/Instagram from their APIs when connected, everything else as entered.</summary>
    public List<SpendLine> CountedSpend { get; private set; } = [];
    /// <summary>Hand-entered rows not counted because an API (Google Ads, Meta) has that channel and month, by AdSpend id.</summary>
    public Dictionary<int, ReplacedSpend> ReplacedSpend { get; private set; } = [];
    /// <summary>API channels feeding the ads view right now, e.g. ("Google Ads", "Google Ads API"), ("Facebook/Instagram", "Meta").</summary>
    public List<(string Channel, string Api)> ApiChannels { get; private set; } = [];
    /// <summary>Ad spend booked in QuickBooks (6410/6420/6430/6440 by default), last 12 months. Null on other tabs.</summary>
    public BookedAdSpend? Booked { get; private set; }
    /// <summary>Platform reported vs booked in QuickBooks per channel and month in the period.</summary>
    public List<ReconcileRow> Reconcile { get; private set; } = [];
    /// <summary>QuickBooks booked lines not counted because an API covers that channel and month.</summary>
    public List<ReplacedLine> ReplacedBooked { get; private set; } = [];
    /// <summary>Spend in channels that aren't ad spend (Sales:NonAdChannels): shown in the reconcile only, not in totals or lead sources.</summary>
    public List<SpendLine> NonAdSpend { get; private set; } = [];
    /// <summary>Refresh link in the reconcile section: drop cached QuickBooks data.</summary>
    [BindProperty(SupportsGet = true)] public bool QbRefresh { get; set; }
    public GoogleAdsView? Google { get; private set; }
    public List<AdsMonth> GoogleMonths { get; private set; } = [];
    public List<AdsCampaign> GoogleCampaigns { get; private set; } = [];
    public string GoogleChannel => googleAds.Channel;
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        ViewData["Title"] = "Sales & Marketing";
        ViewData["ModuleName"] = "Sales & Marketing";
        var today = LocalClock.Today;
        To = today;
        From = Period switch
        {
            "30" => today.AddDays(-30),
            "ytd" => new DateOnly(today.Year, 1, 1),
            "12" when Tab == "ads" => new DateOnly(today.Year, today.Month, 1).AddMonths(-11),
            _ => today.AddDays(-90),
        };

        try
        {
            if (CurrentTab == "commissions")
            {
                if (QbRefresh) PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.ClearCache();
                await LoadCommissionsAsync(refresh, ct);
                Payments = await sales.PaymentsAsync(ct);
                Checks = await commissionPayroll.GetAsync(CheckMonth, Commissions, People, ct);
                Locks = await commissionPayroll.LocksAsync(ct);
            }
            else if (CurrentTab == "doors")
            {
                Activity = await sales.ActivityAsync(From, To, ct);
            }
            else
            {
                string fromMonth = From.ToString("yyyy-MM"), toMonth = To.ToString("yyyy-MM");
                Google = await googleAds.GetAsync(refresh, ct);
                if (Google.Report is { } report)
                {
                    GoogleMonths = GoogleAdsService.Months(report);
                    GoogleCampaigns = GoogleAdsService.Campaigns(report, fromMonth, toMonth);
                }
                Meta = await metaAds.LoadAsync(From, To, refresh || MetaRefresh, ct);
                Spend = await sales.SpendAsync(fromMonth, toMonth, ct);

                if (QbRefresh) PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.ClearCache();
                Booked = await qbAds.GetAsync(googleAds.Channel, ct);

                // One source per channel and month: platform API, else QuickBooks booked, else entered by hand (never two).
                var apis = new List<ApiSpendSource>();
                if (Google.Ready) apis.Add(SalesService.GoogleSpendSource(GoogleMonths, googleAds.Channel));
                if (Meta.Report is { } metaReport) apis.Add(MetaAdsService.SpendSource(metaReport));
                ApiChannels = apis.Select(a => (a.Channel, a.ApiName)).ToList();
                var bookedMonths = Booked.Ready ? Booked.Months : null;
                var sources = bookedMonths is null ? apis : [.. apis, .. QuickBooksAdSpendService.SpendSources(Booked, googleAds.Channel)];
                var merged = SalesService.MergeApiSpend(Spend, sources, fromMonth, toMonth);
                // Marketing that isn't ad spend (Sales:NonAdChannels, default Website SEO, Printing) stays out of the totals and lead sources.
                var nonAd = qbAds.NonAdChannels;
                bool IsNonAd(string channel) => nonAd.Contains(SalesService.ChannelFor(channel, googleAds.Channel));
                NonAdSpend = merged.Lines.Where(l => IsNonAd(l.Channel)).ToList();
                CountedSpend = merged.Lines.Where(l => !IsNonAd(l.Channel)).ToList();
                ReplacedSpend = merged.Replaced.ToDictionary(r => r.Row.Id);
                ReplacedBooked = merged.ReplacedLines;
                Reconcile = SalesService.Reconcile(apis, bookedMonths, merged.Lines, fromMonth, toMonth, googleAds.Channel)
                    .Select(r => IsNonAd(r.Channel) ? r with { NotAdSpend = true } : r).ToList();
                var jobs = (await sales.SoldJobsAsync(refresh, ct)).Where(j => j.ApprovedContract > 0).ToList();
                // New JobTread customer accounts by Lead Source, over the same whole months as the spend.
                JtLeads = await jtLeads.GetAsync(new DateOnly(From.Year, From.Month, 1), To, refresh, ct);
                Sources = SalesService.LeadSources(jobs, CountedSpend, googleAds.Channel, JtLeads);
            }
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading sales failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
        }
    }

    private async Task LoadCommissionsAsync(bool refresh, CancellationToken ct)
    {
        Commissions = await sales.CommissionsAsync(refresh, ct);
        People = SalesService.ByPerson(Commissions, await sales.UnallocatedAsync(ct));
    }

    public IEnumerable<JobCommission> ShownCommissions => Show == "all" ? Commissions : Commissions.Where(c => c.Owed >= 1 || c.JobId == Open);
    /// <summary>Jobs whose salesperson has no commission rate yet (nothing can be owed until it's set).</summary>
    public int JobsWithoutRate => Commissions.Count(c => c.Rule is null);

    public async Task<IActionResult> OnPostRuleAsync(string person, string? percent, string? basis, string? notes, CancellationToken ct)
    {
        if (!CanEditCommissions || string.IsNullOrWhiteSpace(person)) return RedirectToPage(new { tab = Tab });
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
        if (!CanEditCommissions) return RedirectToPage(new { tab = Tab });
        if (!decimal.TryParse(amount?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) || a == 0)
        {
            Message = "Enter the amount paid."; return RedirectToPage(new { tab = Tab, show = Show });
        }
        var on = paidOn ?? LocalClock.Today;
        if (await commissionPayroll.IsLockedAsync(person, on, ct))
        {
            Message = $"{person}'s {CommissionPayroll.Label(CommissionPayroll.MonthOf(on))} commissions are paid & locked, so a payment can't be dated then. Date it in an open month, or unlock that month (Payroll checks).";
            return RedirectToPage(null, null, new { tab = Tab, show = Show, open = jobId }, "comm-" + jobId);
        }
        await sales.AddPaymentAsync(jobId, jobName, person, Math.Round(a, 2), on, note, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "commissions", "pay", $"{person} · {jobName}", new { jobId, amount = a, paidOn, note }, "Done")], ct);
        Message = $"Recorded {Format.Money(a)} paid to {person} for {jobName}.";
        return RedirectToPage(null, null, new { tab = Tab, show = Show, open = jobId }, "comm-" + jobId);
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int id, string? jobId, CancellationToken ct)
    {
        if (CanEditCommissions && await sales.FindPaymentAsync(id, ct) is { } p)
        {
            if (await commissionPayroll.IsLockedAsync(p.Person, p.PaidOn, ct))
                Message = $"That payment is in {p.Person}'s paid & locked {CommissionPayroll.Label(CommissionPayroll.MonthOf(p.PaidOn))}, so it can't be deleted. Unlock the month first (Payroll checks).";
            else
            {
                await sales.DeletePaymentAsync(id, ct);
                await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "commissions", "delete-payment", id.ToString(), new { person = p.Person, job = p.JobName, amount = p.Amount, paidOn = p.PaidOn }, "Done")], ct);
                Message = "Payment deleted.";
            }
        }
        if (Open?.StartsWith("chk-") == true) return BackToChecks(Open);
        return string.IsNullOrEmpty(jobId)
            ? RedirectToPage(new { tab = Tab, show = Show })
            : RedirectToPage(null, null, new { tab = Tab, show = Show, open = jobId }, "comm-" + jobId);
    }

    public async Task<IActionResult> OnPostActivityAsync(DateOnly? date, string? rep, int doors, int leads, int sold, string? soldAmount, string? notes, CancellationToken ct)
    {
        if (!CanEditDoors) return RedirectToPage(new { tab = "doors" });
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
        if (CanEditDoors) await sales.DeleteActivityAsync(id, ct);
        return RedirectToPage(new { tab = "doors", period = Period });
    }

    public async Task<IActionResult> OnPostSpendAsync(string? month, string? channel, string? amount, int? leads, CancellationToken ct)
    {
        if (!CanEditAds) return RedirectToPage(new { tab = "ads" });
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
        if (CanEditAds) await sales.DeleteSpendAsync(id, ct);
        return RedirectToPage(new { tab = "ads", period = Period });
    }

    // ---- Payroll checks (commission paychecks from QuickBooks) and Paid & locked ----

    public static string Slug(string name) => new(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private IActionResult BackToChecks(string? open) =>
        RedirectToPage(null, null, new { show = Show, month = CommissionPayroll.Key(CheckMonth), open }, open ?? "checks");

    /// <summary>Loads the month's rows fresh (for the payroll handlers).</summary>
    private async Task<CommissionPayrollView> ReloadChecksAsync(CancellationToken ct)
    {
        await LoadCommissionsAsync(false, ct);
        return await commissionPayroll.GetAsync(CheckMonth, Commissions, People, ct);
    }

    public async Task<IActionResult> OnPostPayFromPayrollAsync(string person, CancellationToken ct)
    {
        var open = "chk-" + Slug(person);
        if (!CanEditCommissions) return BackToChecks(open);
        var view = await ReloadChecksAsync(ct);
        var row = view.Rows.FirstOrDefault(r => r.Name.Equals(person, StringComparison.OrdinalIgnoreCase));
        if (row is null || row.Lock is not null || row.Unrecorded <= 0)
        {
            Message = row?.Lock is not null ? $"{person}'s {CommissionPayroll.Label(CheckMonth)} is paid & locked." : $"Nothing from payroll left to record for {person} in {CommissionPayroll.Label(CheckMonth)}.";
            return BackToChecks(open);
        }
        Message = await RecordAsync(row, ct);
        return BackToChecks(open);
    }

    private async Task<string> RecordAsync(CommissionCheckRow row, CancellationToken ct)
    {
        var last = row.Checks.OrderByDescending(c => c.Date).First();
        var refs = string.Join(", ", row.Checks.Select(c => c.Num).Where(n => n is not null));
        var made = await commissionPayroll.RecordFromPayrollAsync(row.Name, row.Unrecorded, last.Date, refs.Length > 0 ? refs : null, Commissions, User(), ct);
        var toJobs = made.Where(p => p.JobId != CommissionPayroll.Unallocated).ToList();
        var un = made.Where(p => p.JobId == CommissionPayroll.Unallocated).Sum(p => p.Amount);
        static string Cents(decimal d) => d.ToString("$#,##0.00;-$#,##0.00", CultureInfo.InvariantCulture);
        return $"Recorded {Cents(row.Unrecorded)} for {row.Name} from payroll{(refs.Length > 0 ? " (check " + refs + ")" : "")}: "
            + string.Join(", ", toJobs.Select(p => $"{Cents(p.Amount)} to {p.JobName}"))
            + (un > 0 ? $"{(toJobs.Count > 0 ? "; " : "")}{Cents(un)} unallocated (more than their owed jobs)" : "") + ".";
    }

    public async Task<IActionResult> OnPostLockAsync(string? person, CancellationToken ct)
    {
        var open = person is null ? null : "chk-" + Slug(person);
        if (!CanEditCommissions) return BackToChecks(open);
        var label = CommissionPayroll.Label(CheckMonth);
        var view = await ReloadChecksAsync(ct);
        var targets = view.Rows.Where(r => person is null ? r.ReadyToLock : r.CanLock && r.Name.Equals(person, StringComparison.OrdinalIgnoreCase)).ToList();
        if (person is not null && targets.Count == 0)
        {
            Message = $"Nothing to lock for {person} in {label} (no payment recorded or payroll check found).";
            return BackToChecks(open);
        }
        // A check that isn't recorded here yet is recorded first, so the dashboard and payroll agree.
        var notes = new List<string>();
        foreach (var t in targets.Where(t => t.Unrecorded > 0)) notes.Add(await RecordAsync(t, ct));
        if (notes.Count > 0)
        {
            var names = targets.Select(t => t.Name).ToList();
            view = await ReloadChecksAsync(ct);
            targets = view.Rows.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        var paid = await commissionPayroll.PaymentsInAsync(CheckMonth, ct);
        foreach (var t in targets) await commissionPayroll.LockAsync(t, CheckMonth, Commissions, paid, User(), ct);
        Message = (person is not null ? $"{person}'s {label} commissions are paid & locked."
            : targets.Count == 0 ? $"No one in {label} is ready to lock (a payroll check that matches what's recorded)."
            : $"Locked {targets.Count} {(targets.Count == 1 ? "person" : "people")} for {label}.")
            + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        return BackToChecks(open ?? "checks");
    }

    public async Task<IActionResult> OnPostUnlockAsync(string person, string? reason, CancellationToken ct)
    {
        var open = "chk-" + Slug(person);
        if (!CanEditCommissions) return BackToChecks(open);
        if (string.IsNullOrWhiteSpace(reason)) { Message = "Say why you're unlocking it."; return BackToChecks(open); }
        await commissionPayroll.UnlockAsync(person, CheckMonth, reason, User(), ct);
        Message = $"Unlocked {person}'s {CommissionPayroll.Label(CheckMonth)} commissions: payments dated then can be changed again.";
        return BackToChecks(open);
    }

    private string User() => me.Email ?? me.DisplayName ?? "unknown";

    public static string Rate(CommissionRule? r) => r is null ? "No rate set" : $"{r.Percent:0.##}% {SalesService.BasisLabel(r.Basis)}";
}
