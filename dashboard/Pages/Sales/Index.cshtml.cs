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

public class IndexModel(SalesService sales, GoogleAdsService googleAds, MetaAdsService metaAds, JobTreadLeadsService jtLeads, ChangeLogWriter changeLog, DatabaseStatus database, CurrentUser me, Permissions permissions, ILogger<IndexModel> logger) : PageModel
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
                string fromMonth = From.ToString("yyyy-MM"), toMonth = To.ToString("yyyy-MM");
                Google = await googleAds.GetAsync(refresh, ct);
                if (Google.Report is { } report)
                {
                    GoogleMonths = GoogleAdsService.Months(report);
                    GoogleCampaigns = GoogleAdsService.Campaigns(report, fromMonth, toMonth);
                }
                Meta = await metaAds.LoadAsync(From, To, refresh || MetaRefresh, ct);
                Spend = await sales.SpendAsync(fromMonth, toMonth, ct);

                // API channels replace hand-entered rows for the same channel and month (never both).
                var apis = new List<ApiSpendSource>();
                if (Google.Ready) apis.Add(SalesService.GoogleSpendSource(GoogleMonths, googleAds.Channel));
                if (Meta.Report is { } metaReport) apis.Add(MetaAdsService.SpendSource(metaReport));
                ApiChannels = apis.Select(a => (a.Channel, a.ApiName)).ToList();
                var merged = SalesService.MergeApiSpend(Spend, apis, fromMonth, toMonth);
                CountedSpend = merged.Lines;
                ReplacedSpend = merged.Replaced.ToDictionary(r => r.Row.Id);
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

    public IEnumerable<JobCommission> ShownCommissions => Show == "all" ? Commissions : Commissions.Where(c => c.Owed >= 1 || c.JobId == Open);
    /// <summary>Jobs whose salesperson has no commission rate yet (nothing can be owed until it's set).</summary>
    public int JobsWithoutRate => Commissions.Count(c => c.Rule is null);

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
        return RedirectToPage(null, null, new { tab = Tab, show = Show, open = jobId }, "comm-" + jobId);
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int id, string? jobId, CancellationToken ct)
    {
        if (CanEdit)
        {
            await sales.DeletePaymentAsync(id, ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "commissions", "delete-payment", id.ToString(), null, "Done")], ct);
            Message = "Payment deleted.";
        }
        return string.IsNullOrEmpty(jobId)
            ? RedirectToPage(new { tab = Tab, show = Show })
            : RedirectToPage(null, null, new { tab = Tab, show = Show, open = jobId }, "comm-" + jobId);
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
