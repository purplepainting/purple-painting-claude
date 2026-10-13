using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Changes;
using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Pages.Changes;

/// <summary>
/// Who changed what on the dashboard, newest first (the ChangeLog table). Read only.
/// </summary>
public class IndexModel(ChangeLogReader reader, TeamDirectory directory, DatabaseStatus database, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    public static readonly int[] DayOptions = [7, 30, 90];

    [BindProperty(SupportsGet = true)] public string? Section { get; set; }
    /// <summary>The email (or name) the change was logged under.</summary>
    [BindProperty(SupportsGet = true)] public string? Person { get; set; }
    [BindProperty(SupportsGet = true)] public int? Days { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>Page number ("page" is taken by Razor Pages).</summary>
    [BindProperty(SupportsGet = true)] public int? P { get; set; }

    public int CurrentDays => Days is { } d && DayOptions.Contains(d) ? d : 30;
    public int CurrentPage => Math.Max(1, P ?? 1);
    public DatabaseStatus Database => database;

    public IReadOnlyList<ChangeView> Rows { get; private set; } = [];
    public IReadOnlyList<string> Areas { get; private set; } = [];
    public IReadOnlyList<(string Value, string Name)> People { get; private set; } = [];
    public bool HasOlder { get; private set; }
    public int Total { get; private set; }
    public PersonNames Names { get; private set; } = new(null, null, new Dictionary<string, string>(), []);
    public string? Error { get; private set; }
    public bool Filtered => !string.IsNullOrWhiteSpace(Section) || !string.IsNullOrWhiteSpace(Person) || !string.IsNullOrWhiteSpace(Q);

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Change log";
        ViewData["ModuleName"] = "Change log";
        if (!reader.Available) return;

        // JobTread staff turn emails into names and crew ids into names; the page still works without them.
        Team? team = null;
        try { team = await directory.GetAsync(ct); }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading staff for the change log failed; showing emails and ids");
        }

        try
        {
            var items = await reader.ItemNamesAsync(ct);
            Names = new PersonNames(me.Email, me.DisplayName, await reader.LinkedNamesAsync(ct), team?.Staff ?? []);
            var page = await reader.GetAsync(new ChangeFilter(Section, Person, CurrentDays, Q, CurrentPage), items, ct);

            var crew = team?.ByMembership.ToDictionary(m => m.Key, m => m.Value.Staff.Name) ?? [];
            var formatter = new ChangeFormatter(crew, items);
            Rows = page.Entries.Select(formatter.Format).ToList();
            Areas = page.Areas;
            People = page.People.Select(p => (p, Names.Name(p))).OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToList();
            HasOlder = page.HasOlder;
            Total = page.Total;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Loading the change log failed");
            Error = "Couldn't read the change log from the database. Try again in a minute.";
        }
    }

    public Dictionary<string, string?> Route(int? page = null, int? days = null) => new()
    {
        ["section"] = string.IsNullOrWhiteSpace(Section) ? null : Section,
        ["person"] = string.IsNullOrWhiteSpace(Person) ? null : Person,
        ["days"] = (days ?? CurrentDays) == 30 ? null : (days ?? CurrentDays).ToString(Us),
        ["q"] = string.IsNullOrWhiteSpace(Q) ? null : Q,
        ["p"] = page is null or 1 ? null : page.Value.ToString(Us),
    };

    /// <summary>"Just now", "12 min ago", "3 hr ago", "Yesterday 4:12 PM", "Mon 4:12 PM", "Oct 3, 4:12 PM".</summary>
    public static string Relative(DateTime utc)
    {
        var local = Local(utc);
        var now = LocalClock.Now;
        var ago = now - local;
        if (ago < TimeSpan.FromMinutes(1)) return "Just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (local.Date == now.Date) return $"{(int)ago.TotalHours} hr ago";
        if (local.Date == now.Date.AddDays(-1)) return "Yesterday " + local.ToString("h:mm tt", Us);
        if (local.Date > now.Date.AddDays(-7)) return local.ToString("ddd h:mm tt", Us);
        return local.ToString(local.Year == now.Year ? "MMM d, h:mm tt" : "MMM d, yyyy", Us);
    }

    /// <summary>"Tue, Oct 13, 2026, 4:12:05 PM Pacific".</summary>
    public static string Exact(DateTime utc) => Local(utc).ToString("ddd, MMM d, yyyy, h:mm:ss tt", Us) + " Pacific";

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), LocalClock.Zone);

    public static string Initials(string name) =>
        string.Concat(name.Split([' ', '.', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
}
