using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Net.Http.Headers;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Hiring;

namespace PurpleDashboard.Pages.Employees.Interviews;

/// <summary>
/// One applicant: details, stage and history, their texts, files (scanned sheet, resume, timesheet photo),
/// interview sheet, trial day, hire and onboarding.
/// </summary>
public class CandidateModel(HiringService hiring, CurrentUser me) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [TempData] public string? Message { get; set; }
    public bool CanEdit => hiring.Available;

    public HiringCandidate C { get; private set; } = null!;
    public InterviewSheet? Sheet { get; private set; }
    public List<HiringEvent> Events { get; private set; } = [];
    public List<DocumentInfo> Documents { get; private set; } = [];
    public List<InterviewDay> Days { get; private set; } = [];
    public List<InterviewBooking> AllBookings { get; private set; } = [];
    /// <summary>Their latest booking (the day they came to, or are coming to).</summary>
    public (InterviewDay Day, InterviewBooking Booking)? Booking { get; private set; }
    /// <summary>Open slots on upcoming days, for booking from here.</summary>
    public List<(InterviewDay Day, TimeOnly Slot)> OpenSlots { get; private set; } = [];
    public (int DayId, TimeOnly Slot)? Suggested { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await hiring.CandidateAsync(Id, ct) is not { } c) return CanEdit ? NotFound() : RedirectToPage("/Employees/Interviews/Index");
        C = c;
        ViewData["Title"] = $"Hiring · {c.Name}";
        ViewData["ModuleName"] = "Hiring";
        Sheet = InterviewSheet.FromJson(c.SheetJson);
        Events = await hiring.EventsAsync(Id, ct);
        Documents = await hiring.DocumentsAsync(Id, ct);
        Days = await hiring.DaysAsync(ct);
        AllBookings = await hiring.BookingsAsync(ct);

        var mine = AllBookings.Where(b => b.CandidateId == Id).Select(b => (Day: Days.FirstOrDefault(d => d.Id == b.DayId), Booking: b))
            .Where(x => x.Day is not null).OrderByDescending(x => x.Day!.Date).FirstOrDefault();
        Booking = mine.Day is null ? null : (mine.Day, mine.Booking);

        var today = LocalClock.Today;
        foreach (var day in Days.Where(d => d.Date >= today).OrderBy(d => d.Date))
        {
            var taken = AllBookings.Where(b => b.DayId == day.Id).Select(b => b.Time).ToHashSet();
            OpenSlots.AddRange(HiringService.Slots(day).Where(s => !taken.Contains(s)).Select(s => (day, s)));
            if (Suggested is null && HiringService.SuggestSlot(day, AllBookings, c.Language) is { } sug) Suggested = (day.Id, sug);
        }
        return Page();
    }

    public bool IsUpcomingBooking => Booking is { } b && b.Day.Date >= LocalClock.Today;

    /// <summary>Texts use their booking, else the next interview day, else "[date]".</summary>
    public TextContext Context()
    {
        var day = Booking is { } b && b.Day.Date >= LocalClock.Today ? b.Day : Days.Where(d => d.Date >= LocalClock.Today).MinBy(d => d.Date) ?? Booking?.Day;
        var time = Booking is { } bk && day is not null && bk.Day.Id == day.Id ? bk.Booking.Time : (TimeOnly?)null;
        return new TextContext(C.Position, C.Language, day?.Date, time, day?.Start ?? HiringLists.DefaultStart, day?.End ?? HiringLists.DefaultEnd,
            day?.Location ?? HiringLists.DefaultLocation, C.TrialDate, C.TrialSupervisor);
    }

    public static string Size(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private string Who() => me.Email ?? me.DisplayName ?? "unknown";
    private IActionResult Back(string? anchor = null) => Redirect($"/employees/interviews/candidate/{Id}" + (anchor is null ? "" : "#" + anchor));

    // ---- Files ----

    /// <summary>Shows a file inline (view) or as a download. Signed-in users only (Easy Auth guards every path).</summary>
    public async Task<IActionResult> OnGetFileAsync(int docId, bool download, CancellationToken ct)
    {
        var doc = await hiring.DocumentAsync(docId, ct);
        if (doc is null || doc.CandidateId != Id) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, max-age=3600";
        var disposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline");
        disposition.SetHttpFileName(doc.FileName);
        Response.Headers.ContentDisposition = disposition.ToString();
        return File(doc.Data, doc.ContentType);
    }

    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> files, string? kind, CancellationToken ct)
    {
        if (!CanEdit) return Back("files");
        if (files.Count == 0) { Message = "Pick a file or take a photo first."; return Back("files"); }
        var errors = new List<string>();
        var saved = 0;
        foreach (var f in files.Take(10))
        {
            if (f.Length > HiringService.MaxDocumentBytes) { errors.Add($"{f.FileName}: files can be up to 10 MB."); continue; }
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms, ct);
            if (await hiring.AddDocumentAsync(Id, f.FileName, ms.ToArray(), kind, Who(), ct) is { } error) errors.Add($"{f.FileName}: {error}");
            else saved++;
        }
        Message = string.Join(" ", new[] { saved > 0 ? $"Uploaded {saved} file{(saved == 1 ? "" : "s")}." : null }.Concat(errors).OfType<string>());
        return Back("files");
    }

    public async Task<IActionResult> OnPostDeleteFileAsync(int docId, CancellationToken ct)
    {
        if (!CanEdit) return Back("files");
        var doc = (await hiring.DocumentsAsync(Id, ct)).FirstOrDefault(d => d.Id == docId);
        if (doc is not null) { await hiring.DeleteDocumentAsync(docId, Who(), ct); Message = $"Deleted {doc.FileName}."; }
        return Back("files");
    }

    // ---- Details, stage, notes ----

    public async Task<IActionResult> OnPostEditAsync(string? name, string? phone, string? position, string? language, string? source, string? experience, CancellationToken ct)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(name)) return Back();
        await hiring.UpdateCandidateAsync(Id, name, phone, IndexModel.Pick(position, HiringLists.Positions.Select(p => p.Key), "painter"),
            IndexModel.Pick(language, HiringLists.Languages.Select(l => l.Key), "en"), HiringLists.Sources.Contains(source) ? source : null, experience, Who(), ct);
        Message = "Saved.";
        return Back();
    }

    public async Task<IActionResult> OnPostStageAsync(string stage, string? note, CancellationToken ct)
    {
        if (!CanEdit || !HiringStages.IsValid(stage)) return Back();
        await hiring.SetStageAsync(Id, stage, Who(), note, ct);
        Message = $"Stage: {HiringStages.Label(stage)}.";
        return Back();
    }

    public async Task<IActionResult> OnPostNoteAsync(string? note, CancellationToken ct)
    {
        if (CanEdit && !string.IsNullOrWhiteSpace(note)) await hiring.AddNoteAsync(Id, note, Who(), ct);
        return Back("history");
    }

    /// <summary>Book into "dayId|HH:mm".</summary>
    public async Task<IActionResult> OnPostBookAsync(string? slot, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        var parts = (slot ?? "").Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var dayId) || !TimeOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out var time)) { Message = "Pick a slot."; return Back(); }
        Message = await hiring.BookAsync(dayId, Id, time, Who(), ct) ?? $"Booked at {HiringService.SlotLabel(time)}.";
        return Back();
    }

    // ---- Trial day and hire ----

    public async Task<IActionResult> OnPostTrialAsync(DateOnly? date, string? supervisor, string? notes, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        if (date is null) { Message = "Pick the trial day date."; return Back("trial"); }
        await hiring.InviteToTrialAsync(Id, date.Value, supervisor, notes, Who(), ct);
        Message = $"Invited to a trial day on {Format.Day(date.Value)}. The trial day invite text is ready below.";
        return Back("trial");
    }

    public async Task<IActionResult> OnPostTrialSaveAsync(DateOnly? date, string? supervisor, string? notes, string? hours, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        decimal? h = decimal.TryParse(hours, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : null;
        await hiring.SaveTrialAsync(Id, date, supervisor, notes, h, Who(), ct);
        Message = "Trial day saved.";
        return Back("trial");
    }

    public async Task<IActionResult> OnPostHireAsync(DateOnly? date, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        await hiring.HireAsync(Id, date ?? LocalClock.Today, Who(), ct);
        Message = "Hired. Work through the onboarding checklist below.";
        return Back("onboarding");
    }

    public async Task<IActionResult> OnPostOnboardingAsync(List<string> done, CancellationToken ct)
    {
        if (CanEdit) await hiring.SaveOnboardingAsync(Id, done, Who(), ct);
        Message = "Onboarding saved.";
        return Back("onboarding");
    }

    public async Task<IActionResult> OnPostNotHiredAsync(string? reason, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        await hiring.NotHiredAsync(Id, reason, Who(), ct);
        Message = "Marked not hired. The optional \"Not this time\" text is below if you want to send it.";
        return Back("texts");
    }

    public static string EventText(HiringEvent e) => e.Kind == "stage" && e.ToStage is not null
        ? (e.FromStage is null ? HiringStages.Label(e.ToStage) : $"{HiringStages.Label(e.FromStage)} → {HiringStages.Label(e.ToStage)}") + (e.Detail is null ? "" : " · " + e.Detail)
        : e.Detail ?? e.Kind;
}
