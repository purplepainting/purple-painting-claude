using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Modules.Financials;

/// <summary>Old per-job approval line (LinesJson on BonusPayout).</summary>
public sealed record BonusLine(string Name, string Role, decimal Hours, decimal Amount);

/// <summary>
/// One person's line on one job in one month: their share of the job-month's pool (negative for a losing month)
/// and any clawback charged to them. Net = what it adds to their year.
/// </summary>
public sealed class BonusEntry
{
    public required string JobId { get; init; }
    public required DateOnly Month { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public decimal Hours { get; set; }
    public decimal Amount { get; set; }
    public decimal Clawback { get; set; }
    public bool Capped { get; set; }
    public decimal Net => Amount - Clawback;
}

/// <summary>A job's month: JobTread money, the pools (negative when the month lost money) and how they were split.</summary>
public sealed class JobMonthBonus
{
    public DateOnly Month { get; init; }
    public decimal Income { get; init; }
    public decimal Costs { get; init; }
    public decimal Profit => Income - Costs;
    public decimal SupervisorPool { get; set; }
    public decimal FieldPool { get; set; }
    public decimal SupervisorClawback { get; set; }
    public decimal FieldClawback { get; set; }
    public decimal Clawbacks => SupervisorClawback + FieldClawback;
    public List<BonusEntry> People { get; } = [];
    /// <summary>Shares of people who aren't eligible this month, kept by the company (ForfeitMode "company").</summary>
    public List<(string Name, string Role, decimal Amount, string Why)> Forfeited { get; } = [];
    /// <summary>Part of the field pool nobody gets because of the hours-share cap.</summary>
    public decimal CapKept { get; set; }
    public decimal KeptByCompany => Forfeited.Sum(f => f.Amount) + CapKept;
    /// <summary>People who clocked time but aren't eligible this month, with why (probation, left, excluded).</summary>
    public List<(string Name, string Why)> Excluded { get; } = [];
    public List<string> Notes { get; } = [];
}

public sealed class JobBonusView
{
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public string? Number { get; init; }
    public string? ProjectManager { get; init; }
    public string? Stage { get; init; }
    public DateOnly? ClosedOn { get; init; }
    public List<JobMonthBonus> Months { get; } = [];
    public List<BonusClawback> Clawbacks { get; } = [];
    /// <summary>Pool money waiting for someone to clock time on the job (negative: a loss or clawback waiting for someone to charge).</summary>
    public decimal UnassignedSupervisor { get; set; }
    public decimal UnassignedField { get; set; }
    public IEnumerable<BonusEntry> Entries => Months.SelectMany(m => m.People);
    public decimal Profit => Months.Sum(m => m.Profit);
    public decimal Net => Entries.Sum(p => p.Net);
    public decimal ClawedBack => Months.Sum(m => m.Clawbacks);
    public DateOnly? LastMonth => Months.Count == 0 ? null : Months.Max(m => m.Month);
    public IEnumerable<(string Name, string Role)> People => Entries
        .GroupBy(p => (p.Name, p.Role)).OrderBy(g => g.Key.Role == BonusPools.Supervisor ? 0 : 1).ThenByDescending(g => g.Sum(p => p.Net)).Select(g => g.Key);
    public JobMonthBonus? In(DateOnly month) => Months.FirstOrDefault(m => m.Month == month);
}

public sealed record PersonJobLine(JobBonusView Job, decimal Hours, decimal Amount, decimal Clawback, bool Capped)
{
    public decimal Net => Amount - Clawback;
}

/// <summary>
/// Someone's bonus year as of the end of a month. YTD net = every job-month line this year (plus and minus) and
/// adjustments; Opening = last year's year-end settlement (paid with January) or a negative balance carried over.
/// Owed = max(0, Opening + YTD net − retention held − YTD paid): losses just lower what's paid later, nobody owes cash back.
/// </summary>
public sealed record PersonReconcile(
    string Name, string Role, int Year, decimal Opening,
    decimal YtdEarned, decimal YtdLosses, decimal YtdAdjustments, decimal MonthEarned, decimal MonthLosses, decimal MonthAdjustments,
    decimal Held, decimal ReleasedYtd, decimal YtdPaid, decimal PaidThisMonth,
    IReadOnlyList<PersonJobLine> Jobs, IReadOnlyList<PersonJobLine> JobsYtd, IReadOnlyList<BonusPayment> Payments, IReadOnlyList<BonusAdjustment> Adjustments)
{
    public decimal YtdNet => YtdEarned + YtdLosses + YtdAdjustments;
    public decimal MonthNet => MonthEarned + MonthLosses + MonthAdjustments;
    public decimal Balance => Opening + YtdNet - Held - YtdPaid;
    public decimal Owed => Math.Max(0, Balance);
    /// <summary>Losses (or overpayment) still to make up from future bonuses.</summary>
    public decimal ToMakeUp => Math.Max(0, -Balance);
    public bool IsSettlement => Opening > 0;
}

/// <summary>One month of a person's year (Employees page and the person's ledger).</summary>
public sealed record PersonMonth(DateOnly Month, decimal Earned, decimal Losses, decimal Adjustments, decimal RunningNet, decimal Held, decimal Paid, decimal Owed, decimal Opening)
{
    public decimal Net => Earned + Losses + Adjustments;
}

/// <summary>A person's effort for a month: every rater's score and the plain average.</summary>
public sealed record EffortScore(IReadOnlyList<EffortRating> Ratings)
{
    public decimal? Average => Ratings.Count == 0 ? null : Math.Round(Ratings.Average(r => r.Score), 2);
    public decimal Spread => Ratings.Count < 2 ? 0 : Ratings.Max(r => r.Score) - Ratings.Min(r => r.Score);
}

/// <summary>
/// Who can earn a bonus in a month. Not eligible: on probation at the end of the month (hire date + probation days,
/// QuickBooks), left (QuickBooks release date on or before the month's end, or inactive with no release date),
/// or excluded by hand for that month (write-up, more than 2 weeks off, ...). No hire date = eligible.
/// </summary>
public sealed class Eligibility(IReadOnlyDictionary<string, QbEmployee> byName, IReadOnlyList<BonusExclusion> exclusions, int probationDays, IReadOnlyCollection<QbEmployee>? allQb = null)
{
    private readonly Dictionary<string, QbEmployee?> matched = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<BonusExclusion> Exclusions => exclusions;
    public bool HasQuickBooks => byName.Count > 0 || allQb?.Count > 0;

