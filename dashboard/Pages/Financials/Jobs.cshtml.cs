using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Financials;

namespace PurpleDashboard.Pages.Financials;

/// <summary>Job by job: JobTread vs QuickBooks revenue, costs and profit, lifetime.</summary>
public class JobsModel(JobReconciliationService recon, QuickBooksClient quickBooks, ILogger<JobsModel> logger) : PageModel
{
    /// <summary>"off" (default: gaps and unmatched), "all".</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }

    public JobReconReport? Report { get; private set; }
    public bool Connected { get; private set; }
    public string? Error { get; private set; }

    public IEnumerable<JobRecon> Shown => Report is null ? [] : Show == "all"
        ? Report.Rows
        : Report.Rows.Where(r => r.Off || r.Job is null || r.Qb is null);

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.ClearCache();
        ViewData["Title"] = "Job by job";
        ViewData["ModuleName"] = "Financials";
        Connected = await quickBooks.GetConnectionAsync(ct) is not null;
        if (!Connected) return;
        try { Report = await recon.GetAsync(refresh, ct); }
        catch (Exception e) when (e is QuickBooksException or JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading job reconciliation failed");
            Error = e is QuickBooksException or JobTreadException ? e.Message : "Couldn't reach QuickBooks or JobTread. Try again in a minute.";
        }
    }
}
