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

/// <summary>
/// A lead source row: jobs sold to customers whose JobTread account has this Lead Source (mapped to a spend channel by
/// <see cref="SalesService.ChannelFor"/>), spend and platform-reported leads for that channel, and new JobTread customer
/// accounts in the period with this Lead Source (JtLeads). WonLeads = of those new accounts, how many have a sold job.
/// ApiSource = where the spend came from ("Google Ads", "Meta" APIs, or "QuickBooks" booked spend), null when entered by hand.
/// NoLeadSource = jobs whose customer has no Lead Source, grouped by the job's "How bid came in" instead.
/// </summary>
public sealed record LeadSource(string Source, int Jobs, decimal Contract, decimal Spend, decimal? Leads = null, string? ApiSource = null,
    int? JtLeads = null, int WonLeads = 0, bool NoLeadSource = false)
{
    public bool FromApi => ApiSource is not null;
    public decimal? CostPerJtLead => Spend > 0 && JtLeads > 0 ? Spend / JtLeads.Value : null;
    /// <summary>New accounts in the period that bought ÷ new accounts in the period.</summary>
    public decimal? CloseRate => JtLeads > 0 ? (decimal)WonLeads / JtLeads.Value : null;
}

/// <summary>One month of spend for a channel: entered by hand (AdSpends), from an ad platform API (ApiSource: "Google Ads", "Meta") or booked in QuickBooks (ApiSource: "QuickBooks").</summary>
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

/// <summary>A source line (e.g. QuickBooks booked spend) not counted because a higher-priority source (an ad platform API) covers that channel and month.</summary>
public sealed record ReplacedLine(SpendLine Line, string ApiName, decimal ApiAmount);

/// <summary>
/// Spend for a period with API and QuickBooks channels merged in. Replaced = hand-entered rows not counted because a source
/// covers them; ReplacedLines = source lines (QuickBooks) not counted because a higher-priority source (API) covers them.
/// </summary>
public sealed record MergedSpend(List<SpendLine> Lines, List<ReplacedSpend> Replaced, List<ReplacedLine> ReplacedLines);

/// <summary>
/// One channel and month on the reconcile view: what the ad platform reported (null when no platform covers it), what's
/// booked in QuickBooks (null when QuickBooks isn't read), and what the ads view counts (and from where).
/// </summary>
public sealed record ReconcileRow(string Month, string Channel, string? Platform, decimal? PlatformAmount, decimal? Booked, decimal Counted, string CountedFrom)
{
    /// <summary>Platform − QuickBooks, when both are known.</summary>
    public decimal? Difference => PlatformAmount is { } p && Booked is { } b ? p - b : null;

    /// <summary>Worth a look: the difference is over $25 and over 5% of the platform figure (or of QuickBooks when the platform shows $0).</summary>
    public bool Flag => Difference is { } d && Math.Abs(d) > SalesService.ReconcileMinDollars
        && Math.Abs(d) > SalesService.ReconcileMinShare * (PlatformAmount is > 0 ? PlatformAmount.Value : Math.Abs(Booked ?? 0));
}