    /// <summary>
    /// The person's QuickBooks record: by the QuickBooks id on their JobTread membership, else by name (same name, or
    /// every word of the JobTread name in exactly one QuickBooks name: "Edem Mejia" → "Edem Mejia Leon").
    /// Without this, someone whose JobTread profile isn't linked has no hire or release date and counts as eligible.
    /// </summary>
    public QbEmployee? Qb(string name)
    {
        if (byName.TryGetValue(name, out var linked)) return linked;
        if (matched.TryGetValue(name, out var known)) return known;
        return matched[name] = MatchByName(name);
    }

    private QbEmployee? MatchByName(string name)
    {
        if (allQb is null || allQb.Count == 0) return null;
        var key = PayrollPayments.Normalize(name);
        var withNames = allQb.Where(q => !string.IsNullOrWhiteSpace(q.Name)).Select(q => (Q: q, Key: PayrollPayments.Normalize(q.Name!))).ToList();
        var same = withNames.Where(x => x.Key == key).ToList();
        if (same.Count == 1) return same[0].Q;
        var words = key.Split(' ');
        if (words.Length < 2) return null;
        var fits = withNames.Where(x => words.All(w => x.Key.Split(' ').Contains(w))).ToList();
        return fits.Count == 1 ? fits[0].Q : null;
    }
    public DateOnly? ProbationUntil(string name) => Qb(name)?.HiredDate is { } h ? h.AddDays(probationDays) : null;
    public bool HireDateKnown(string name) => Qb(name)?.HiredDate is not null;
    public BonusExclusion? Manual(string name, DateOnly month) =>
        exclusions.FirstOrDefault(e => e.Month == BonusService.Key(month) && e.Person.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why someone gets no bonus this month, or null when they're eligible.</summary>
    public string? Why(string name, DateOnly month)
    {
        var end = month.AddMonths(1).AddDays(-1);
        if (Manual(name, month) is { } x) return "Excluded: " + x.Reason;
        if (Qb(name) is { } q)
        {
            if (q.ReleasedDate is { } r && r <= end) return $"Left {Format.Date(r)}";
            if (!q.Active && q.ReleasedDate is null) return "Left (inactive in QuickBooks)";
            if (q.HiredDate is { } h && end < h.AddDays(probationDays)) return $"Probation until {Format.Date(h.AddDays(probationDays))}";
        }
        return null;
    }
}

/// <summary>Everything worked out: jobs by month, everyone's lines, payments, eligibility, ratings; per-person years.</summary>
public sealed class BonusLedger
{
    public required BonusOptions Rules { get; init; }
    public required IReadOnlyList<DateOnly> Months { get; init; }
    public required IReadOnlyList<JobBonusView> Jobs { get; init; }
    public required IReadOnlyList<BonusEntry> Entries { get; init; }
    public required IReadOnlyList<BonusPayment> Payments { get; init; }
    public required IReadOnlyList<BonusAdjustment> Adjustments { get; init; }
    public required IReadOnlyList<BonusRetentionRelease> Releases { get; init; }
    public IReadOnlyList<PayLock> Locks { get; init; } = [];
    public PayLock? LockFor(string name, DateOnly month) => Locks.FirstOrDefault(l => l.Month == BonusService.Key(month) && Same(l.Person, name));
    public bool AnyLocked(DateOnly month) => Locks.Any(l => l.Month == BonusService.Key(month));
    public required IReadOnlyDictionary<string, string> Roles { get; init; }
    public required Eligibility Eligibility { get; init; }
    /// <summary>Old per-job approvals that were approved but never marked paid (history only).</summary>
    public IReadOnlyList<BonusPayout> UnpaidApprovals { get; init; } = [];
    public IReadOnlyList<EffortRating> Ratings { get; init; } = [];
    /// <summary>Field staff (JobTread) still with the company, for effort ratings.</summary>
    public IReadOnlyList<StaffMember> FieldStaff { get; init; } = [];
    /// <summary>Last spreadsheet round's average rating by normalized name (pre-fill for the first month).</summary>
    public IReadOnlyDictionary<string, decimal> LastRound { get; init; } = new Dictionary<string, decimal>();
    /// <summary>Jobs left out of bonuses entirely (Jobs tab).</summary>
    public IReadOnlyList<BonusJobExclusion> LeftOutJobs { get; init; } = [];
    public DateOnly StartMonth => Months[0];

