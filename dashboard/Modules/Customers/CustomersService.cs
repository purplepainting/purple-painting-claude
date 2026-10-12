using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Modules.Customers;

/// <summary>A QuickBooks customer: what they owe and how to reach them.</summary>
public sealed record QbCustomer(string Id, string Name, decimal Balance, string? Email, string? Phone, string? Address);

/// <summary>A customer as the dashboard sees it: JobTread jobs plus the QuickBooks balance (matched by name).</summary>
public sealed record CustomerView(string Name, IReadOnlyList<ProjectSummary> Jobs, QbCustomer? Qb)
{
    public decimal Billed => Jobs.Sum(j => j.Billed);
    public decimal Contract => Jobs.Sum(j => j.Contract ?? 0);
    public decimal Owes => Qb?.Balance ?? Jobs.Sum(j => j.Unpaid);
    public DateOnly? LastActivity => Jobs.Select(j => j.LastTimeEntry ?? j.End).Where(d => d is not null).Max();
}

/// <summary>
/// Customers: JobTread jobs (open, plus closed in the last year) grouped by customer, QuickBooks balances
/// and contact info matched by name, and review status per finished job (dashboard database).
/// </summary>
public sealed class CustomersService(ProjectsService projects, QuickBooksClient quickBooks, IServiceProvider services, DatabaseStatus database, IMemoryCache cache)
{
    public static readonly string[] Platforms = ["Google", "Yelp", "Facebook", "Houzz", "Nextdoor", "Other"];

    public async Task<List<CustomerView>> GetAsync(bool refresh, CancellationToken ct)
    {
        var open = await projects.GetOpenJobsAsync(refresh, ct);
        var closed = await projects.GetClosedJobsAsync(LocalClock.Today.AddDays(-365), refresh, ct);
        var jobs = open.Concat(closed).Where(j => !j.IsInternal).GroupBy(j => j.Id).Select(g => g.First()).ToList();
        var qb = await QbCustomersAsync(refresh, ct);
        var qbByName = qb.GroupBy(c => Key(c.Name)).ToDictionary(g => g.Key, g => g.First());

        var views = jobs.GroupBy(j => string.IsNullOrWhiteSpace(j.Customer) ? "(no customer)" : j.Customer!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CustomerView(g.Key, g.OrderByDescending(j => j.Number).ToList(), qbByName.GetValueOrDefault(Key(g.Key))))
            .ToList();

        // QuickBooks customers who owe money but have no recent JobTread job still show.
        var shown = views.Where(v => v.Qb is not null).Select(v => v.Qb!.Id).ToHashSet();
        views.AddRange(qb.Where(c => c.Balance >= 1 && !shown.Contains(c.Id)).Select(c => new CustomerView(c.Name, [], c)));
        return views;
    }

    /// <summary>QuickBooks customers (empty if QuickBooks isn't connected or fails), cached 10 minutes.</summary>
    public async Task<List<QbCustomer>> QbCustomersAsync(bool refresh, CancellationToken ct)
    {
        const string key = "customers:qb";
        if (refresh) cache.Remove(key);
        if (cache.TryGetValue(key, out List<QbCustomer>? hit)) return hit!;
        if (await quickBooks.GetConnectionAsync(ct) is null) return [];
        var rows = await quickBooks.QueryAsync("Customer", "select * from Customer where Active = true", ct);
        var list = rows.Select(c => new QbCustomer(
            c["Id"]?.GetValue<string>() ?? "",
            c["DisplayName"]?.GetValue<string>() ?? c["FullyQualifiedName"]?.GetValue<string>() ?? "",
            Dec(c["Balance"]),
            c["PrimaryEmailAddr"]?["Address"]?.GetValue<string>(),
            c["PrimaryPhone"]?["FreeFormNumber"]?.GetValue<string>(),
            Address(c["BillAddr"]))).ToList();
        cache.Set(key, list, TimeSpan.FromMinutes(10));
        return list;
    }

    public async Task<Dictionary<string, JobReview>> ReviewsAsync(CancellationToken ct) =>
        database.Available ? await services.GetRequiredService<DashboardDb>().JobReviews.AsNoTracking().ToDictionaryAsync(r => r.JobId, ct) : [];

    public async Task SaveReviewAsync(string jobId, string? jobName, string status, string? platform, int? rating, string? notes, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var r = await db.JobReviews.FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (r is null) db.JobReviews.Add(r = new JobReview { JobId = jobId, Status = status });
        r.JobName = jobName;
        if (status == "requested" && r.Status != "requested") r.RequestedOn = LocalClock.Today;
        if (status == "received" && r.Status != "received") r.ReceivedOn = LocalClock.Today;
        r.Status = status;
        r.Platform = platform;
        r.Rating = rating is >= 1 and <= 5 ? rating : null;
        r.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        r.UpdatedAt = DateTime.UtcNow;
        r.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Completed jobs with no review requested yet (and not marked "later" or "skip").</summary>
    public static bool NeedsReview(ProjectSummary j, IReadOnlyDictionary<string, JobReview> reviews) =>
        j.IsCompleted && !reviews.ContainsKey(j.Id);

    private static string Key(string name) => new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static decimal Dec(JsonNode? n) => n is null ? 0 : n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.GetValue<decimal>() : decimal.TryParse(n.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string? Address(JsonNode? a)
    {
        if (a is null) return null;
        var parts = new[] { a["Line1"], a["City"], a["CountrySubDivisionCode"], a["PostalCode"] }.Select(p => p?.GetValue<string>()).Where(p => !string.IsNullOrWhiteSpace(p));
        var s = string.Join(", ", parts);
        return s.Length == 0 ? null : s;
    }
}

/// <summary>Customers tile: customers who owe money (QuickBooks balance, else unpaid JobTread invoices).</summary>
public sealed class CustomersOwingMetric(CustomersService customers) : ITileMetric
{
    public string ModuleKey => "customers";

    public async Task<string?> GetValueAsync(CancellationToken ct) => (await customers.GetAsync(false, ct)).Count(c => c.Owes >= 1).ToString();
}
