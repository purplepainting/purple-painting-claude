using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Modules.Financials;

/// <summary>One month (or the whole period) side by side: JobTread vs QuickBooks.</summary>
public sealed class FinancialPeriod
{
    public required string Label { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    // JobTread
    public decimal JtInvoiced { get; set; }      // customer invoices issued (net of retention)
    public decimal JtLaborCost { get; set; }     // time entries, at JobTread's burdened rates
    public decimal JtHours { get; set; }
    public decimal JtBills { get; set; }         // vendor bills on jobs
    // Internal jobs (Office Work, Time Off, Shop Labor...): office pay and paid time off are overhead, which
    // JobTread's burdened rates already absorb, so they're left out of JobTread's job profit.
    public decimal JtInternalLabor { get; set; }
    public decimal JtInternalHours { get; set; }
    public decimal JtInternalBills { get; set; }

    // QuickBooks
    public decimal QbIncome { get; set; }
    public decimal QbFieldWages { get; set; }
    public decimal QbBurden { get; set; }
    public decimal QbJobCosts { get; set; }      // materials, jobsite services, other job costs
    public decimal QbOverhead { get; set; }
    /// <summary>Bonus payroll (part of overhead). Bonuses are paid out of profit, so they're not in the burdened rates.</summary>
    public decimal QbBonuses { get; set; }

    public decimal QbLabor => QbFieldWages + QbBurden;
    public decimal QbGrossProfit => QbIncome - QbLabor - QbJobCosts;
    public decimal QbNet => QbGrossProfit - QbOverhead;
    public decimal JtGrossProfit => JtInvoiced - JtLaborCost - JtBills;

    // The goal: JobTread's burdened rates carry wages, payroll taxes, comp, insurance and overhead, so profit on
    // JobTread's jobs matches QuickBooks net profit before bonuses. What's left is timing (billing, bills) or rates.
    public decimal JtJobHours => JtHours - JtInternalHours;
    public decimal JtJobLabor => JtLaborCost - JtInternalLabor;
    /// <summary>Invoices − bills − time at burdened rates, on real jobs (internal jobs left out).</summary>
    public decimal JtJobProfit => JtInvoiced - JtJobLabor - (JtBills - JtInternalBills);
    public decimal QbNetBeforeBonuses => QbNet + QbBonuses;
    /// <summary>JobTread job profit − QuickBooks net before bonuses: positive = JobTread shows more profit than there was.</summary>
    public decimal NetDelta => JtJobProfit - QbNetBeforeBonuses;
    /// <summary>Everything the burdened rates have to cover, per JobTread hour on jobs: field wages + burden + overhead (not bonuses).</summary>
    public decimal? QbAllInPerHour => JtJobHours > 0 ? (QbLabor + QbOverhead - QbBonuses) / JtJobHours : null;
    /// <summary>JobTread's average burdened rate on job hours.</summary>
    public decimal? JtJobRate => JtJobHours > 0 ? JtJobLabor / JtJobHours : null;

    /// <summary>Real field labor cost per JobTread clocked hour (QuickBooks wages + burden ÷ JobTread hours).</summary>
    public decimal? QbCostPerHour => JtHours > 0 ? QbLabor / JtHours : null;
    /// <summary>JobTread's average burdened rate (time entry cost ÷ hours).</summary>
    public decimal? JtCostPerHour => JtHours > 0 ? JtLaborCost / JtHours : null;
}

public sealed record CategoryTotal(string Category, decimal Total, decimal[] ByMonth, IReadOnlyList<(string Account, decimal Total)> Accounts);

public sealed record Flag(string Severity, string Title, string Detail);

public sealed class FinancialsReport
{
    public required IReadOnlyList<FinancialPeriod> Months { get; init; }
    public required FinancialPeriod Total { get; init; }
    public required IReadOnlyList<CategoryTotal> FieldWages { get; init; }
    public required IReadOnlyList<CategoryTotal> Burden { get; init; }
    public required IReadOnlyList<CategoryTotal> JobCosts { get; init; }
    public required IReadOnlyList<CategoryTotal> Overhead { get; init; }
    public required IReadOnlyList<Flag> Flags { get; init; }
    public DateTime LoadedAt { get; init; }
}

/// <summary>
/// Financials: JobTread vs QuickBooks month by month, the real labor burden, and overhead.
/// QuickBooks is the source of truth; JobTread is what jobs are estimated and tracked with.
/// Job bills are entered in JobTread and pushed to QuickBooks, so QuickBooks job costs should match
/// JobTread bills; differences are listed as flags to clean up.
/// </summary>
public sealed class FinancialsService(QuickBooksClient quickBooks, IJobTreadClient jobTread, AccountRules rules, IMemoryCache cache, IOptions<JobTreadOptions> jtOptions,
    PurpleDashboard.Modules.Projects.ProjectsService projects)
{
    public Task<FinancialsReport> GetAsync(DateOnly from, DateOnly to, string basis, bool refresh, CancellationToken ct)
    {
        var key = $"financials:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{basis}";
        if (refresh) cache.Remove(key);
        return cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            entry.AddExpirationToken(PurpleDashboard.Infrastructure.CacheSignals.Token(PurpleDashboard.Infrastructure.CacheSignals.Financials));
            return await LoadAsync(from, to, basis, ct);
        })!;
    }

    private async Task<FinancialsReport> LoadAsync(DateOnly from, DateOnly to, string basis, CancellationToken ct)
    {
        var months = MonthsBetween(from, to);

        var pnlTask = quickBooks.ReportAsync("ProfitAndLoss", new()
        {
            ["start_date"] = from.ToString("yyyy-MM-dd"),
            ["end_date"] = to.ToString("yyyy-MM-dd"),
            ["summarize_column_by"] = "Month",
            ["accounting_method"] = basis,
        }, ct);
        var jtTask = JobTreadByMonthAsync(months, ct);
        var pnl = QbReport.Parse(await pnlTask);
        var jt = await jtTask;

        // Map report columns to our months by title ("Aug 2026"); QuickBooks returns one column per month, then Total.
        var monthCount = months.Count;
        var columnOf = months.Select(m => pnl.Columns.ToList().FindIndex(c =>
            DateTime.TryParseExact(c, "MMM yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            && d.Year == m.From.Year && d.Month == m.From.Month)).ToArray();
        decimal Month(QbLine l, int i) =>
            monthCount <= 1 ? l.Total
            : columnOf[i] >= 0 && columnOf[i] < l.Amounts.Length ? l.Amounts[columnOf[i]]
            : i < l.Amounts.Length - 1 ? l.Amounts[i] : 0;

        var classified = pnl.Lines.Select(l => (Line: l, Class: rules.Classify(l))).ToList();
        var periods = months.Select((m, i) =>
        {
            var p = jt[i];
            foreach (var (line, c) in classified)
            {
                var v = Month(line, i);
                switch (c.Kind)
                {
                    case AccountKind.Income: p.QbIncome += v; break;
                    case AccountKind.FieldWages: p.QbFieldWages += v; break;
                    case AccountKind.LaborBurden: p.QbBurden += v; break;
                    case AccountKind.JobCost: p.QbJobCosts += v; break;
                    case AccountKind.Overhead: p.QbOverhead += v; if (c.Category == "Bonuses") p.QbBonuses += v; break;
                }
            }
            return p;
        }).ToList();

        var total = new FinancialPeriod
        {
            Label = "Total", From = from, To = to,
            JtInvoiced = periods.Sum(p => p.JtInvoiced), JtLaborCost = periods.Sum(p => p.JtLaborCost),
            JtHours = periods.Sum(p => p.JtHours), JtBills = periods.Sum(p => p.JtBills),
            QbIncome = periods.Sum(p => p.QbIncome), QbFieldWages = periods.Sum(p => p.QbFieldWages),
            QbBurden = periods.Sum(p => p.QbBurden), QbJobCosts = periods.Sum(p => p.QbJobCosts), QbOverhead = periods.Sum(p => p.QbOverhead),
            QbBonuses = periods.Sum(p => p.QbBonuses), JtInternalLabor = periods.Sum(p => p.JtInternalLabor),
            JtInternalHours = periods.Sum(p => p.JtInternalHours), JtInternalBills = periods.Sum(p => p.JtInternalBills),
        };

        List<CategoryTotal> Categories(AccountKind kind) => classified
            .Where(x => x.Class.Kind == kind)
            .GroupBy(x => x.Class.Category)
            .Select(g => new CategoryTotal(
                g.Key,
                g.Sum(x => x.Line.Total),
                Enumerable.Range(0, monthCount).Select(i => g.Sum(x => Month(x.Line, i))).ToArray(),
                g.GroupBy(x => x.Line.Name).Select(a => (a.Key, a.Sum(x => x.Line.Total))).Where(a => a.Item2 != 0).OrderByDescending(a => Math.Abs(a.Item2)).ToList()))
            .Where(c => c.Total != 0)
            .OrderByDescending(c => c.Total)
            .ToList();

        return new FinancialsReport
        {
            Months = periods,
            Total = total,
            FieldWages = Categories(AccountKind.FieldWages),
            Burden = Categories(AccountKind.LaborBurden),
            JobCosts = Categories(AccountKind.JobCost),
            Overhead = Categories(AccountKind.Overhead),
            Flags = FindFlags(periods, classified),
            LoadedAt = LocalClock.Now,
        };
    }

    /// <summary>Things that look off between JobTread and QuickBooks, or in how QuickBooks is categorized.</summary>
    private static List<Flag> FindFlags(List<FinancialPeriod> months, List<(QbLine Line, AccountClass Class)> lines)
    {
        var flags = new List<Flag>();
        foreach (var m in months)
        {
            var gap = m.QbJobCosts - m.JtBills;
            if (Math.Abs(gap) > Math.Max(1000, 0.1m * Math.Max(m.QbJobCosts, m.JtBills)))
                flags.Add(new("warn", $"{m.Label}: job costs don't match",
                    $"QuickBooks materials/jobsite {Format.Money(m.QbJobCosts)} vs JobTread bills {Format.Money(m.JtBills)} ({(gap > 0 ? "QuickBooks higher" : "JobTread higher")} by {Format.Money(Math.Abs(gap))}). Job bills should go through JobTread; look for bills entered straight in QuickBooks or not pushed yet."));

            var revGap = m.QbIncome - m.JtInvoiced;
            if (m.JtInvoiced > 0 && Math.Abs(revGap) > Math.Max(2000, 0.1m * m.QbIncome))
                flags.Add(new("info", $"{m.Label}: revenue differs",
                    $"QuickBooks income {Format.Money(m.QbIncome)} vs JobTread invoices {Format.Money(m.JtInvoiced)}. JobTread invoices are net of retention; QuickBooks may also include invoices made outside JobTread."));

            if (m.JtHours == 0 && m.QbFieldWages > 0)
                flags.Add(new("info", $"{m.Label}: no JobTread hours", $"QuickBooks shows {Format.Money(m.QbFieldWages)} field wages but nobody clocked in on JobTread, so per-hour numbers skip this month."));
        }

        var fieldInExpenses = lines.Where(x => x.Class.Kind == AccountKind.FieldWages && x.Line.Section == "Expenses").Sum(x => x.Line.Total);
        if (fieldInExpenses != 0)
            flags.Add(new("warn", "Field payroll booked under Expenses",
                $"{Format.Money(fieldInExpenses)} of field payroll sits under Expenses instead of Cost of Goods Sold. It's counted as field labor here; fix the payroll item mapping in QuickBooks."));

        var wc = lines.Where(x => x.Class.Kind == AccountKind.LaborBurden && x.Line.Section == "Expenses").Sum(x => x.Line.Total);
        if (wc != 0)
            flags.Add(new("info", "Labor burden under Expenses",
                $"{Format.Money(wc)} of labor burden (e.g. workers' comp) is under Expenses. It's counted as field labor burden here, not overhead."));

        if (!lines.Any(x => x.Line.Name.Contains("tax", StringComparison.OrdinalIgnoreCase) && x.Class.Kind is AccountKind.LaborBurden or AccountKind.Overhead && x.Line.Name.Contains("payroll", StringComparison.OrdinalIgnoreCase)))
            flags.Add(new("info", "No employer payroll tax account",
                "QuickBooks has no expense account for employer payroll taxes, so they're probably inside the wage accounts. Wages then include taxes, and burden here only shows workers' comp and sick/holiday."));

        var uncategorized = lines.Where(x => x.Line.Name.Contains("Uncategorized", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Line.Total);
        if (uncategorized != 0) flags.Add(new("warn", "Uncategorized amounts", $"{Format.Money(uncategorized)} is uncategorized in QuickBooks."));

        var deleted = lines.Where(x => x.Line.Name.Contains("(deleted)", StringComparison.OrdinalIgnoreCase) || x.Line.Path.Any(p => p.Contains("(deleted)", StringComparison.OrdinalIgnoreCase))).Sum(x => x.Line.Total);
        if (deleted != 0) flags.Add(new("warn", "Deleted accounts with activity", $"{Format.Money(deleted)} is posted to accounts that were deleted in QuickBooks."));

        var unnumbered = lines.Where(x => x.Line.Number is null && x.Line.Total != 0 && x.Class.Kind != AccountKind.Income).ToList();
        if (unnumbered.Count > 0)
            flags.Add(new("info", "Accounts without numbers",
                $"{string.Join(", ", unnumbered.Select(x => $"{x.Line.Name} ({Format.Money(x.Line.Total)})"))}. Sorted by report section; give them account numbers in QuickBooks."));
        return flags;
    }

    /// <summary>JobTread invoices, bills and time per month, all months in one request (aggregates only).</summary>
    private async Task<List<FinancialPeriod>> JobTreadByMonthAsync(List<(DateOnly From, DateOnly To)> months, CancellationToken ct)
    {
        var open = await projects.GetOpenJobsAsync(false, ct);
        var closed = await projects.GetClosedJobsAsync(months[0].From, false, ct);
        var internalIds = open.Concat(closed).Where(j => j.IsInternal).Select(j => j.Id).Distinct().ToList();
        JsonArray OnInternal() => new(new JsonArray("job", "id"), "in", new JsonArray(internalIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));
        var org = new JsonObject { ["$"] = new JsonObject { ["id"] = jtOptions.Value.OrganizationId } };
        for (var i = 0; i < months.Count; i++)
        {
            var (f, t) = months[i];
            var dates = new JsonArray(new JsonArray("issueDate", ">=", f.ToString("yyyy-MM-dd")), new JsonArray("issueDate", "<=", t.ToString("yyyy-MM-dd")));
            org[$"inv{i}"] = new JsonObject
            {
                ["_"] = "documents",
                ["$"] = new JsonObject { ["where"] = new JsonObject { ["and"] = new JsonArray(new JsonArray("type", "customerInvoice"), new JsonArray("status", "in", new JsonArray("approved", "pending")), dates[0]!.DeepClone(), dates[1]!.DeepClone()) } },
                ["sum"] = new JsonObject { ["$"] = "price" },
            };
            org[$"bill{i}"] = new JsonObject
            {
                ["_"] = "documents",
                ["$"] = new JsonObject { ["where"] = new JsonObject { ["and"] = new JsonArray(new JsonArray("type", "vendorBill"), new JsonArray("status", "in", new JsonArray("approved", "pending")), dates[0]!.DeepClone(), dates[1]!.DeepClone()) } },
                ["sum"] = new JsonObject { ["$"] = "cost" },
            };
            org[$"time{i}"] = new JsonObject
            {
                ["_"] = "timeEntries",
                ["$"] = new JsonObject { ["where"] = new JsonObject { ["and"] = new JsonArray(
                    new JsonArray("startedAt", ">=", LocalClock.StartOfDayUtc(f).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                    new JsonArray("startedAt", "<", LocalClock.StartOfDayUtc(t.AddDays(1)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))) } },
                ["cost"] = new JsonObject { ["_"] = "sum", ["$"] = "cost" },
                ["minutes"] = new JsonObject { ["_"] = "sum", ["$"] = "minutes" },
            };
            if (internalIds.Count == 0) continue;
            var timeAnd = (JsonArray)org[$"time{i}"]!["$"]!["where"]!["and"]!.DeepClone();
            timeAnd.Add(OnInternal());
            org[$"itime{i}"] = new JsonObject
            {
                ["_"] = "timeEntries",
                ["$"] = new JsonObject { ["where"] = new JsonObject { ["and"] = timeAnd } },
                ["cost"] = new JsonObject { ["_"] = "sum", ["$"] = "cost" },
                ["minutes"] = new JsonObject { ["_"] = "sum", ["$"] = "minutes" },
            };
            var billAnd = (JsonArray)org[$"bill{i}"]!["$"]!["where"]!["and"]!.DeepClone();
            billAnd.Add(OnInternal());
            org[$"ibill{i}"] = new JsonObject
            {
                ["_"] = "documents",
                ["$"] = new JsonObject { ["where"] = new JsonObject { ["and"] = billAnd } },
                ["sum"] = new JsonObject { ["$"] = "cost" },
            };
        }
        var result = (await jobTread.QueryAsync("financials-months", new JsonObject { ["organization"] = org }, ct))["organization"];
        return months.Select((m, i) => new FinancialPeriod
        {
            Label = m.From.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            From = m.From, To = m.To,
            JtInvoiced = result?[$"inv{i}"].Dec("sum") ?? 0,
            JtBills = result?[$"bill{i}"].Dec("sum") ?? 0,
            JtLaborCost = result?[$"time{i}"].Dec("cost") ?? 0,
            JtHours = Math.Round((result?[$"time{i}"].Dec("minutes") ?? 0) / 60m, 1),
            JtInternalLabor = result?[$"itime{i}"].Dec("cost") ?? 0,
            JtInternalHours = Math.Round((result?[$"itime{i}"].Dec("minutes") ?? 0) / 60m, 1),
            JtInternalBills = result?[$"ibill{i}"].Dec("sum") ?? 0,
        }).ToList();
    }

    /// <summary>Calendar months overlapping [from, to], clipped to the range.</summary>
    public static List<(DateOnly From, DateOnly To)> MonthsBetween(DateOnly from, DateOnly to)
    {
        var list = new List<(DateOnly, DateOnly)>();
        for (var start = new DateOnly(from.Year, from.Month, 1); start <= to; start = start.AddMonths(1))
        {
            var end = start.AddMonths(1).AddDays(-1);
            list.Add((start < from ? from : start, end > to ? to : end));
        }
        return list;
    }
}
