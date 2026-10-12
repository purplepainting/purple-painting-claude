using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
    JobTreadUserKeys userKeys,
    TeamDirectory directory,
    ProjectsService projects,
    CurrentUser me,
    ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"week" (default) or "day".</summary>
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }
    /// <summary>"job" (default) or "person".</summary>
    [BindProperty(SupportsGet = true)] public string? By { get; set; }
    /// <summary>"crew" (default: tasks with painters, or unassigned) or "supervisors" (tasks only supervisors are on).</summary>
    [BindProperty(SupportsGet = true)] public string? Who { get; set; }

    /// <summary>Any date in the period to show (yyyy-MM-dd). Defaults to today.</summary>
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }

    public bool IsDay => Mode == "day";
    public bool ByPerson => By == "person";
    public bool Supervisors => Who == "supervisors";
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

    public decimal WeekHours(string jobId) => Costs.Where(c => c.Key.JobId == jobId).Sum(c => c.Value.Hours);

    public decimal PersonWeekHours(string name) => PersonCosts.Where(c => c.Key.Name == name).Sum(c => c.Value.Hours);

    /// <summary>Hours one person is scheduled for on a task's day (0 without start and end times).</summary>
    public static decimal ScheduledHours(ScheduleTask t) =>
        t.StartTime is { } s && t.EndTime is { } e && e > s ? (decimal)(e - s).TotalHours : 0;

    /// <summary>"$1,234 · 24h" for a cost cell; either part is left out when zero.</summary>
    public static string CostLabel(DayCost c) =>
        string.Join(" · ", new[] { c.Amount > 0 ? Format.Money(c.Amount) : null, c.Hours > 0 ? Format.Number(Math.Round(c.Hours, 1)) + "h" : null }.OfType<string>());

    /// <summary>JSON for the schedule editor (week view only).</summary>
    public string? EditorJson { get; private set; }

    private Dictionary<string, StaffMember> staffByName = [];
    private Dictionary<string, StaffMember> staffByUserId = [];

    /// <summary>Job colors for this week (each job a different pastel). Day view uses the whole week's jobs so colors match.</summary>
    public JobColors Colors { get; private set; } = new([]);

    /// <summary>Levels and PM colors (Employees page, then settings).</summary>
    public Team Team { get; private set; } = null!;

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        Team = directory.Fallback();
        ViewData["Title"] = "Scheduling";
        ViewData["ModuleName"] = "Scheduling";

        var date = Date ?? Today;
        From = IsDay ? date : LocalClock.WeekStart(date);
        To = IsDay ? date : From.AddDays(6);

        IReadOnlyList<StaffMember> staff;
        IReadOnlyList<ScheduleTask> weekTasks;
        try
        {
            Tasks = await schedule.GetTasksAsync(From, To, refresh, ct);
            Team = await directory.GetAsync(ct);
            staff = Team.Staff;
            var weekStart = LocalClock.WeekStart(From);
            weekTasks = IsDay ? await schedule.GetTasksAsync(weekStart, weekStart.AddDays(6), refresh, ct) : Tasks;
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

        // Painter and supervisor schedules are separate: a task belongs to Supervisors when everyone on it is a supervisor.
        Tasks = Tasks.Where(t => IsSupervisorTask(t) == Supervisors).ToList();
        Colors = new JobColors(weekTasks.Where(t => IsSupervisorTask(t) == Supervisors).Select(t => t.JobId));

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

        // Changes go to JobTread as the person pushing, with their own linked key.
        var linked = await userKeys.GetAsync(me.ObjectId, ct);
        if (linked is null)
            return BadRequest(new { error = "Link your JobTread account first: open Connections (bottom of the menu) and add your JobTread key. Your changes are kept in this browser." });

        var results = await pushService.PushAsync(request.Changes, me.Email ?? me.DisplayName ?? "unknown", linked.GrantKey, ct);
        return new JsonResult(new { results });
    }

    /// <summary>
    /// The job's upcoming unfinished tasks outside this week, for "+ Add day" (move one instead of adding).
    /// GET ?handler=JobTasks&amp;jobId=…&amp;date=yyyy-MM-dd (any day in the week shown).
    /// </summary>
    public async Task<IActionResult> OnGetJobTasksAsync(string jobId, CancellationToken ct)
    {
        if (!JobIdPattern().IsMatch(jobId ?? "")) return BadRequest();
        var from = LocalClock.WeekStart(Date ?? Today);
        try
        {
            var list = await schedule.GetJobTasksAsync(jobId!, from.AddDays(-14), from.AddDays(120), ct);
            return new JsonResult(list
                .Where(t => t.End < from || t.Start > from.AddDays(6))
                .OrderBy(t => t.Start)
                .Take(40)
                .Select(t => new
                {
                    id = t.Id, jobId = t.JobId, name = t.Name,
                    start = t.Start.ToString("yyyy-MM-dd"), end = t.End.ToString("yyyy-MM-dd"),
                    startTime = t.StartTime?.ToString("HH:mm"), endTime = t.EndTime?.ToString("HH:mm"),
                    crew = t.Crew.Select(c => c.MembershipId),
                    parentTaskId = t.ParentTaskId, taskTypeId = t.TaskTypeId, dependents = t.Dependents,
                }));
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading tasks for job {JobId} failed", jobId);
            return StatusCode(502, new { error = "Couldn't load this job's tasks from JobTread." });
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex JobIdPattern();

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
                    if (actual.TryGetValue((st.UserId, day), out var a) && (a.Cost > 0 || a.Minutes > 0)) PersonCosts[(name, day)] = new DayCost(a.Cost, false, a.Minutes / 60m);
                }
                else
                {
                    decimal est = 0, hours = 0;
                    foreach (var t in Tasks.Where(t => t.Covers(day) && t.Crew.Any(c => c.MembershipId == st.MembershipId)))
                    {
                        est += await laborCosts.EstimatePersonAsync(t, st.MembershipId, ct) ?? 0;
                        hours += ScheduledHours(t);
                    }
                    if (est > 0 || hours > 0) PersonCosts[(name, day)] = new DayCost(est, true, hours);
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
                            // Only this schedule's people: painters on Crew, supervisors on Supervisors.
                            var users = a.MinutesByUser.Keys.Where(InView).ToList();
                            var cost = users.Sum(u => a.CostByUser.GetValueOrDefault(u));
                            var minutes = users.Sum(u => a.MinutesByUser.GetValueOrDefault(u));
                            if (cost > 0 || minutes > 0) Costs[(jobId, day)] = new DayCost(cost, false, minutes / 60m);
                            Actual[(jobId, day)] = users
                                .Select(u => (Name: staffByUserId.TryGetValue(u, out var st) ? st.Name : "Unknown", Hours: Math.Round(a.MinutesByUser[u] / 60m, 1)))
                                .OrderByDescending(x => x.Hours)
                                .ToList();
                        }
                    }
                    else
                    {
                        decimal est = 0, hours = 0;
                        foreach (var t in Tasks.Where(t => t.JobId == jobId && t.Covers(day)))
                        {
                            est += await laborCosts.EstimateAsync(t, ct) ?? 0;
                            hours += ScheduledHours(t) * t.Crew.Count;
                        }
                        if (est > 0 || hours > 0) Costs[(jobId, day)] = new DayCost(est, true, hours);
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
                .Select(j => (object)new { id = j.Id, name = j.Name, type = j.JobTypeShort, pm = j.ProjectManager, pmColor = Team.PmColor(j.ProjectManager), hue = Colors.HueFor(j.Id), deep = Colors.IsDeep(j.Id) })
                .ToList();
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading jobs for Add job failed");
        }

        var data = new
        {
            weekStart = From.ToString("yyyy-MM-dd"),
            who = Supervisors ? "supervisors" : "crew",
            today = Today.ToString("yyyy-MM-dd"),
            jobTreadLinked = await userKeys.FindAsync(me.ObjectId, ct) is not null,
            days = Enumerable.Range(0, 7).Select(i => From.AddDays(i).ToString("yyyy-MM-dd")),
            jobs = jobsOnSchedule.OrderBy(j => j.JobName, StringComparer.OrdinalIgnoreCase).Select(j => new
            {
                id = j.JobId, name = j.JobName, type = ShortType(j.JobType), pm = j.ProjectManager, pmColor = Team.PmColor(j.ProjectManager), hue = Colors.HueFor(j.JobId), deep = Colors.IsDeep(j.JobId),
                costs = Days.Where(d => Costs.ContainsKey((j.JobId, d))).ToDictionary(d => d.ToString("yyyy-MM-dd"), d => new { amount = Math.Round(Costs[(j.JobId, d)].Amount), estimate = Costs[(j.JobId, d)].IsEstimate, hours = Math.Round(Costs[(j.JobId, d)].Hours, 1) }),
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
            people = staff.Select(s => new { id = s.MembershipId, name = s.Name, first = s.FirstName, level = LevelOf(s), chip = Team.ByMembership.TryGetValue(s.MembershipId, out var m) ? m.ChipClass : "lvl-" + LevelOf(s), chipStyle = Team.ByMembership.TryGetValue(s.MembershipId, out var m2) ? m2.ChipStyle : null }),
        };
        return JsonSerializer.Serialize(data);
    }

    /// <summary>"foreman", "painter", "helper", or "supervisor" for non-field staff.</summary>
    public string LevelOf(StaffMember s) => Team.ByMembership.TryGetValue(s.MembershipId, out var m) ? m.Level : directory.DefaultLevel(s);

    public string LevelOf(string name) => Team.LevelOf(name);

    private static int Rank(string level) => Levels.Rank(level);

    /// <summary>Crew in display order: foremen first, then painters, helpers, supervisors.</summary>
    public IEnumerable<CrewAssignment> Ordered(IEnumerable<CrewAssignment> crew) =>
        crew.OrderBy(c => Rank(LevelOf(c.Name))).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<(string Name, decimal Hours)> Ordered(IEnumerable<(string Name, decimal Hours)> crew) =>
        crew.OrderBy(c => Rank(LevelOf(c.Name))).ThenByDescending(c => c.Hours);

    /// <summary>Does this person's time belong on the schedule being shown (supervisors vs everyone else)?</summary>
    private bool InView(string userId) => (staffByUserId.TryGetValue(userId, out var st) && !st.IsField) == Supervisors;

    public bool IsSupervisorTask(ScheduleTask t) => t.Crew.Count > 0 && t.Crew.All(c => LevelOf(c.Name) == "supervisor");

    private List<ScheduleRow> RowsByJob() => Tasks
        .GroupBy(t => t.JobId)
        .Select(g =>
        {
            var first = g.First();
            return new ScheduleRow(first.JobName, ShortType(first.JobType), Colors.Style(first.JobId), first.ProjectManager, first.JobId,
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

    public Dictionary<string, string?> Route(DateOnly? date = null, string? mode = null, string? by = null, string? who = null) => new()
    {
        ["who"] = (who ?? Who) == "supervisors" ? "supervisors" : null,
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

    /// <summary>Tasks renamed "DELETE …" (marked in the editor) are waiting for someone to delete them in JobTread.</summary>
    public static bool IsMarkedForDeletion(ScheduleTask t) => t.Name.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);

    public static string Time(ScheduleTask t) => t.StartTime is { } s
        ? Clock(s) + (t.EndTime is { } e ? " – " + Clock(e) : "")
        : "";

    private static string Clock(TimeOnly t) => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    [GeneratedRegex(@"^(Day\s*\d+\s*\([^)]*\)|(Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s*[\d./]+)\s*[—–-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DatePrefix();
}

public sealed record ScheduleRow(string Title, string? Subtitle, string? ColorStyle, string? ProjectManager, string? JobId, IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduleTask>> Cells);
