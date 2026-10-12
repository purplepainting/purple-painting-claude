using Microsoft.Extensions.Caching.Memory;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Modules.Financials;

/// <summary>QuickBooks totals for one customer/job column of the P&amp;L by customer.</summary>
public sealed record QbJobTotals(string Column, decimal Income, decimal Labor, decimal JobCosts)
{
    public decimal Profit => Income - Labor - JobCosts;
}

/// <summary>One job compared: JobTread (lifetime) vs QuickBooks (all dates, that job's column).</summary>
public sealed record JobRecon(ProjectSummary? Job, QbJobTotals? Qb, string Match)
{
    public decimal JtRevenue => Job?.InvoicedNet ?? 0;
    public decimal JtCosts => Job?.ActualCost ?? 0;
    public decimal JtProfit => JtRevenue - JtCosts;
    public decimal QbRevenue => Qb?.Income ?? 0;
    public decimal QbCosts => (Qb?.Labor ?? 0) + (Qb?.JobCosts ?? 0);
    public decimal QbProfit => QbRevenue - QbCosts;
    public decimal Gap => QbProfit - JtProfit;

    /// <summary>Profit differs by more than $1,000 and 10% of revenue.</summary>
    public bool Off => Job is not null && Qb is not null && Math.Abs(Gap) > Math.Max(1000, 0.1m * Math.Max(JtRevenue, QbRevenue));
}

public sealed class JobReconReport
{
    public required List<JobRecon> Rows { get; init; }
    /// <summary>QuickBooks amounts not assigned to any customer/job ("Not Specified").</summary>
    public QbJobTotals? Unassigned { get; init; }
    public DateTime LoadedAt { get; init; }
}

/// <summary>
/// Job-by-job JobTread vs QuickBooks. QuickBooks' P&amp;L summarized by customer (all dates) gives one column
/// per customer or customer:job; each column is matched to a JobTread job by job name or number, or by
/// customer when that customer has a single job. Labor counts field wages + burden (account rules).
/// </summary>
public sealed class JobReconciliationService(QuickBooksClient quickBooks, ProjectsService projects, AccountRules rules, IMemoryCache cache)
{
    public Task<JobReconReport> GetAsync(bool refresh, CancellationToken ct)
    {
        const string key = "financials:jobrecon";
        if (refresh) cache.Remove(key);
        return cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await LoadAsync(refresh, ct);
        })!;
    }

    private async Task<JobReconReport> LoadAsync(bool refresh, CancellationToken ct)
    {
        var reportTask = quickBooks.ReportAsync("ProfitAndLoss", new()
        {
            ["start_date"] = "2015-01-01",
            ["end_date"] = PurpleDashboard.Infrastructure.LocalClock.Today.ToString("yyyy-MM-dd"),
            ["summarize_column_by"] = "Customers",
            ["accounting_method"] = "Accrual",
        }, ct);
        var open = await projects.GetOpenJobsAsync(refresh, ct);
        var closed = await projects.GetClosedJobsAsync(PurpleDashboard.Infrastructure.LocalClock.Today.AddDays(-365), refresh, ct);
        var jobs = open.Concat(closed).Where(j => !j.IsInternal && j.Stage is not null).GroupBy(j => j.Id).Select(g => g.First()).ToList();
        var report = QbReport.Parse(await reportTask);

        // One totals record per column (the last column is the grand total; skip it).
        var columns = new List<QbJobTotals>();
        for (var i = 0; i < report.Columns.Count - 1; i++)
        {
            decimal income = 0, labor = 0, jobCosts = 0;
            foreach (var line in report.Lines)
            {
                var v = i < line.Amounts.Length ? line.Amounts[i] : 0;
                if (v == 0) continue;
                switch (rules.Classify(line).Kind)
                {
                    case AccountKind.Income: income += v; break;
                    case AccountKind.FieldWages or AccountKind.LaborBurden: labor += v; break;
                    case AccountKind.JobCost: jobCosts += v; break;
                }
            }
            if (income != 0 || labor != 0 || jobCosts != 0) columns.Add(new QbJobTotals(report.Columns[i], income, labor, jobCosts));
        }

        var unassigned = columns.FirstOrDefault(c => c.Column.Contains("Not Specified", StringComparison.OrdinalIgnoreCase) || c.Column.Length == 0);
        var rows = new List<JobRecon>();
        var matchedJobs = new HashSet<string>();
        foreach (var col in columns.Where(c => c != unassigned))
        {
            var (job, how) = Match(col.Column, jobs, matchedJobs);
            if (job is not null) matchedJobs.Add(job.Id);
            rows.Add(new JobRecon(job, col, how));
        }
        rows.AddRange(jobs.Where(j => !matchedJobs.Contains(j.Id) && (j.ActualCost > 0 || j.InvoicedNet > 0))
            .Select(j => new JobRecon(j, null, "Not in QuickBooks")));

        return new JobReconReport
        {
            Rows = rows.OrderByDescending(r => r.Off).ThenByDescending(r => Math.Abs(r.Gap)).ToList(),
            Unassigned = unassigned,
            LoadedAt = PurpleDashboard.Infrastructure.LocalClock.Now,
        };
    }

    /// <summary>"Customer:Job" or "Customer" → the JobTread job it most likely is.</summary>
    private static (ProjectSummary? Job, string How) Match(string column, List<ProjectSummary> jobs, HashSet<string> taken)
    {
        var parts = column.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var jobPart = parts.Length > 1 ? parts[^1] : null;
        var customerPart = parts.Length > 0 ? parts[0] : column;
        var free = jobs.Where(j => !taken.Contains(j.Id)).ToList();

        if (jobPart is not null)
        {
            var key = Key(jobPart);
            var byName = free.FirstOrDefault(j => Key(j.Name) == key) ?? free.FirstOrDefault(j => key.Contains(Key(j.Name)) || Key(j.Name).Contains(key));
            if (byName is not null) return (byName, "Job name");
            var byNumber = free.FirstOrDefault(j => !string.IsNullOrEmpty(j.Number) && jobPart.Split(' ', '#', '-').Contains(j.Number));
            if (byNumber is not null) return (byNumber, "Job number");
        }

        var forCustomer = free.Where(j => j.Customer is not null && Key(j.Customer) == Key(customerPart)).ToList();
        if (forCustomer.Count == 1) return (forCustomer[0], jobPart is null ? "Customer (one job)" : "Customer");
        // Customer-level column with no job: also try the column as a job name (some jobs are set up as customers).
        var asJob = free.FirstOrDefault(j => Key(j.Name) == Key(column));
        if (asJob is not null) return (asJob, "Job name");
        return (null, forCustomer.Count > 1 ? $"Customer has {forCustomer.Count} jobs; not split by job in QuickBooks" : "No matching JobTread job");
    }

    private static string Key(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
