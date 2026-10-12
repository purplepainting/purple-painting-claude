using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Pages.Projects;

/// <summary>The cost-code panel shown when a job row is expanded. Returns an HTML fragment.</summary>
public partial class DetailModel(ProjectsService projects, ILogger<DetailModel> logger) : PageModel
{
    public ProjectDetail? Detail { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id, bool refresh, CancellationToken ct)
    {
        if (!JobId().IsMatch(id)) return NotFound();
        try
        {
            Detail = await projects.GetDetailAsync(id, refresh, ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading job {JobId} detail failed", id);
            Error = "Couldn't load cost codes from JobTread. Close and reopen to try again.";
        }
        return Page();
    }

    [GeneratedRegex("^[A-Za-z0-9]{6,20}$")]
    private static partial Regex JobId();
}
