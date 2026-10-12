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

public sealed record LeadSource(string Source, int Jobs, decimal Contract, decimal Spend);

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
    /// Jobs sold by "How bid came in" (JobTread) next to ad spend for channels with the same name. Which ad
    /// produced which lead isn't tracked yet, so this is a side-by-side, not attribution.
    /// </summary>
    public static List<LeadSource> LeadSources(IEnumerable<ProjectSummary> jobs, IEnumerable<AdSpend> spend)
    {
        var bySource = jobs.GroupBy(j => string.IsNullOrWhiteSpace(j.HowBidCameIn) ? "Not set" : j.HowBidCameIn!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Jobs: g.Count(), Contract: g.Sum(j => j.Contract ?? 0)), StringComparer.OrdinalIgnoreCase);
        var spendBy = spend.GroupBy(s => s.Channel.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount), StringComparer.OrdinalIgnoreCase);
        return bySource.Keys.Union(spendBy.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => new LeadSource(k, bySource.GetValueOrDefault(k).Jobs, bySource.GetValueOrDefault(k).Contract, spendBy.GetValueOrDefault(k)))
            .OrderByDescending(s => s.Contract).ToList();
    }
}
