namespace PurpleDashboard.Modules.Scheduling;

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
    public string? Address { get; init; }
    public bool IsInternal { get; init; }
    public IReadOnlyList<string> People { get; init; } = [];

    public bool Covers(DateOnly day) => Start <= day && day <= End;
}

/// <summary>The color group a job type is drawn in. Groups differ in lightness, not just hue.</summary>
public enum JobColor { Residential, Commercial, PublicWorks, Other }

public static class JobColors
{
    public static JobColor For(string? jobType) => jobType switch
    {
        "Residential - Standard Home" or "Residential - Custom House" => JobColor.Residential,
        "Commercial" or "Property Management / Production" => JobColor.Commercial,
        "Prevailing Wage" => JobColor.PublicWorks,
        _ => JobColor.Other,
    };

    public static string Css(string? jobType) => "jc-" + For(jobType).ToString().ToLowerInvariant();

    public static readonly (JobColor Color, string Label)[] Legend =
    [
        (JobColor.Residential, "Residential"),
        (JobColor.Commercial, "Commercial / property mgmt"),
        (JobColor.PublicWorks, "Public works"),
    ];
}
