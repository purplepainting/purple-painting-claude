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

/// <summary>
/// A customer as the dashboard sees it: JobTread jobs plus the QuickBooks balance (matched by name).
/// AccountId is the JobTread customer account (null for QuickBooks-only customers).
/// </summary>
public sealed record CustomerView(string Name, IReadOnlyList<ProjectSummary> Jobs, QbCustomer? Qb, string? AccountId = null)
{
    public bool HasCompletedJob => Jobs.Any(j => j.IsCompleted);
    /// <summary>JobTread's "Left Review" customer field says they left one.</summary>
    public bool LeftReviewInJobTread => Jobs.Any(j => j.CustomerLeftReview == true);
    public decimal Billed => Jobs.Sum(j => j.Billed);
    public decimal Contract => Jobs.Sum(j => j.Contract ?? 0);
    public decimal Owes => Qb?.Balance ?? Jobs.Sum(j => j.Unpaid);
    public DateOnly? LastActivity => Jobs.Select(j => j.LastTimeEntry ?? j.End).Where(d => d is not null).Max();
}

/// <summary>
/// Customers: JobTread jobs (open, plus closed in the last year) grouped by customer, QuickBooks balances
/// and contact info matched by name, and review status per customer (dashboard database; one review per customer, however many jobs).
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

        // Grouped by JobTread customer account (by name when a job has no account id).
        var views = jobs.GroupBy(j => j.CustomerId ?? "name:" + (string.IsNullOrWhiteSpace(j.Customer) ? "(no customer)" : j.Customer!.Trim().ToLowerInvariant()))
            .Select(g =>
            {
                var name = g.Select(j => j.Customer?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "(no customer)";
                return new CustomerView(name, g.OrderByDescending(j => j.Number).ToList(), qbByName.GetValueOrDefault(Key(name)), g.First().CustomerId);
            })
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

    /// <summary>QuickBooks customers if they're already cached, else null. Never calls QuickBooks (used by search).</summary>
    public List<QbCustomer>? CachedQbCustomers() => cache.TryGetValue("customers:qb", out List<QbCustomer>? hit) ? hit : null;

    /// <summary>Review status per customer, by JobTread account id.</summary>
    public async Task<Dictionary<string, CustomerReview>> ReviewsAsync(CancellationToken ct) =>
        database.Available ? await services.GetRequiredService<DashboardDb>().CustomerReviews.AsNoTracking().ToDictionaryAsync(r => r.AccountId, ct) : [];

    /// <summary>Saves the customer's review status in the dashboard database only (nothing is written to JobTread).</summary>
    public async Task SaveReviewAsync(string accountId, string? customerName, string status, string? platform, int? rating, string? notes, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var r = await db.CustomerReviews.FirstOrDefaultAsync(x => x.AccountId == accountId, ct);
        if (r is null) db.CustomerReviews.Add(r = new CustomerReview { AccountId = accountId, Status = status });
        r.CustomerName = customerName;
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

    /// <summary>
    /// A customer needs a review request when they have at least one completed job, JobTread doesn't mark them
    /// "Left a Review", and there's no review status for them in the dashboard yet (any status, including "later" or "skip").
    /// </summary>
    public static bool NeedsReview(CustomerView c, IReadOnlyDictionary<string, CustomerReview> reviews) =>
        c.AccountId is not null && c.HasCompletedJob && !c.LeftReviewInJobTread && !reviews.ContainsKey(c.AccountId);

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
