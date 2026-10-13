using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Modules.Sales;

/// <summary>A job's commission for its salesperson: what's earned under their rule, what's been paid, what's owed.</summary>
public sealed record JobCommission(
    string JobId, string JobName, string? Number, string Person, string? Stage,
    decimal Contract, decimal Collected, decimal Profit,
    CommissionRule? Rule, decimal Earned, decimal Paid)
{
    public decimal Owed => Math.Max(0, Earned - Paid);
}

public sealed record PersonCommissions(string Person, CommissionRule? Rule, int Jobs, decimal Earned, decimal Paid)
{
    public decimal Owed => Math.Max(0, Earned - Paid);
}

/// <summary>A lead source: jobs sold by JobTread's "How bid came in", spend and leads for the channel with the same name. ApiSource = the API the spend came from ("Google Ads", "Meta"), null when entered by hand.</summary>
public sealed record LeadSource(string Source, int Jobs, decimal Contract, decimal Spend, decimal? Leads = null, string? ApiSource = null)
{
    public bool FromApi => ApiSource is not null;
}

/// <summary>One month of spend for a channel: entered by hand (AdSpends) or from an ad platform API (ApiSource: "Google Ads", "Meta").</summary>
public sealed record SpendLine(string Month, string Channel, decimal Amount, decimal? Leads, string? ApiSource = null)
{
    public bool FromApi => ApiSource is not null;
}

/// <summary>
/// One ad platform API feeding the ads view: its name and channel, which hand-entered channel names mean the same thing,
/// which months it covers, and its monthly spend/leads (SpendLines with ApiSource set).
/// </summary>
public sealed record ApiSpendSource(string ApiName, string Channel, Func<string, bool> IsSameChannel, Func<string, bool> Covers, IReadOnlyList<SpendLine> Months);

/// <summary>A hand-entered row not counted because an API covers that channel and month; ApiAmount = the API's figure for it.</summary>
public sealed record ReplacedSpend(AdSpend Row, string ApiName, decimal ApiAmount);

/// <summary>Spend for a period with API channels merged in. Replaced = hand-entered rows not counted because an API covers them.</summary>
public sealed record MergedSpend(List<SpendLine> Lines, List<ReplacedSpend> Replaced);

/// <summary>
/// Sales &amp; Marketing: commissions per salesperson (rules and payments in the dashboard database, jobs and
/// salesperson from JobTread), door-to-door activity and monthly ad spend.
/// </summary>
public sealed class SalesService(ProjectsService projects, IServiceProvider services, DatabaseStatus database)
{
    public static readonly string[] Bases = ["collected", "contract", "profit"];

    public static string BasisLabel(string basis) => basis switch
    {
        "contract" => "of contract",
        "profit" => "of job profit",
        _ => "of money collected",
    };

    private DashboardDb Db => services.GetRequiredService<DashboardDb>();

    /// <summary>Jobs with a salesperson (open, plus closed in the last year).</summary>
    public async Task<List<ProjectSummary>> SoldJobsAsync(bool refresh, CancellationToken ct)
    {
        var open = await projects.GetOpenJobsAsync(refresh, ct);
        var closed = await projects.GetClosedJobsAsync(LocalClock.Today.AddDays(-365), refresh, ct);
        return open.Concat(closed)
            .Where(j => !j.IsInternal && j.Stage is not null && !string.IsNullOrWhiteSpace(j.SalesPerson))
            .GroupBy(j => j.Id).Select(g => g.First()).ToList();
    }

    public async Task<List<JobCommission>> CommissionsAsync(bool refresh, CancellationToken ct)
    {
        var jobs = await SoldJobsAsync(refresh, ct);
        var rules = database.Available ? await Db.CommissionRules.AsNoTracking().ToDictionaryAsync(r => r.Person, StringComparer.OrdinalIgnoreCase, ct) : [];
        var paid = database.Available
            ? (await Db.CommissionPayments.AsNoTracking().ToListAsync(ct)).GroupBy(p => (p.JobId, p.Person.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount))
            : [];

        return jobs.Select(j =>
        {
            var person = j.SalesPerson!.Trim();
            rules.TryGetValue(person, out var rule);
            var contract = j.Contract ?? 0;
            var collected = Math.Max(0, j.InvoicedNet - j.Unpaid);
            var basisAmount = rule?.Basis switch { "contract" => contract, "profit" => Math.Max(0, j.Profit), _ => collected };
            var earned = rule is null ? 0 : Math.Round(basisAmount * rule.Percent / 100m, 2);
            return new JobCommission(j.Id, j.Name, j.Number, person, j.Stage, contract, collected, j.Profit, rule, earned,
                paid.GetValueOrDefault((j.Id, person.ToLowerInvariant())));
        }).OrderByDescending(c => c.Owed).ThenBy(c => c.Person).ToList();
    }

