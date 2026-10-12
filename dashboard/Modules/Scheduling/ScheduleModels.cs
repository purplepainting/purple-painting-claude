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
/// Each job gets its own color (a hue), picked from a set of well-separated hues by the job id,
/// so a job is always the same color everywhere.
/// </summary>
public static class JobColors
{
    private static readonly int[] Hues = [268, 200, 28, 145, 340, 48, 175, 300, 12, 225, 95, 320];

    public static int HueFor(string jobId)
    {
        var hash = 0;
        foreach (var c in jobId) hash = unchecked(hash * 31 + c);
        return Hues[(int)((uint)hash % (uint)Hues.Length)];
    }

    /// <summary>Inline style that sets the job's hue for .block, .swatch etc.</summary>
    public static string Style(string jobId) => $"--h:{HueFor(jobId)}";
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
