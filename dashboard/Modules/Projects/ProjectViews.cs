using PurpleDashboard.Infrastructure;

namespace PurpleDashboard.Modules.Projects;

/// <summary>
/// The saved-view pills on the Projects screen. Preset for now; per-user saved views
/// come with the dashboard database. Add or edit a view here.
/// </summary>
public sealed record ProjectView(string Key, string Label, Func<ProjectSummary, bool> Matches);

public static class ProjectViews
{
    public static readonly string[] ActiveStages =
        ["Pre-Construction / Color Submittals", "Scheduled", "In Progress", "On Hold - Phasing"];

    private static bool IsActive(ProjectSummary p) => p.Stage is not null && ActiveStages.Contains(p.Stage);

    public static readonly IReadOnlyList<ProjectView> All =
    [
        new("active",      "All active",          IsActive),
        new("inprogress",  "In progress",         p => p.Stage == "In Progress"),
        new("starting",    "Starting this week",  p => IsActive(p) && p.Start is { } s && s >= WeekStart() && s < WeekStart().AddDays(7)),
        new("costsover",   "Costs over billed",   p => (IsActive(p) || p.Stage == "Completed") && p.CostsOverBilled),
        new("lefttobill",  "Left to bill",        p => (IsActive(p) || p.Stage == "Completed") && p.LeftToBill > 0),
        new("publicworks", "Public works",        p => IsActive(p) && p.JobType == "Prevailing Wage"),
        new("closeout",    "Completed, not closed", p => p.Stage == "Completed"),
        new("bids",        "Bids",                p => p.Stage is null),
    ];

    public static ProjectView Default => All[0];

    public static ProjectView Find(string? key) => All.FirstOrDefault(v => v.Key == key) ?? Default;

    private static DateOnly WeekStart() => LocalClock.WeekStart(LocalClock.Today);
}
