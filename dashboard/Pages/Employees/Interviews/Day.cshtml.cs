using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Hiring;

namespace PurpleDashboard.Pages.Employees.Interviews;

/// <summary>
/// One interview day: the slots (replaces the Interviews M-D-YYYY spreadsheet), booking, moving and cancelling,
/// the reminder / day-of confirmation checklist, and the texts filled in with each person's time.
/// </summary>
public class DayModel(HiringService hiring, CurrentUser me) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [TempData] public string? Message { get; set; }
    public bool CanEdit => hiring.Available;

    public InterviewDay Day { get; private set; } = null!;
    public List<TimeOnly> Slots { get; private set; } = [];
    public List<InterviewBooking> Bookings { get; private set; } = [];
    public Dictionary<int, HiringCandidate> People { get; private set; } = [];
    /// <summary>People who can still be booked on this day.</summary>
    public List<HiringCandidate> Bookable { get; private set; } = [];
    public bool PastDeadline { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await hiring.DayAsync(Id, ct) is not { } day) return CanEdit ? NotFound() : RedirectToPage("/Employees/Interviews/Index");
        Day = day;
        ViewData["Title"] = $"Hiring · {Format.Day(day.Date)}";
        ViewData["ModuleName"] = "Hiring";
        Slots = HiringService.Slots(day);
        Bookings = await hiring.BookingsForDayAsync(Id, ct);
        People = (await hiring.CandidatesAsync(ct)).ToDictionary(c => c.Id);
        var booked = Bookings.Select(b => b.CandidateId).ToHashSet();
        Bookable = People.Values
            .Where(c => !booked.Contains(c.Id) && c.Stage is HiringStages.Texted or HiringStages.Booked or HiringStages.Confirmed or HiringStages.NoShow)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PastDeadline = HiringService.PastConfirmDeadline(day);
        return Page();
    }

    public InterviewBooking? BookingAt(TimeOnly slot) => Bookings.FirstOrDefault(b => b.Time == slot);

    /// <summary>English speakers first for early slots, Spanish speakers first for the later half (a hint, not a rule).</summary>
    public IEnumerable<HiringCandidate> PickerOrder(TimeOnly slot)
    {
        var later = HiringService.IsLaterHalf(Day, slot);
        return Bookable.OrderBy(c => later ? (c.Language == "es" ? 0 : c.Language == "both" ? 1 : 2) : (c.Language == "en" ? 0 : c.Language == "both" ? 1 : 2))
            .ThenBy(c => c.Stage == HiringStages.Texted ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public TextContext Context(HiringCandidate? c, TimeOnly? time) =>
        new(c?.Position ?? "painter", c?.Language ?? "en", Day.Date, time, Day.Start, Day.End, Day.Location);

    /// <summary>Flag: past 10am on the day (or after it) and still not confirmed.</summary>
    public bool NeedsRemoval(InterviewBooking b) => PastDeadline && b.ConfirmedAt is null
        && People.TryGetValue(b.CandidateId, out var c) && c.Stage is HiringStages.Booked or HiringStages.Texted;

    private string Who() => me.Email ?? me.DisplayName ?? "unknown";
    private IActionResult Back(string? anchor = null) => Redirect($"/employees/interviews/day/{Id}" + (anchor is null ? "" : "#" + anchor));
    public static string SlotId(TimeOnly t) => "s-" + t.ToString("HHmm");

    public async Task<IActionResult> OnPostBookAsync(TimeOnly slot, int? candidateId, string? name, string? phone, string? position, string? language, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        if (candidateId is null)
        {
            if (string.IsNullOrWhiteSpace(name)) { Message = "Pick someone or enter a name."; return Back(SlotId(slot)); }
            var existing = await hiring.FindByPhoneAsync(phone, ct);
            candidateId = existing?.Id ?? (await hiring.AddCandidateAsync(name, phone,
                IndexModel.Pick(position, HiringLists.Positions.Select(p => p.Key), "painter"),
                IndexModel.Pick(language, HiringLists.Languages.Select(l => l.Key), "en"), null, null, Who(), ct)).Id;
            if (existing is not null) Message = $"{existing.Name} was already in the list (same phone), so they were booked instead of added again.";
        }
        var error = await hiring.BookAsync(Id, candidateId.Value, slot, Who(), ct);
        if (error is not null) Message = error;
        return Back(SlotId(slot));
    }

    public async Task<IActionResult> OnPostMoveAsync(int bookingId, TimeOnly slot, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        Message = await hiring.MoveAsync(bookingId, slot, Who(), ct) ?? $"Moved to {HiringService.SlotLabel(slot)}.";
        return Back(SlotId(slot));
    }

    public async Task<IActionResult> OnPostCancelAsync(int bookingId, string? toStage, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        var stage = toStage is HiringStages.NoShow or HiringStages.Withdrew ? toStage : HiringStages.Texted;
        await hiring.CancelBookingAsync(bookingId, stage, Who(), ct);
        Message = stage == HiringStages.Texted ? "Taken off the schedule (back to Texted, can be rebooked)." : $"Taken off the schedule ({HiringStages.Label(stage)}).";
        return Back();
    }

    public async Task<IActionResult> OnPostReminderAsync(int bookingId, bool sent, CancellationToken ct)
    {
        if (CanEdit) await hiring.SetReminderAsync(bookingId, sent, Who(), ct);
        return Back("b-" + bookingId);
    }

    public async Task<IActionResult> OnPostConfirmAsync(int bookingId, bool confirmed, CancellationToken ct)
    {
        if (CanEdit) await hiring.SetConfirmedAsync(bookingId, confirmed, Who(), ct);
        return Back("b-" + bookingId);
    }

    public async Task<IActionResult> OnPostEditDayAsync(DateOnly date, string? location, TimeOnly start, TimeOnly end, int slotMinutes, string? notes, CancellationToken ct)
    {
        if (!CanEdit) return Back();
        await hiring.UpdateDayAsync(Id, date, string.IsNullOrWhiteSpace(location) ? HiringLists.DefaultLocation : location, start, end, slotMinutes, notes, Who(), ct);
        Message = "Day saved. Anyone booked outside the new slots still shows, marked off-schedule.";
        return Back();
    }

    public async Task<IActionResult> OnPostDeleteDayAsync(CancellationToken ct)
    {
        if (!CanEdit) return Back();
        if (!await hiring.DeleteDayAsync(Id, Who(), ct)) { Message = "Take everyone off the schedule before deleting the day."; return Back(); }
        Message = "Interview day deleted.";
        return RedirectToPage("/Employees/Interviews/Index", new { tab = "days" });
    }
}