    public static List<PersonCommissions> ByPerson(IEnumerable<JobCommission> jobs) => jobs
        .GroupBy(c => c.Person, StringComparer.OrdinalIgnoreCase)
        .Select(g => new PersonCommissions(g.First().Person, g.First().Rule, g.Count(), g.Sum(c => c.Earned), g.Sum(c => c.Paid)))
        .OrderByDescending(p => p.Owed).ThenBy(p => p.Person).ToList();

    public async Task SaveRuleAsync(string person, decimal percent, string basis, string? notes, string user, CancellationToken ct)
    {
        var db = Db;
        var rule = await db.CommissionRules.FirstOrDefaultAsync(r => r.Person == person, ct);
        if (rule is null) db.CommissionRules.Add(rule = new CommissionRule { Person = person, Basis = basis });
        rule.Percent = percent;
        rule.Basis = Bases.Contains(basis) ? basis : "collected";
        rule.Notes = notes;
        rule.UpdatedAt = DateTime.UtcNow;
        rule.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
    }

    public async Task AddPaymentAsync(string jobId, string? jobName, string person, decimal amount, DateOnly paidOn, string? note, string user, CancellationToken ct)
    {
        var db = Db;
        db.CommissionPayments.Add(new CommissionPayment
        {
            JobId = jobId, JobName = jobName, Person = person, Amount = amount, PaidOn = paidOn, Note = note,
            CreatedAt = DateTime.UtcNow, CreatedBy = user,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<CommissionPayment>> PaymentsAsync(CancellationToken ct) =>
        database.Available ? await Db.CommissionPayments.AsNoTracking().OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.Id).Take(200).ToListAsync(ct) : [];

    public async Task DeletePaymentAsync(int id, CancellationToken ct) => await Db.CommissionPayments.Where(p => p.Id == id).ExecuteDeleteAsync(ct);

    // ---- Door-to-door ----

    public async Task<List<SalesActivity>> ActivityAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        database.Available
            ? await Db.SalesActivities.AsNoTracking().Where(a => a.Date >= from && a.Date <= to).OrderByDescending(a => a.Date).ThenByDescending(a => a.Id).ToListAsync(ct)
            : [];

    public async Task AddActivityAsync(SalesActivity a, CancellationToken ct)
    {
        var db = Db;
        db.SalesActivities.Add(a);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteActivityAsync(int id, CancellationToken ct) => await Db.SalesActivities.Where(a => a.Id == id).ExecuteDeleteAsync(ct);

    // ---- Ads & lead sources ----

    public async Task<List<AdSpend>> SpendAsync(string fromMonth, string toMonth, CancellationToken ct) =>
        database.Available
            ? await Db.AdSpends.AsNoTracking().Where(s => string.Compare(s.Month, fromMonth) >= 0 && string.Compare(s.Month, toMonth) <= 0).OrderByDescending(s => s.Month).ThenBy(s => s.Channel).ToListAsync(ct)
            : [];

    public async Task SaveSpendAsync(string month, string channel, decimal amount, int? leads, string user, CancellationToken ct)
    {
        var db = Db;
        var row = await db.AdSpends.FirstOrDefaultAsync(s => s.Month == month && s.Channel == channel, ct);
        if (row is null) db.AdSpends.Add(row = new AdSpend { Month = month, Channel = channel });
        row.Amount = amount;
        row.Leads = leads;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSpendAsync(int id, CancellationToken ct) => await Db.AdSpends.Where(s => s.Id == id).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Whether a hand-entered channel is Google Ads (the API's channel name, or "Google", "Google Ads", "AdWords").
    /// Local Services Ads or the Business Profile aren't in the Google Ads API, so names like "Google LSA" stay manual.
    /// </summary>
    public static bool IsGoogleAdsChannel(string channel, string adsChannel)
    {
        static string N(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var n = N(channel);
        return n == N(adsChannel) || n is "google" or "googleads" or "adwords" or "googleadwords";
    }

    /// <summary>
    /// Google Ads (API) as an <see cref="ApiSpendSource"/>: covers the months the API returned; hand-entered rows named like
    /// Google Ads (<see cref="IsGoogleAdsChannel"/>) for those months are replaced. Meta's is MetaAdsService.SpendSource.
    /// </summary>
    public static ApiSpendSource GoogleSpendSource(IReadOnlyList<AdsMonth> google, string adsChannel)
    {
        var months = google.Select(m => m.Month).ToHashSet();
        return new ApiSpendSource("Google Ads", adsChannel, c => IsGoogleAdsChannel(c, adsChannel), months.Contains,
            google.Where(m => m.Cost > 0 || m.Conversions > 0).Select(m => new SpendLine(m.Month, adsChannel, Math.Round(m.Cost, 2), m.Conversions, "Google Ads")).ToList());
    }

    /// <summary>
    /// Spend for months from..to: each API channel (Google Ads, Facebook/Instagram) from its API, every other channel as
    /// entered. A hand-entered row for a channel and month an API covers isn't counted (never both, so nothing is double
    /// counted); it's returned in Replaced with the API's figure. Without API sources everything entered counts.
    /// </summary>
    public static MergedSpend MergeApiSpend(IEnumerable<AdSpend> manual, IEnumerable<ApiSpendSource> apis, string fromMonth, string toMonth)
    {
        bool InRange(string month) => string.CompareOrdinal(month, fromMonth) >= 0 && string.CompareOrdinal(month, toMonth) <= 0;
        var sources = apis.ToList();
        var lines = new List<SpendLine>();
        var replaced = new List<ReplacedSpend>();
        foreach (var s in manual)
        {
            var api = sources.FirstOrDefault(a => a.Covers(s.Month) && a.IsSameChannel(s.Channel.Trim()));
            if (api is null) lines.Add(new SpendLine(s.Month, s.Channel.Trim(), s.Amount, s.Leads));
            else replaced.Add(new ReplacedSpend(s, api.ApiName, api.Months.Where(m => m.Month == s.Month).Sum(m => m.Amount)));
        }
        lines.AddRange(sources.SelectMany(a => a.Months).Where(m => InRange(m.Month)));
        return new MergedSpend(lines, replaced);
    }

    /// <summary>
    /// Jobs sold by "How bid came in" (JobTread) next to ad spend for channels with the same name. Which ad
    /// produced which lead isn't tracked yet, so this is a side-by-side, not attribution.
    /// </summary>
    public static List<LeadSource> LeadSources(IEnumerable<ProjectSummary> jobs, IEnumerable<SpendLine> spend)
    {
        var bySource = jobs.GroupBy(j => string.IsNullOrWhiteSpace(j.HowBidCameIn) ? "Not set" : j.HowBidCameIn!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Jobs: g.Count(), Contract: g.Sum(j => j.Contract ?? 0)), StringComparer.OrdinalIgnoreCase);
        var spendBy = spend.GroupBy(s => s.Channel.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => (Amount: g.Sum(s => s.Amount), Leads: g.Any(s => s.Leads is not null) ? g.Sum(s => s.Leads ?? 0) : (decimal?)null, Api: g.Select(s => s.ApiSource).FirstOrDefault(a => a is not null)),
            StringComparer.OrdinalIgnoreCase);
        return bySource.Keys.Union(spendBy.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k =>
            {
                var sp = spendBy.GetValueOrDefault(k);
                return new LeadSource(k, bySource.GetValueOrDefault(k).Jobs, bySource.GetValueOrDefault(k).Contract, sp.Amount, sp.Leads, sp.Api);
            })
            .OrderByDescending(s => s.Contract).ToList();
    }
}
