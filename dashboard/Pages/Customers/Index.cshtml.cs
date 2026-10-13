using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Customers;

namespace PurpleDashboard.Pages.Customers;

public class IndexModel(CustomersService customers, GoogleReviewsService googleReviews, ChangeLogWriter changeLog, DatabaseStatus database, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"all" (default), "owes", "review", "google" (the Google reviews panel).</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<CustomerView> All { get; private set; } = [];
    /// <summary>Review status per customer, by JobTread account id.</summary>
    public Dictionary<string, CustomerReview> Reviews { get; private set; } = [];
    public string? Error { get; private set; }
    public GoogleReviewsView Google { get; private set; } = GoogleReviewsView.Fail(null, "", "not-loaded");
    private IReadOnlyDictionary<string, ReviewMatch> GoogleByCustomer => Google.ByCustomer;
    public bool CanEdit => database.Available;
    [TempData] public string? Message { get; set; }

    public IEnumerable<CustomerView> Shown => All
        .Where(c => Show switch
        {
            "owes" => c.Owes >= 1,
            "review" => CustomersService.NeedsReview(c, Reviews, GoogleByCustomer),
            _ => true,
        })
        .Where(c => string.IsNullOrWhiteSpace(Q) || c.Name.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase)
            || c.Jobs.Any(j => j.Name.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) || (j.Address?.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)))
        .OrderByDescending(c => Show == "owes" ? c.Owes : 0)
        .ThenByDescending(c => c.LastActivity ?? DateOnly.MinValue)
        .ThenBy(c => c.Name);

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.ClearCache();
        ViewData["Title"] = "Customers";
        ViewData["ModuleName"] = "Customers";
        try
        {
            All = await customers.GetAsync(refresh, ct);
            Reviews = await customers.ReviewsAsync(ct);
            Google = await googleReviews.GetAsync(All, refresh, ct);
        }
        catch (Exception e) when (e is JobTreadException or QuickBooksException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading customers failed");
            Error = e is JobTreadException or QuickBooksException ? e.Message : "Couldn't reach JobTread or QuickBooks. Try again in a minute.";
        }
    }

    /// <summary>Saves the customer's review status (dashboard database only; JobTread's "Left Review" field isn't changed).</summary>
    public async Task<IActionResult> OnPostReviewAsync(string accountId, string? customerName, string status, string? platform, string? notes, CancellationToken ct)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(accountId) || status is not ("requested" or "later" or "skip")) return RedirectToPage(new { show = Show, q = Q });
        var user = me.Email ?? me.DisplayName ?? "unknown";
        await customers.SaveReviewAsync(accountId, customerName, status, platform, null, notes, user, ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "reviews", status, customerName, new { accountId, platform, notes }, "Done")], ct);
        Message = $"Saved review status for {customerName}.";
        return RedirectToPage(new { show = Show, q = Q });
    }

    /// <summary>
    /// The customer's review chip: JobTread's "Left Review" field first (the source of truth for a left review),
    /// then the dashboard's request status, then "Review to request" for customers with a completed job.
    /// Null when there's nothing to show.
    /// </summary>
    public (string Label, string Css, string? Title)? ReviewChip(CustomerView c)
    {
        var r = c.AccountId is { } id ? Reviews.GetValueOrDefault(id) : null;
        if (Google.For(c) is { } g)
            return (GoogleChip(g), "good", $"Google review by {g.Review.Reviewer}, {g.Review.Created:MMM d, yyyy}" + (g.How == "close" ? " (close name match)" : "")
                + (c.LeftReviewInJobTread ? "" : ". JobTread isn't marked \"Left a Review\" yet; update it there by hand."));
        if (c.LeftReviewInJobTread) return ("Left a review (JobTread)", "good", "JobTread customer field \"Left Review\"");
        if (r is not null) return (ReviewLabel(r), r.Status == "received" ? "good" : "", r.UpdatedBy is { } by ? $"Updated {r.UpdatedAt:MMM d} by {by}" : null);
        if (CustomersService.NeedsReview(c, Reviews, GoogleByCustomer)) return ("Review to request", "warn", null);
        return null;
    }

    public bool NeedsReview(CustomerView c) => CustomersService.NeedsReview(c, Reviews, GoogleByCustomer);

    public static string GoogleChip(ReviewMatch m) => m.Review.Stars > 0 ? $"Google review ★{m.Review.Stars}" : "Google review";

    public static string Stars(int n) => n <= 0 ? "" : new string('★', n) + new string('☆', 5 - n);

    public static string ReviewLabel(CustomerReview? r) => r?.Status switch
    {
        "requested" => $"Requested {r.RequestedOn:MMM d}{(r.Platform is { } asked ? " · " + asked : "")}",
        "received" => $"Received{(r.Rating is { } s ? $" ★{s}" : "")}{(r.Platform is { } p ? " · " + p : "")}",
        "later" => "Ask later",
        "skip" => "Not asking",
        _ => "Not requested",
    };
}