    public string RoleOf(string name) => Roles.TryGetValue(name, out var r) ? r : BonusPools.Field;
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // ---- Effort ----

    public EffortScore EffortFor(StaffMember s, DateOnly month) => new(Ratings.Where(r => r.Month == BonusService.Key(month) && r.MembershipId == s.MembershipId).ToList());
    /// <summary>Field staff who need an effort rating for the month: still employed, not on probation, not excluded.</summary>
    public IEnumerable<StaffMember> ToRate(DateOnly month) => FieldStaff.Where(s => Eligibility.Why(s.Name, month) is null);
    public List<StaffMember> NotRated(DateOnly month) => ToRate(month).Where(s => EffortFor(s, month).Ratings.Count == 0).ToList();

    /// <summary>Pre-fill suggestion for a rating: the latest earlier month's average, or the last spreadsheet round.</summary>
    public (decimal Score, string Source)? Suggestion(StaffMember s, DateOnly month)
    {
        for (var m = month.AddMonths(-1); m >= StartMonth; m = m.AddMonths(-1))
            if (EffortFor(s, m).Average is { } avg) return (avg, BonusService.Label(m) + " average");
        var key = PayrollPayments.Normalize(s.Name);
        if (LastRound.TryGetValue(key, out var last)) return (last, "last round");
        // Spreadsheet names can be longer ("Adrian Ramirez Hernandez" for "Adrian Ramirez"): every word of the JobTread name, only one fits.
        var words = key.Split(' ');
        var fits = LastRound.Where(r => words.Length >= 2 && words.All(w => r.Key.Split(' ').Contains(w))).ToList();
        return fits.Count == 1 ? (fits[0].Value, "last round") : null;
    }

    // ---- People's years ----

    /// <summary>Everyone with anything this year up to the month (lines, payments, adjustments, an opening balance).</summary>
    public List<PersonReconcile> Reconcile(DateOnly month)
    {
        var yearStart = new DateOnly(month.Year, 1, 1);
        var next = month.AddMonths(1);
        var names = Entries.Where(e => e.Month < next).Select(e => e.Name)
            .Concat(Payments.Where(p => p.PaidOn < next).Select(p => p.Person))
            .Concat(Adjustments.Select(a => a.Person))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return names.Select(n => Status(n, month))
            .Where(r => r.Opening != 0 || r.YtdNet != 0 || r.YtdPaid != 0 || r.Held != 0 || r.Jobs.Count > 0)
            .OrderBy(r => r.Role == BonusPools.Supervisor ? 0 : 1).ThenByDescending(r => r.Owed).ThenBy(r => r.Name).ToList();
    }