/// <summary>
/// Sales &amp; Marketing: commissions per salesperson (rules and payments in the dashboard database, jobs and
/// salesperson from JobTread), door-to-door activity and monthly ad spend.
/// </summary>
public sealed class SalesService(ProjectsService projects, PurpleDashboard.Modules.People.PeopleService people, PurpleDashboard.Modules.People.QbEmployeeStatus qbStatus, IServiceProvider services, DatabaseStatus database)
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

        var current = await CurrentStaffNamesAsync(ct);
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
        })
        // Nothing to pay on a job with no contract and nothing collected.
        .Where(c => c.Contract > 0 || c.Collected > 0 || c.Paid > 0)
        // Someone who's left (not JobTread staff anymore, or inactive in QuickBooks) only shows while they're still owed something.
        .Where(c => c.Owed > 0 || current is null || current.Contains(c.Person))
        .OrderByDescending(c => c.Owed).ThenBy(c => c.Person).ToList();
    }

    /// <summary>Names of current staff: internal in JobTread and not inactive in QuickBooks. Null if JobTread couldn't be read (then nobody is hidden).</summary>
    private async Task<HashSet<string>?> CurrentStaffNamesAsync(CancellationToken ct)
    {
        try
        {
            var active = await qbStatus.ActiveByIdAsync(ct);
            return (await people.GetStaffAsync(ct)).Where(s => PurpleDashboard.Modules.People.QbEmployeeStatus.IsCurrent(s, active))
                .Select(s => s.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is PurpleDashboard.Integrations.JobTread.JobTreadException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
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
        return n == N(adsChannel) || n is "google" or "googlead" or "googleads" or "adwords" or "googleadwords";
    }

    /// <summary>Channel name for Nextdoor (manual spend; JobTread's Lead Source says "NextDoor").</summary>
    public const string NextdoorChannel = "Nextdoor";

    /// <summary>
    /// The one place a JobTread "Lead Source" value (or a hand-entered spend channel) is turned into the channel the ads
    /// view groups by, so jobs, JobTread leads and spend line up: Google Ad / Google / Google Ads → the Google Ads channel
    /// (Google:AdsChannel, default "Google Ads"); Meta / Facebook / Instagram → "Facebook/Instagram" (Meta's channel);
    /// NextDoor → "Nextdoor". Anything else is kept as written (grouping is case-insensitive).
    /// </summary>
    public static string ChannelFor(string source, string googleChannel)
    {
        var s = source.Trim();
        if (IsGoogleAdsChannel(s, googleChannel)) return googleChannel;
        if (MetaAdsService.ChannelAliases.Contains(s)) return MetaAdsService.Channel;
        if (string.Equals(new string(s.Where(char.IsLetterOrDigit).ToArray()), "nextdoor", StringComparison.OrdinalIgnoreCase)) return NextdoorChannel;
        return s;
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
    /// Spend for months from..to, one source per channel and month, in priority order: the ad platform APIs (Google Ads,
    /// Facebook/Instagram) for the months they cover, then QuickBooks booked spend (<see cref="QuickBooksAdSpendService.SpendSources"/>)
    /// for channel-months with bookings, then hand-entered rows. <paramref name="apis"/> is in that priority order. A line or
    /// row for a channel and month a higher source covers isn't counted (never both, so nothing is double counted): hand-entered
    /// rows come back in Replaced, source lines (QuickBooks under an API) in ReplacedLines, each with the winning source's figure.
    /// Without sources everything entered counts.
    /// </summary>
    public static MergedSpend MergeApiSpend(IEnumerable<AdSpend> manual, IEnumerable<ApiSpendSource> apis, string fromMonth, string toMonth)
    {
        bool InRange(string month) => string.CompareOrdinal(month, fromMonth) >= 0 && string.CompareOrdinal(month, toMonth) <= 0;
        var sources = apis.ToList();
        ApiSpendSource? Winner(string month, string channel, int before) => sources.Take(before).FirstOrDefault(a => a.Covers(month) && a.IsSameChannel(channel));
        static decimal AmountOf(ApiSpendSource a, string month) => a.Months.Where(m => m.Month == month).Sum(m => m.Amount);

        var lines = new List<SpendLine>();
        var replaced = new List<ReplacedSpend>();
        var replacedLines = new List<ReplacedLine>();
        foreach (var s in manual)
        {
            var api = Winner(s.Month, s.Channel.Trim(), sources.Count);
            if (api is null) lines.Add(new SpendLine(s.Month, s.Channel.Trim(), s.Amount, s.Leads));
            else replaced.Add(new ReplacedSpend(s, api.ApiName, AmountOf(api, s.Month)));
        }
        for (var i = 0; i < sources.Count; i++)
        {
            foreach (var m in sources[i].Months.Where(m => InRange(m.Month)))
            {
                var higher = Winner(m.Month, m.Channel, i);
                if (higher is null) lines.Add(m);
                else replacedLines.Add(new ReplacedLine(m, higher.ApiName, AmountOf(higher, m.Month)));
            }
        }
        return new MergedSpend(lines, replaced, replacedLines);
    }

    public const decimal ReconcileMinDollars = 25m;
    public const decimal ReconcileMinShare = 0.05m;

    /// <summary>Display name of a spend source: "Google Ads API", "Meta API", "QuickBooks", or "Entered by hand" (null).</summary>
    public static string SourceLabel(string? apiSource) => apiSource switch
    {
        null => "Entered by hand",
        QuickBooksAdSpendService.SourceName => "QuickBooks",
        var a => a + " API",
    };

    /// <summary>
    /// Platform reported vs booked in QuickBooks per channel and month (from..to): a row for every month an API covers (its
    /// figure next to QuickBooks' for the same channel) and every channel-month with QuickBooks bookings but no platform.
    /// <paramref name="booked"/> is QuickBooks' per channel and month (null when QuickBooks isn't read); <paramref name="counted"/>
    /// is the merged spend, for the Counted column. Newest month first, then channels by spend.
    /// </summary>
    public static List<ReconcileRow> Reconcile(IEnumerable<ApiSpendSource> platforms, IReadOnlyList<SpendLine>? booked, IEnumerable<SpendLine> counted, string fromMonth, string toMonth, string googleChannel)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var months = new List<string>();
        if (DateOnly.TryParseExact(fromMonth + "-01", "yyyy-MM-dd", out var m0) && DateOnly.TryParseExact(toMonth + "-01", "yyyy-MM-dd", out var m1))
            for (var m = m0; m <= m1; m = m.AddMonths(1)) months.Add(m.ToString("yyyy-MM"));
        var countedList = counted.ToList();
        (decimal Amount, string From) Counted(string month, string channel)
        {
            var c = countedList.Where(l => l.Month == month && cmp.Equals(ChannelFor(l.Channel, googleChannel), channel)).ToList();
            var from = string.Join(" + ", c.Select(l => SourceLabel(l.ApiSource)).Distinct());
            return (c.Sum(l => l.Amount), from.Length == 0 ? "—" : from);
        }

        var rows = new List<ReconcileRow>();
        var platformList = platforms.ToList();
        foreach (var month in months)
        {
            foreach (var p in platformList.Where(p => p.Covers(month)))
            {
                decimal? b = booked is null ? null : booked.Where(l => l.Month == month && p.IsSameChannel(l.Channel)).Sum(l => l.Amount);
                var (amount, from) = Counted(month, p.Channel);
                rows.Add(new ReconcileRow(month, p.Channel, SourceLabel(p.ApiName), p.Months.Where(x => x.Month == month).Sum(x => x.Amount), b, amount, from));
            }
            foreach (var g in (booked ?? []).Where(l => l.Month == month && !platformList.Any(p => p.Covers(month) && p.IsSameChannel(l.Channel))).GroupBy(l => l.Channel, cmp))
            {
                var (amount, from) = Counted(month, g.Key);
                rows.Add(new ReconcileRow(month, g.Key, null, null, g.Sum(l => l.Amount), amount, from));
            }
        }
        var channelOrder = rows.GroupBy(r => r.Channel, cmp).OrderByDescending(g => g.Sum(r => Math.Max(r.PlatformAmount ?? 0, r.Booked ?? 0))).Select(g => g.Key).ToList();
        return rows.OrderByDescending(r => r.Month).ThenBy(r => channelOrder.FindIndex(c => cmp.Equals(c, r.Channel))).ToList();
    }

    /// <summary>Row name for sold jobs whose customer account has no Lead Source: grouped by the job's "How bid came in".</summary>
    public static string NoLeadSourceLabel(string? howBidCameIn) =>
        string.IsNullOrWhiteSpace(howBidCameIn) ? "No lead source" : $"No lead source (bid: {howBidCameIn.Trim()})";

    /// <summary>
    /// Ads → revenue by lead source. Sold jobs are grouped by the customer account's Lead Source in JobTread (set by the
    /// website form from the ad click id, or by hand), mapped to a channel by <see cref="ChannelFor"/>, next to spend and
    /// platform leads for that channel and new JobTread customer accounts in the period with that Lead Source
    /// (<paramref name="jtLeads"/>). Jobs whose customer has no Lead Source fall back to "How bid came in", in rows marked
    /// NoLeadSource. WonLeads counts new accounts from the leads period that have a sold job.
    /// </summary>
    public static List<LeadSource> LeadSources(IEnumerable<ProjectSummary> jobs, IEnumerable<SpendLine> spend, string googleChannel, JobTreadLeads? jtLeads = null)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var jobList = jobs.ToList();
        string KeyOf(ProjectSummary j) => j.CustomerLeadSource is { } ls ? ChannelFor(ls, googleChannel) : NoLeadSourceLabel(j.HowBidCameIn);

        var bySource = jobList.GroupBy(KeyOf, cmp)
            .ToDictionary(g => g.Key, g => (Jobs: g.Count(), Contract: g.Sum(j => j.Contract ?? 0)), cmp);
        var noSource = jobList.Where(j => j.CustomerLeadSource is null).Select(KeyOf).ToHashSet(cmp);
        var spendBy = spend.GroupBy(s => ChannelFor(s.Channel, googleChannel), cmp).ToDictionary(g => g.Key,
            g => (Amount: g.Sum(s => s.Amount), Leads: g.Any(s => s.Leads is not null) ? g.Sum(s => s.Leads ?? 0) : (decimal?)null, Api: g.Select(s => s.ApiSource).Where(a => a is not null).OrderBy(a => a == QuickBooksAdSpendService.SourceName).FirstOrDefault()),
            cmp);
        var leadsBy = (jtLeads?.BySource ?? new Dictionary<string, int>())
            .GroupBy(kv => ChannelFor(kv.Key, googleChannel), cmp).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), cmp);
        var wonBy = jtLeads is null ? new Dictionary<string, int>(cmp) : jobList
            .Where(j => j.CustomerLeadSource is not null && j.CustomerId is not null && j.CustomerCreatedOn >= jtLeads.From && j.CustomerCreatedOn <= jtLeads.To)
            .GroupBy(KeyOf, cmp).ToDictionary(g => g.Key, g => g.Select(j => j.CustomerId).Distinct().Count(), cmp);

        return bySource.Keys.Union(spendBy.Keys, cmp).Union(leadsBy.Keys, cmp)
            .Select(k =>
            {
                var sp = spendBy.GetValueOrDefault(k);
                var none = noSource.Contains(k);
                int? jt = jtLeads is null || none ? null : leadsBy.GetValueOrDefault(k);
                return new LeadSource(k, bySource.GetValueOrDefault(k).Jobs, bySource.GetValueOrDefault(k).Contract, sp.Amount, sp.Leads, sp.Api,
                    jt, wonBy.GetValueOrDefault(k), none);
            })
            .OrderBy(s => s.NoLeadSource).ThenByDescending(s => s.Contract).ThenByDescending(s => s.JtLeads ?? 0).ThenByDescending(s => s.Spend).ToList();
    }
}
