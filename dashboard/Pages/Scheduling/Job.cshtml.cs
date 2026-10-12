using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Pages.Scheduling;

/// <summary>A job's whole schedule as a timeline (Gantt): phases, every day/task, crew, done or not.</summary>
public partial class JobModel(ScheduleService schedule, TeamDirectory directory, ILogger<JobModel> logger) : PageModel
{
    public string JobId { get; private set; } = "";
    public string JobName { get; private set; } = "";
    public string? ProjectManager { get; private set; }
    public string? JobType { get; private set; }
    public IReadOnlyList<ScheduleTask> Tasks { get; private set; } = [];
    public Team Team { get; private set; } = null!;
    public string? Error { get; private set; }
    public DateOnly Start { get; private set; }
    public DateOnly End { get; private set; }
    public DateOnly Today { get; } = LocalClock.Today;
    public int Days => End.DayNumber - Start.DayNumber + 1;

    /// <summary>Phase (group task) → its tasks, in date order; tasks without a loaded phase go under "Tasks".</summary>
    public List<(ScheduleTask? Phase, List<ScheduleTask> Items)> Rows { get; } = [];

    public async Task<IActionResult> OnGetAsync(string id, bool refresh, CancellationToken ct)
    {
        if (!JobIdPattern().IsMatch(id)) return NotFound();
        JobId = id;
        Team = directory.Fallback();
        ViewData["ModuleName"] = "Scheduling";
        try
        {
            Tasks = await schedule.GetJobTimelineAsync(id, refresh, ct);
            Team = await directory.GetAsync(ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading job timeline failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            ViewData["Title"] = "Job timeline";
            return Page();
        }

        var first = Tasks.FirstOrDefault();
        JobName = first?.JobName ?? "Job";
        ProjectManager = first?.ProjectManager;
        JobType = IndexModel.ShortType(first?.JobType);
        ViewData["Title"] = JobName;
        if (Tasks.Count == 0) return Page();

        Start = Tasks.Min(t => t.Start).AddDays(-1);
        End = Tasks.Max(t => t.End).AddDays(1);
        if (End.DayNumber - Start.DayNumber < 13) End = Start.AddDays(13);

        var phases = Tasks.Where(t => t.IsGroup).OrderBy(t => t.Start).ToList();
        var phaseIds = phases.Select(p => p.Id).ToHashSet();
        foreach (var p in phases)
            Rows.Add((p, Tasks.Where(t => !t.IsGroup && t.ParentTaskId == p.Id).OrderBy(t => t.Start).ThenBy(t => t.StartTime).ToList()));
        var loose = Tasks.Where(t => !t.IsGroup && (t.ParentTaskId is null || !phaseIds.Contains(t.ParentTaskId))).OrderBy(t => t.Start).ToList();
        if (loose.Count > 0) Rows.Add((null, loose));
        return Page();
    }

    /// <summary>Inline style placing a bar for [start, end] on the timeline.</summary>
    public string Bar(DateOnly start, DateOnly end)
    {
        var left = 100.0 * (start.DayNumber - Start.DayNumber) / Days;
        var width = 100.0 * (end.DayNumber - start.DayNumber + 1) / Days;
        return $"left:{left:0.###}%;width:{Math.Max(width, 0.6):0.###}%";
    }

    /// <summary>Crew chips go right of the bar, or left of it when the bar is near the end of the timeline.</summary>
    public string CrewStyle(ScheduleTask t)
    {
        var endPct = 100.0 * (t.End.DayNumber - Start.DayNumber + 1) / Days;
        var startPct = 100.0 * (t.Start.DayNumber - Start.DayNumber) / Days;
        return endPct > 65 ? $"right:{100 - startPct:0.###}%;padding:0 6px 0 0" : $"left:{endPct:0.###}%";
    }

    public string At(DateOnly day) => $"left:{100.0 * (day.DayNumber - Start.DayNumber) / Days:0.###}%";

    /// <summary>Mondays across the range, for the week labels.</summary>
    public IEnumerable<DateOnly> Weeks()
    {
        for (var d = LocalClock.WeekStart(Start).AddDays(7); d <= End; d = d.AddDays(7)) yield return d;
    }

    public int DoneCount => Tasks.Count(t => !t.IsGroup && t.Completed);
    public int TaskCount => Tasks.Count(t => !t.IsGroup);
    public decimal CrewDays => Tasks.Where(t => !t.IsGroup).Sum(t => (decimal)t.Crew.Count * (t.End.DayNumber - t.Start.DayNumber + 1));

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex JobIdPattern();
}