    /// <summary>A person's year as of the end of <paramref name="month"/>.</summary>
    public PersonReconcile Status(string name, DateOnly month)
    {
        var year = month.Year;
        var opening = OpeningFor(name, year);
        var (held, released) = HeldThrough(name, month);
        var key = BonusService.Key(month);
        var mine = Entries.Where(e => Same(e.Name, name) && e.Month.Year == year && e.Month <= month).ToList();
        var adj = Adjustments.Where(a => Same(a.Person, name) && a.Month.StartsWith(year + "-") && string.CompareOrdinal(a.Month, key) <= 0).ToList();
        var paid = Payments.Where(p => Same(p.Person, name) && p.PaidOn.Year == year && p.PaidOn < month.AddMonths(1)).ToList();
        var jobs = Jobs.ToDictionary(j => j.JobId);
        List<PersonJobLine> Lines(IEnumerable<BonusEntry> es) => es.GroupBy(e => e.JobId)
            .Select(g => new PersonJobLine(jobs[g.Key], g.Sum(e => e.Hours), g.Sum(e => e.Amount), g.Sum(e => e.Clawback), g.Any(e => e.Capped)))
            .OrderByDescending(l => l.Net).ToList();
        var now = mine.Where(e => e.Month == month).ToList();
        return new PersonReconcile(name, RoleOf(name), year, opening,
            mine.Where(e => e.Net > 0).Sum(e => e.Net), mine.Where(e => e.Net < 0).Sum(e => e.Net), adj.Sum(a => a.Amount),
            now.Where(e => e.Net > 0).Sum(e => e.Net), now.Where(e => e.Net < 0).Sum(e => e.Net), adj.Where(a => a.Month == key).Sum(a => a.Amount),
            held, released, paid.Sum(p => p.Amount), paid.Where(p => p.PaidOn >= month).Sum(p => p.Amount),
            Lines(now), Lines(mine), paid.Where(p => p.PaidOn >= month).OrderBy(p => p.PaidOn).ThenBy(p => p.Id).ToList(), adj);
    }

    /// <summary>Each month of the person's year that's been counted so far (for the Employees page).</summary>
    public List<PersonMonth> Year(string name, int year)
    {
        var result = new List<PersonMonth>();
        foreach (var m in Months.Where(m => m.Year == year))
        {
            var s = Status(name, m);
            result.Add(new PersonMonth(m, s.MonthEarned, s.MonthLosses, s.MonthAdjustments, s.YtdNet, s.Held, s.PaidThisMonth, s.Owed, s.Opening));
        }
        return result;
    }

    /// <summary>
    /// Last year's year-end balance with retention released: positive = the year-end settlement, paid with January;
    /// negative = carried into this year when NegativeCarryOver is on (otherwise 0).
    /// </summary>
    public decimal OpeningFor(string name, int year)
    {
        if (year <= StartMonth.Year) return 0;
        var prior = year - 1;
        var open = OpeningFor(name, prior);
        var net = Entries.Where(e => Same(e.Name, name) && e.Month.Year == prior).Sum(e => e.Net)
            + Adjustments.Where(a => Same(a.Person, name) && a.Month.StartsWith(prior + "-")).Sum(a => a.Amount);
        var paid = Payments.Where(p => Same(p.Person, name) && p.PaidOn.Year == prior).Sum(p => p.Amount);
        var end = open + net - paid;
        return end >= 0 ? Math.Round(end, 2) : Rules.NegativeCarryOver ? Math.Round(end, 2) : 0;
    }

    /// <summary>
    /// Retention held at the end of the month, and how much was released early by hand this year. Year-end rule:
    /// RetentionPercent of the year's net so far (never below 0), released in the January settlement. Job-close rule:
    /// RetentionPercent of their net on each job still open at the month's end.
    /// </summary>
    public (decimal Held, decimal Released) HeldThrough(string name, DateOnly month)
    {
        var released = 0m;
        decimal Base(DateOnly m)
        {
            var mine = Entries.Where(e => Same(e.Name, name) && e.Month.Year == m.Year && e.Month <= m);
            if (Rules.RetentionRelease == "job-close")
            {
                var end = m.AddMonths(1).AddDays(-1);
                return mine.GroupBy(e => e.JobId)
                    .Where(g => Jobs.FirstOrDefault(j => j.JobId == g.Key)?.ClosedOn is not { } c || c > end)
                    .Sum(g => Math.Max(0, g.Sum(e => e.Net))) * Rules.RetentionPercent / 100m;
            }
            var net = mine.Sum(e => e.Net) + Adjustments.Where(a => Same(a.Person, name) && a.Month.StartsWith(m.Year + "-") && string.CompareOrdinal(a.Month, BonusService.Key(m)) <= 0).Sum(a => a.Amount);
            return Math.Max(0, net) * Rules.RetentionPercent / 100m;
        }
        for (var m = new DateOnly(month.Year, 1, 1) < StartMonth ? StartMonth : new DateOnly(month.Year, 1, 1); m <= month; m = m.AddMonths(1))
        {
            var key = BonusService.Key(m);
            if (Releases.Any(r => r.Month == key && (r.Person is null || Same(r.Person, name))))
                released += Math.Max(0, Base(m) - released);
        }
        return (Math.Round(Math.Max(0, Base(month) - released), 2), Math.Round(released, 2));
    }
}
