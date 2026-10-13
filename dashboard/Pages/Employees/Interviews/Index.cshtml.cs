using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Hiring;

namespace PurpleDashboard.Pages.Employees.Interviews;

/// <summary>
/// Hiring: everyone who has applied (the pipeline and the list of past applicants) and the interview days.
/// Replaces the Interviews spreadsheets. Dashboard-owned data only.
/// </summary>
public class IndexModel(HiringService hiring, CurrentUser me, Permissions permissions) : PageModel
{
    /// <summary>"days" for interview days; otherwise candidates.</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>A stage key, "all", or empty for everyone still in progress.</summary>
    [BindProperty(SupportsGet = true)] public string? Stage { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>Only people booked on this interview day.</summary>
    [BindProperty(SupportsGet = true)] public int? Day { get; set; }

    [TempData] public string? Message { get; set; }
    public bool CanEdit => hiring.Available && permissions.Can(Perm.HiringEdit);
    public bool DbAvailable => hiring.Available;
    public bool DaysTab => Tab == "days";

    public List<HiringCandidate> All { get; private set; } = [];
    public List<HiringCandidate> Shown { get; private set; } = [];
    public List<InterviewDay> Days { get; private set; } = [];
    public List<InterviewBooking> Bookings { get; private set; } = [];
    public Dictionary<int, int> DocCounts { get; private set; } = [];
    public InterviewDay? DayFilter { get; private set; }
    public DateOnly DefaultDate { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Hiring";
        ViewData["ModuleName"] = "Hiring";

        All = await hiring.CandidatesAsync(ct);
        Days = await hiring.DaysAsync(ct);
        Bookings = await hiring.BookingsAsync(ct);
        DocCounts = await hiring.DocumentCountsAsync(ct);
        DefaultDate = HiringService.DefaultDate(Days);
        DayFilter = Day is { } d ? Days.FirstOrDefault(x => x.Id == d) : null;

        IEnumerable<HiringCandidate> list = All;
        if (DayFilter is { } df) list = list.Where(c => Bookings.Any(b => b.DayId == df.Id && b.CandidateId == c.Id));
        list = list.Where(c => Matches(c, Q));
        // Stage pills count what the search leaves, so the numbers match what a tap shows.
        Searched = list.ToList();
        Shown = Searched.Where(InStage).ToList();
        if (DayFilter is not null) Shown = Shown.OrderBy(c => BookingOf(c.Id)?.Booking.Time).ToList();
    }

    public List<HiringCandidate> Searched { get; private set; } = [];

    private bool InStage(HiringCandidate c) => Stage switch
    {
        null or "" => HiringStages.Get(c.Stage).Open,
        "all" => true,
        _ => c.Stage == Stage,
    };

    public int CountFor(string? stage) => stage switch
    {
        null => Searched.Count(c => HiringStages.Get(c.Stage).Open),
        "all" => Searched.Count,
        _ => Searched.Count(c => c.Stage == stage),
    };

    /// <summary>The interview day each person came to (or is booked for): their latest booking.</summary>
    public (InterviewDay Day, InterviewBooking Booking)? BookingOf(int candidateId)
    {
        var best = Bookings.Where(b => b.CandidateId == candidateId)
            .Select(b => (Day: Days.FirstOrDefault(d => d.Id == b.DayId), Booking: b))
            .Where(x => x.Day is not null)
            .OrderByDescending(x => x.Day!.Date).FirstOrDefault();
        return best.Day is null ? null : (best.Day, best.Booking);
    }

    /// <summary>Search by name, phone, position, language, stage, source, experience, or a date (interviewed or interview day: "10/14", "Oct 14", "2026-10-14").</summary>
    private bool Matches(HiringCandidate c, string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        q = q.Trim();
        bool Has(string? s) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var qDigits = new string(q.Where(char.IsAsciiDigit).ToArray());
        if (Has(c.Name) || Has(c.Source) || Has(c.Experience) || Has(HiringLists.Position(c.Position).Label)
            || Has(HiringLists.LanguageLabel(c.Language)) || Has(HiringStages.Label(c.Stage)) || Has(c.Interviewer)) return true;
        if (qDigits.Length >= 3 && !q.Contains('/') && !q.Contains('-') && new string((c.Phone ?? "").Where(char.IsAsciiDigit).ToArray()).Contains(qDigits)) return true;
        var dates = new List<DateOnly>();
        if (c.InterviewedOn is { } io) dates.Add(io);
        dates.AddRange(Bookings.Where(b => b.CandidateId == c.Id).Select(b => Days.FirstOrDefault(d => d.Id == b.DayId)?.Date).OfType<DateOnly>());
        return dates.Any(d => DateStrings(d).Any(Has));
    }

    private static IEnumerable<string> DateStrings(DateOnly d)
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        yield return d.ToString("M/d", us);
        yield return d.ToString("M/d/yyyy", us);
        yield return d.ToString("M/d/yy", us);
        yield return d.ToString("yyyy-MM-dd", us);
        yield return d.ToString("MMM d", us);
        yield return d.ToString("MMMM d", us);
        yield return d.ToString("M-d-yyyy", us);
    }

    public Dictionary<string, string?> RouteFor(string? stage) => new() { ["stage"] = stage, ["q"] = Q, ["day"] = Day?.ToString() };

    private string Who() => me.Email ?? me.DisplayName ?? "unknown";

    public async Task<IActionResult> OnPostAddAsync(string? name, string? phone, string? position, string? language, string? source, string? experience, CancellationToken ct)
    {
        if (!CanEdit) return RedirectToPage();
        if (string.IsNullOrWhiteSpace(name)) { Message = "Enter a name."; return RedirectToPage(); }
        if (await hiring.FindByPhoneAsync(phone, ct) is { } existing)
        {
            Message = $"{existing.Name} already has that phone number, so nobody was added. Here's their page.";
            return RedirectToPage("/Employees/Interviews/Candidate", new { id = existing.Id });
        }
        var c = await hiring.AddCandidateAsync(name, phone, Pick(position, HiringLists.Positions.Select(p => p.Key), "painter"),
            Pick(language, HiringLists.Languages.Select(l => l.Key), "en"), HiringLists.Sources.Contains(source) ? source : null, experience, Who(), ct);
        Message = $"Added {c.Name}.";
        return RedirectToPage(new { q = Q, stage = Stage });
    }

    public async Task<IActionResult> OnPostDayAsync(DateOnly? date, string? location, TimeOnly? start, TimeOnly? end, int? slotMinutes, string? notes, CancellationToken ct)
    {
        if (!CanEdit || date is null) return RedirectToPage(new { tab = "days" });
        var day = await hiring.CreateDayAsync(date.Value, string.IsNullOrWhiteSpace(location) ? HiringLists.DefaultLocation : location,
            start ?? HiringLists.DefaultStart, end ?? HiringLists.DefaultEnd, slotMinutes ?? HiringLists.DefaultSlotMinutes, notes, Who(), ct);
        Message = $"Interview day set for {Format.Day(day.Date)}.";
        return RedirectToPage("/Employees/Interviews/Day", new { id = day.Id });
    }

    public static string Pick(string? value, IEnumerable<string> allowed, string fallback) => value is not null && allowed.Contains(value) ? value : fallback;

    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    public static string Stars(int? rating) => rating is { } r and > 0 ? new string('★', r) + new string('☆', 5 - r) : "";
}
