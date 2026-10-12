using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Pages.Scheduling;

public partial class IndexModel(ScheduleService schedule, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"week" (default) or "day".</summary>
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }
    /// <summary>"job" (default) or "person".</summary>
    [BindProperty(SupportsGet = true)] public string? By { get; set; }
    /// <summary>Any date in the period to show (yyyy-MM-dd). Defaults to today.</summary>
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }

    public bool IsDay => Mode == "day";
    public bool ByPerson => By == "person";
    public DateOnly Today { get; } = LocalClock.Today;
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public IReadOnlyList<DateOnly> Days { get; private set; } = [];
    public IReadOnlyList<ScheduleTask> Tasks { get; private set; } = [];
    public IReadOnlyList<ScheduleRow> Rows { get; private set; } = [];
    public string? Error { get; private set; }
    public DateTime LoadedAt { get; private set; }

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        ViewData["Title"] = "Scheduling";
        ViewData["ModuleName"] = "Scheduling";

        var date = Date ?? Today;
        From = IsDay ? date : LocalClock.WeekStart(date);
        To = IsDay ? date : From.AddDays(6);

        try
        {
            Tasks = await schedule.GetTasksAsync(From, To, refresh, ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading schedule from JobTread failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return;
        }
        LoadedAt = LocalClock.Now;

        // Weekends only appear when something is scheduled on them.
        Days = Enumerable.Range(0, To.DayNumber - From.DayNumber + 1)
            .Select(From.AddDays)
            .Where(d => IsDay || d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) || Tasks.Any(t => t.Covers(d)))
            .ToList();

        Rows = ByPerson ? RowsByPerson() : RowsByJob();
    }

    private List<ScheduleRow> RowsByJob() => Tasks
        .GroupBy(t => t.JobId)
        .Select(g =>
        {
            var first = g.First();
            return new ScheduleRow(
                $"#{first.JobNumber} {first.JobName}", first.Address, JobColors.Css(first.JobType),
                Days.ToDictionary(d => d, d => (IReadOnlyList<ScheduleTask>)g.Where(t => t.Covers(d)).ToList()));
        })
        .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<ScheduleRow> RowsByPerson()
    {
        var people = Tasks.SelectMany(t => t.People).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = people.Select(p => new ScheduleRow(p, null, null,
            Days.ToDictionary(d => d, d => (IReadOnlyList<ScheduleTask>)Tasks.Where(t => t.Covers(d) && t.People.Contains(p)).ToList())))
            .ToList();
        var unassigned = Tasks.Where(t => t.People.Count == 0).ToList();
        if (unassigned.Count > 0)
            rows.Add(new ScheduleRow("Unassigned", null, null,
                Days.ToDictionary(d => d, d => (IReadOnlyList<ScheduleTask>)unassigned.Where(t => t.Covers(d)).ToList())));
        return rows;
    }

    public Dictionary<string, string?> Route(DateOnly? date = null, string? mode = null, string? by = null) => new()
    {
        ["date"] = (date ?? From) == LocalClock.WeekStart(Today) && (mode ?? Mode) != "day" ? null : (date ?? From).ToString("yyyy-MM-dd"),
        ["mode"] = (mode ?? Mode) == "day" ? "day" : null,
        ["by"] = (by ?? By) == "person" ? "person" : null,
    };

    public DateOnly Previous => IsDay ? From.AddDays(-1) : From.AddDays(-7);
    public DateOnly Next => IsDay ? From.AddDays(1) : From.AddDays(7);

    public string Heading => IsDay
        ? From.ToString("dddd, MMMM d")
        : From.Month == To.Month ? $"{From:MMM d} – {To.Day}" : $"{From:MMM d} – {To:MMM d}";

    /// <summary>Task names repeat the date ("Mon, 10.12 — Paint", "Day 6 (Mon 10/12) – Prep"); the grid already shows it.</summary>
    public static string ShortName(string name) => DatePrefix().Replace(name, "").Trim() is { Length: > 0 } s ? s : name;

    public static string FirstNames(IEnumerable<string> people) => string.Join(", ", people.Select(p => p.Split(' ')[0]));

    public static string Time(ScheduleTask t) => t.StartTime is { } s
        ? s.ToString("h:mm tt").Replace(":00", "") + (t.EndTime is { } e ? "–" + e.ToString("h:mm tt").Replace(":00", "") : "")
        : "";

    [GeneratedRegex(@"^(Day\s*\d+\s*\([^)]*\)|(Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s*[\d./]+)\s*[—–-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DatePrefix();
}

public sealed record ScheduleRow(string Title, string? Subtitle, string? ColorCss, IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduleTask>> Cells);
