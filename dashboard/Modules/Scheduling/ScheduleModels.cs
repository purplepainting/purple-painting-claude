namespace PurpleDashboard.Modules.Scheduling;

public sealed record CrewAssignment(string MembershipId, string Name);

/// <summary>One scheduled JobTread task on a job (usually one day of work with the painters assigned).</summary>
public sealed record ScheduleTask
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? EndTime { get; init; }
    public bool Completed { get; init; }
    /// <summary>A phase that groups other tasks (only loaded for the job timeline).</summary>
    public bool IsGroup { get; init; }
    public string? TaskType { get; init; }
    public required string JobId { get; init; }
    public required string JobName { get; init; }
    public string? JobNumber { get; init; }
    public string? JobType { get; init; }
    public string? ProjectManager { get; init; }
    public string? Address { get; init; }
    public bool IsInternal { get; init; }
    public IReadOnlyList<CrewAssignment> Crew { get; init; } = [];
    public IReadOnlyList<string> People => Crew.Select(c => c.Name).ToList();
    public string? ParentTaskId { get; init; }
    public string? TaskTypeId { get; init; }
    public int Dependents { get; init; }

    public bool Covers(DateOnly day) => Start <= day && day <= End;
}

/// <summary>
/// Job colors for one screen: every job shown gets a different pastel. Hues are spaced evenly
/// around the color wheel (greens left out) for the number of jobs; with more than 4 jobs, every other job gets a
/// mid tone instead of a pastel so neighbors still stand apart. Jobs are ordered by id, so the same set of jobs
/// gets the same colors. Jobs added later ("+ Add job" in the editor) fill in between.
/// </summary>
public sealed class JobColors
{
    private const double Start = 268;   // first job is purple
    // No greens (hues 70-170): crew chips are green, so green jobs would hide them.
    // Jobs use the arc from 170 (teal-blue) round through purple, red and orange to 70 (yellow).
    private const double ArcStart = 170, Arc = 260;
    private readonly Dictionary<string, (int Hue, bool Deep)> colors = [];
    private readonly int count;

    public JobColors(IEnumerable<string> jobIds)
    {
        var ids = jobIds.Distinct().Order(StringComparer.Ordinal).ToList();
        count = Math.Max(ids.Count, 1);
        foreach (var id in ids) Add(id);
    }

    public int HueFor(string jobId) => Get(jobId).Hue;
    public bool IsDeep(string jobId) => Get(jobId).Deep;

    /// <summary>Inline style for .block, .swatch etc.: the hue, plus a mid tone for every other job.</summary>
    public string Style(string jobId) => Get(jobId) is var c && c.Deep ? $"--h:{c.Hue};--l:80%" : $"--h:{c.Hue}";

    private (int Hue, bool Deep) Get(string jobId) => colors.TryGetValue(jobId, out var c) ? c : Add(jobId);

    private (int Hue, bool Deep) Add(string jobId)
    {
        var i = colors.Count;
        // Within the first `count` jobs: even spacing. Extra jobs land halfway between existing hues.
        var step = Arc / count;
        var along = i < count ? i * step : ((i - count) * 2 + 1) * step / 2;
        var hue = ArcStart + (Start - ArcStart + along) % Arc;
        return colors[jobId] = ((int)Math.Round(hue % 360), count > 4 && i % 2 == 1);
    }
}

/// <summary>Crew level, for balanced-crew colors. From the Crew:Foremen / Crew:Helpers settings; everyone else is a painter.</summary>
public enum CrewLevel { Foreman, Painter, Helper }

public sealed class CrewLevelOptions
{
    public List<string> Foremen { get; set; } = [];
    public List<string> Helpers { get; set; } = [];

    public CrewLevel For(string name) =>
        Foremen.Contains(name, StringComparer.OrdinalIgnoreCase) ? CrewLevel.Foreman
        : Helpers.Contains(name, StringComparer.OrdinalIgnoreCase) ? CrewLevel.Helper
        : CrewLevel.Painter;
}
