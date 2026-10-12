using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Pages.Projects;

public class IndexModel(ProjectsService projects, IOptions<MarginBands> margin, ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Pm { get; set; }
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public ProjectView CurrentView { get; private set; } = ProjectViews.Default;
    public IReadOnlyList<ProjectSummary> Jobs { get; private set; } = [];
    public Dictionary<string, int> ViewCounts { get; } = [];
    public IReadOnlyList<string> ProjectManagers { get; private set; } = [];
    public IReadOnlyList<string> JobTypes { get; private set; } = [];
    public MarginBands Bands => margin.Value;
    public string? Error { get; private set; }
    public DateTime LoadedAt { get; private set; }

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        ViewData["Title"] = "Projects";
        ViewData["ModuleName"] = "Projects";
        CurrentView = ProjectViews.Find(View);

        IReadOnlyList<ProjectSummary> all;
        try
        {
            all = (await projects.GetOpenJobsAsync(refresh, ct)).Where(j => !j.IsInternal).ToList();
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading projects from JobTread failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return;
        }
        LoadedAt = LocalClock.Now;

        ProjectManagers = all.Select(j => j.ProjectManager).OfType<string>().Distinct().Order().ToList();
        JobTypes = all.Select(j => j.JobType).OfType<string>().Distinct().Order().ToList();

        var filtered = all.Where(MatchesFilters).ToList();
        foreach (var view in ProjectViews.All) ViewCounts[view.Key] = filtered.Count(view.Matches);

        Jobs = filtered
            .Where(CurrentView.Matches)
            .OrderBy(j => j.Start ?? DateOnly.MaxValue)
            .ThenByDescending(j => j.Number)
            .ToList();
    }

    public bool HasFilters => !string.IsNullOrEmpty(Pm) || !string.IsNullOrEmpty(Type) || !string.IsNullOrEmpty(Q);

    private bool MatchesFilters(ProjectSummary j)
    {
        if (!string.IsNullOrEmpty(Pm) && j.ProjectManager != Pm) return false;
        if (!string.IsNullOrEmpty(Type) && j.JobType != Type) return false;
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            return Contains(j.Name, q) || Contains(j.Number, q) || Contains(j.Customer, q) || Contains(j.Address, q);
        }
        return true;
    }

    private static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Query string for a view pill, keeping the current filters.</summary>
    public Dictionary<string, string?> RouteFor(string view) => new()
    {
        ["view"] = view == ProjectViews.Default.Key ? null : view,
        ["pm"] = Pm, ["type"] = Type, ["q"] = Q,
    };

    public static string StageCss(string? stage) => stage switch
    {
        "In Progress" => "active",
        "On Hold - Phasing" => "warn",
        "Completed" => "good",
        _ => "",
    };
}
