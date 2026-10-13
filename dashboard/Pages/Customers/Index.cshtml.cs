using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Customers;

namespace PurpleDashboard.Pages.Customers;

public class IndexModel(CustomersService customers, ChangeLogWriter changeLog, DatabaseStatus database, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"all" (default), "owes", "review".</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<CustomerView> All { get; private set; } = [];
    public Dictionary<string, JobReview> Reviews { get; private set; } = [];
    public string? Error { get; private set; }
    public bool CanEdit => database.Available;
    [TempData] public string? Message { get; set; }

    public IEnumerable<CustomerView> Shown => All
        .Where(c => Show switch
        {
            "owes" => c.Owes >= 1,
            "review" => c.Jobs.Any(j => CustomersService.NeedsReview(j, Reviews)),
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
        }
        catch (Exception e) when (e is JobTreadException or QuickBooksException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading customers failed");
            Error = e is JobTreadException or QuickBooksException ? e.Message : "Couldn't reach JobTread or QuickBooks. Try again in a minute.";
        }
    }

    public async Task<IActionResult> OnPostReviewAsync(string jobId, string? jobName, string status, string? platform, int? rating, string? notes, CancellationToken ct)
    {
        if (!CanEdit || status is not ("requested" or "received" or "later" or "skip")) return RedirectToPage(new { show = Show, q = Q });
        var user = me.Email ?? me.DisplayName ?? "unknown";
        await customers.SaveReviewAsync(jobId, jobName, status, platform, rating, notes, user, ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "reviews", status, jobName, new { platform, rating, notes }, "Done")], ct);
        Message = $"Saved review status for {jobName}.";
        return RedirectToPage(new { show = Show, q = Q });
    }

    public static string ReviewLabel(JobReview? r) => r?.Status switch
    {
        "requested" => $"Requested {r.RequestedOn:MMM d}",
        "received" => $"Received{(r.Rating is { } s ? $" ★{s}" : "")}{(r.Platform is { } p ? " · " + p : "")}",
        "later" => "Ask later",
        "skip" => "Not asking",
        _ => "Not requested",
    };
}
