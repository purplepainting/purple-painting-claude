using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PurpleDashboard.Auth;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Projects;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Pages.Scheduling;

public partial class IndexModel(
    ScheduleService schedule,
    LaborCostService laborCosts,
    SchedulePushService pushService,
    PeopleService people,
    ProjectsService projects,
    IOptions<CrewLevelOptions> crewLevels,
    IOptions<PmColorOptions> pmColors,
    CurrentUser me,
    ILogger<IndexModel> logger) : PageModel
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

    /// <summary>Labor cost per job per day (actual up to today, estimated after). Empty if costs couldn't load.</summary>
    public Dictionary<(string JobId, DateOnly Day), DayCost> Costs { get; } = [];

    /// <summary>Who actually clocked in per job per day (past days and today), with hours.</summary>
    public Dictionary<(string JobId, DateOnly Day), List<(string Name, decimal Hours)>> Actual { get; } = [];

    /// <summary>Past day (or today) with no time entries for a job that was scheduled: nobody clocked in.</summary>
    public bool IsPastOrToday(DateOnly day) => day <= Today;

    /// <summary>Did this scheduled person clock in on this job that day?</summary>
    public bool ClockedIn(string jobId, DateOnly day, string name) =>
        Actual.TryGetValue((jobId, day), out var list) && list.Any(x => x.Name == name);

    public (decimal Actual, decimal Estimate) WeekCost(string jobId) =>
        (Costs.Where(c => c.Key.JobId == jobId && !c.Value.IsEstimate).Sum(c => c.Value.Amount),
         Costs.Where(c => c.Key.JobId == jobId && c.Value.IsEstimate).Sum(c => c.Value.Amount));

    /// <summary>JSON for the schedule editor (week view only).</summary>
    public string? EditorJson { get; private set; }

    private Dictionary<string, StaffMember> staffByName = [];
    private Dictionary<string, StaffMember> staffByUserId = [];

    public PmColorOptions PmColors => pmColors.Value;

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        ViewData["Title"] = "Scheduling";
        ViewData["ModuleName"] = "Scheduling";

        var date = Date ?? Today;
        From = IsDay ? date : LocalClock.WeekStart(date);
        To = IsDay ? date : From.AddDays(6);

        IReadOnlyList<StaffMember> staff;
        try
        {
            Tasks = await schedule.GetTasksAsync(From, To, refresh, ct);
            staff = await people.GetStaffAsync(ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading schedule from JobTread failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return;
        }
        LoadedAt = LocalClock.Now;
        staffByName = staff.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First());
        staffByUserId = staff.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.First());

        // Weekends only appear when something is scheduled on them.
        Days = Enumerable.Range(0, To.DayNumber - From.DayNumber + 1)
            .Select(From.AddDays)
            .Where(d => IsDay || d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) || Tasks.Any(t => t.Covers(d)))
            .ToList();

        Rows = ByPerson ? RowsByPerson() : RowsByJob();
        await LoadCostsAsync(ct);

        if (!IsDay) EditorJson = await BuildEditorJsonAsync(staff, ct);
    }

    /// <summary>Push the editor's draft to JobTread. Body: { changes: [...] }.</summary>
    public async Task<IActionResult> OnPostPushAsync([FromBody] PushRequest request, CancellationToken ct)
    {
        if (request?.Changes is not { Count: > 0 }) return BadRequest(new { error = "No changes to push." });
        if (request.Changes.Count > 100) return BadRequest(new { error = "Too many changes at once. Push in smaller batches." });

        var results = await pushService.PushAsync(request.Changes, me.Email ?? me.DisplayName ?? "unknown", ct);
        return new JsonResult(new { results });
    }

    /// <summary>Labor cost per person per day (by-person view): actual up to today, estimated after.</summary>
    public Dictionary<(string Name, DateOnly Day), DayCost> PersonCosts { get; } = [];

    public (decimal Actual, decimal Estimate) PersonWeekCost(string name) =>
        (PersonCosts.Where(c => c.Key.Name == name && !c.Value.IsEstimate).Sum(c => c.Value.Amount),
         PersonCosts.Where(c => c.Key.Name == name && c.Value.IsEstimate).Sum(c => c.Value.Amount));

    private async Task LoadPersonCostsAsync(CancellationToken ct)
    {
        var actual = await laborCosts.GetActualByPersonAsync(From, To, ct);
        foreach (var name in Tasks.SelectMany(t => t.People).Distinct())
        {
            if (!staffByName.TryGetValue(name, out var st)) continue;
            foreach (var day in Days)
            {
                if (day <= Today)
                {
                    if (actual.TryGetValue((st.UserId, day), out var a) && a.Cost > 0) PersonCosts[(name, day)] = new DayCost(a.Cost, false);
                }
                else
                {
                    decimal est = 0;
                    foreach (var t in Tasks.Where(t => t.Covers(day) && t.Crew.Any(c => c.MembershipId == st.MembershipId)))
                        est += await laborCosts.EstimatePersonAsync(t, st.MembershipId, ct) ?? 0;
                    if (est > 0) PersonCosts[(name, day)] = new DayCost(est, true);
                }
            }
        }
    }

    private async Task LoadCostsAsync(CancellationToken ct)
    {
        try
        {
            if (ByPerson) { await LoadPersonCostsAsync(ct); return; }
            var actual = await laborCosts.GetActualAsync(From, To, ct);
            foreach (var jobId in Tasks.Select(t => t.JobId).Distinct())
            {
                foreach (var day in Days)
                {
                    if (day <= Today)
                    {
                        if (actual.TryGetValue((jobId, day), out var a))
                        {
                            if (a.Cost > 0) Costs[(jobId, day)] = new DayCost(a.Cost, false);
                            Actual[(jobId, day)] = a.MinutesByUser
                                .Select(m => (Name: staffByUserId.TryGetValue(m.Key, out var st) ? st.Name : "Unknown", Hours: Math.Round(m.Value / 60m, 1)))
                                .OrderByDescending(x => x.Hours)
                                .ToList();
                        }
                    }
                    else
                    {
                        decimal est = 0;
                        foreach (var t in Tasks.Where(t => t.JobId == jobId && t.Covers(day)))
                            est += await laborCosts.EstimateAsync(t, ct) ?? 0;
                        if (est > 0) Costs[(jobId, day)] = new DayCost(est, true);
                    }
                }
            }
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading labor costs failed; schedule shows without costs");
        }
    }

    private async Task<string> BuildEditorJsonAsync(IReadOnlyList<StaffMember> staff, CancellationToken ct)
    {
        var jobsOnSchedule = Tasks.GroupBy(t => t.JobId).Select(g => g.First()).ToList();

        // Active jobs not on this week's schedule, for "+ Add job". Failing to load them only hides the button.
        var addable = new List<object>();
        try
        {
            var onSchedule = jobsOnSchedule.Select(j => j.JobId).ToHashSet();
            addable = (await projects.GetOpenJobsAsync(ct: ct))
                .Where(j => !j.IsInternal && ProjectViews.Default.Matches(j) && !onSchedule.Contains(j.Id))
                .OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
                .Select(j => (object)new { id = j.Id, name = j.Name, type = j.JobTypeShort, pm = j.ProjectManager, pmColor = PmColors.For(j.ProjectManager), hue = JobColors.HueFor(j.Id) })
                .ToList();
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading jobs for Add job failed");
        }

        var data = new
        {
            weekStart = From.ToString("yyyy-MM-dd"),
            today = Today.ToString("yyyy-MM-dd"),
            days = Enumerable.Range(0, 7).Select(i => From.AddDays(i).ToString("yyyy-MM-dd")),
            jobs = jobsOnSchedule.OrderBy(j => j.JobName, StringComparer.OrdinalIgnoreCase).Select(j => new
            {
                id = j.JobId, name = j.JobName, type = ShortType(j.JobType), pm = j.ProjectManager, pmColor = PmColors.For(j.ProjectManager), hue = JobColors.HueFor(j.JobId),
                costs = Days.Where(d => Costs.ContainsKey((j.JobId, d))).ToDictionary(d => d.ToString("yyyy-MM-dd"), d => new { amount = Math.Round(Costs[(j.JobId, d)].Amount), estimate = Costs[(j.JobId, d)].IsEstimate }),
            }),
            addableJobs = addable,
            tasks = Tasks.Select(t => new
            {
                id = t.Id, jobId = t.JobId, name = t.Name,
                start = t.Start.ToString("yyyy-MM-dd"), end = t.End.ToString("yyyy-MM-dd"),
                startTime = t.StartTime?.ToString("HH:mm"), endTime = t.EndTime?.ToString("HH:mm"),
                crew = t.Crew.Select(c => c.MembershipId),
                parentTaskId = t.ParentTaskId, taskTypeId = t.TaskTypeId, dependents = t.Dependents,
            }),
            people = staff.Select(s => new { id = s.MembershipId, name = s.Name, first = s.FirstName, level = LevelOf(s) }),
        };
        return JsonSerializer.Serialize(data);
    }

    /// <summary>"foreman", "painter", "helper", or "supervisor" for non-field staff.</summary>
    public string LevelOf(StaffMember s) => !s.IsField ? "supervisor" : crewLevels.Value.For(s.Name).ToString().ToLowerInvariant();

    public string LevelOf(string name) => staffByName.TryGetValue(name, out var s) ? LevelOf(s) : "painter";

    /// <summary>"1F · 2P · 1H": the crew balance on a task.</summary>
    public string Balance(ScheduleTask t)
    {
        var levels = t.People.Select(LevelOf).Where(l => l != "supervisor").ToList();
        if (levels.Count == 0) return "";
        var parts = new List<string>();
        void Add(string level, string letter) { var n = levels.Count(l => l == level); if (n > 0) parts.Add($"{n}{letter}"); }
        Add("foreman", "F"); Add("painter", "P"); Add("helper", "H");
        return string.Join(" · ", parts);
    }

    private List<ScheduleRow> RowsByJob() => Tasks
        .GroupBy(t => t.JobId)
        .Select(g =>
        {
            var first = g.First();
            return new ScheduleRow(first.JobName, ShortType(first.JobType), JobColors.Style(first.JobId), first.ProjectManager, first.JobId,
                Days.ToDictionary(d => d, d => (IReadOnlyList<ScheduleTask>)g.Where(t => t.Covers(d)).ToList()));
        })
        .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<ScheduleRow> RowsByPerson()
    {
        var names = Tasks.SelectMany(t => t.People).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = names.Select(p => new ScheduleRow(p, null, null, null, null,
            Days.ToDictionary(d => d, d => (IReadOnlyList<ScheduleTask>)Tasks.Where(t => t.Covers(d) && t.People.Contains(p)).ToList())))
            .ToList();
        var unassigned = Tasks.Where(t => t.Crew.Count == 0).ToList();
        if (unassigned.Count > 0)
            rows.Add(new ScheduleRow("Unassigned", null, null, null, null,
                Days.ToDictionary(d => d, d => (IReadOnlyList<ScheduleTask>)unassigned.Where(t => t.Covers(d)).ToList())));
        return rows;
    }

    public static string? ShortType(string? jobType) => jobType switch
    {
        "Residential - Standard Home" => "Residential",
        "Residential - Custom House" => "Custom Home",
        "Prevailing Wage" => "Public Works",
        "Property Management / Production" => "Property Mgmt",
        var t => t,
    };

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

    public static string Time(ScheduleTask t) => t.StartTime is { } s
        ? Clock(s) + (t.EndTime is { } e ? " – " + Clock(e) : "")
        : "";

    private static string Clock(TimeOnly t) => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    [GeneratedRegex(@"^(Day\s*\d+\s*\([^)]*\)|(Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s*[\d./]+)\s*[—–-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DatePrefix();
}

public sealed record ScheduleRow(string Title, string? Subtitle, string? ColorStyle, string? ProjectManager, string? JobId, IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduleTask>> Cells);
