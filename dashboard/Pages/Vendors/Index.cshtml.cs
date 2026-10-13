using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Vendors;

namespace PurpleDashboard.Pages.Vendors;

public class IndexModel(PayablesService payables, QuickBooksClient quickBooks, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"due" (next 14 days, default), "overdue", "all", "vendors".</summary>
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public bool Connected { get; private set; }
    public string? Error { get; private set; }
    public List<OpenBill> Bills { get; private set; } = [];
    public DateOnly Today { get; } = LocalClock.Today;
    public string CurrentView => View is "overdue" or "all" or "vendors" ? View : "due";

    public IEnumerable<OpenBill> Overdue => Bills.Where(b => b.DaysUntilDue(Today) < 0);
    public IEnumerable<OpenBill> DueSoon => Bills.Where(b => b.DaysUntilDue(Today) is >= 0 and <= 14);

    public IEnumerable<OpenBill> Shown => (CurrentView switch
    {
        "overdue" => Overdue,
        "all" => Bills,
        _ => Bills.Where(b => b.DaysUntilDue(Today) <= 14),
    }).Where(b => string.IsNullOrWhiteSpace(Q) || b.Vendor.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) || (b.DocNumber?.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));

    public IEnumerable<(string Vendor, int Bills, decimal Owed, decimal Overdue, DateOnly? NextDue)> ByVendor => Bills
        .Where(b => string.IsNullOrWhiteSpace(Q) || b.Vendor.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase))
        .GroupBy(b => b.Vendor)
        .Select(g => (g.Key, g.Count(), g.Sum(b => b.Balance), g.Where(b => b.DaysUntilDue(Today) < 0).Sum(b => b.Balance), g.Min(b => b.Due)))
        .OrderByDescending(v => v.Item3);

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.ClearCache();
        ViewData["Title"] = "Vendors & Payables";
        ViewData["ModuleName"] = "Vendors & Payables";
        Connected = await quickBooks.GetConnectionAsync(ct) is not null;
        if (!Connected) return;
        try { Bills = await payables.OpenBillsAsync(refresh, ct); }
        catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading bills failed");
            Error = e is QuickBooksException ? e.Message : "Couldn't reach QuickBooks. Try again in a minute.";
        }
    }

    public string DueLabel(OpenBill b) => b.DaysUntilDue(Today) switch
    {
        null => "No due date",
        < 0 and var d => $"{-d} days overdue",
        0 => "Due today",
        1 => "Due tomorrow",
        var d => $"Due in {d} days",
    };
}
