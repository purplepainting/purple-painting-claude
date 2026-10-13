using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;

namespace PurpleDashboard.Modules.Hiring;

/// <summary>
/// Hiring (Employees → Hiring): candidates and their stages, interview days and slots, the interview sheet,
/// trial day and onboarding. All dashboard-owned data; nothing here writes to JobTread or QuickBooks
/// ("Added to JobTread" / "Added to QuickBooks payroll" are checkboxes someone ticks after doing it there).
/// Every stage change is kept in HiringEvents (the candidate's history) and the ChangeLog (area "hiring").
/// </summary>
/// <summary>A candidate's file without its bytes (for lists).</summary>
public sealed record DocumentInfo(int Id, int CandidateId, string FileName, string ContentType, long Size, string Kind, DateTime UploadedAt, string? UploadedBy);

public sealed class HiringService(IServiceProvider services, DatabaseStatus database, ChangeLogWriter changeLog)
{
    public const string Area = "hiring";

    public bool Available => database.Available;
    private DashboardDb Db => services.GetRequiredService<DashboardDb>();

    // ---- Candidates ----

    public async Task<List<HiringCandidate>> CandidatesAsync(CancellationToken ct) =>
        database.Available ? await Db.HiringCandidates.AsNoTracking().OrderByDescending(c => c.StageAt).ToListAsync(ct) : [];

    public async Task<HiringCandidate?> CandidateAsync(int id, CancellationToken ct) =>
        database.Available ? await Db.HiringCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) : null;

    public async Task<List<HiringEvent>> EventsAsync(int candidateId, CancellationToken ct) =>
        await Db.HiringEvents.AsNoTracking().Where(e => e.CandidateId == candidateId).OrderByDescending(e => e.At).ThenByDescending(e => e.Id).ToListAsync(ct);

    /// <summary>Someone already in the list with the same phone number (last 10 digits), so a re-texted person isn't added twice.</summary>
    public async Task<HiringCandidate?> FindByPhoneAsync(string? phone, CancellationToken ct)
    {
        var key = PhoneKey(phone);
        if (key is null || !database.Available) return null;
        var withPhones = await Db.HiringCandidates.AsNoTracking().Where(c => c.Phone != null).ToListAsync(ct);
        return withPhones.FirstOrDefault(c => PhoneKey(c.Phone) == key);
    }

    public async Task<HiringCandidate> AddCandidateAsync(string name, string? phone, string position, string language, string? source, string? experience, string user, CancellationToken ct)
    {
        var db = Db;
        var now = DateTime.UtcNow;
        var c = new HiringCandidate
        {
            Name = Clip(name, 200)!, Phone = Clip(TextTemplates.PrettyPhone(phone), 40), Position = position, Language = language,
            Source = Clip(source, 40), Experience = Clip(experience, 500), Stage = HiringStages.Texted, StageAt = now,
            CreatedAt = now, CreatedBy = user, UpdatedAt = now,
        };
        db.HiringCandidates.Add(c);
        await db.SaveChangesAsync(ct);
        db.HiringEvents.Add(new HiringEvent { CandidateId = c.Id, At = now, Kind = "stage", ToStage = c.Stage, Detail = "Added" + (c.Source is null ? "" : $" (from {c.Source})"), By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "add-candidate", c.Name, new { c.Id, c.Position, c.Language, c.Source }, ct);
        return c;
    }

    public async Task UpdateCandidateAsync(int id, string name, string? phone, string position, string language, string? source, string? experience, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        var before = new { c.Name, c.Phone, c.Position, c.Language, c.Source, c.Experience };
        c.Name = Clip(name, 200)!; c.Phone = Clip(TextTemplates.PrettyPhone(phone), 40); c.Position = position; c.Language = language;
        c.Source = Clip(source, 40); c.Experience = Clip(experience, 500); c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "edit-candidate", c.Name, new { before, after = new { c.Name, c.Phone, c.Position, c.Language, c.Source, c.Experience } }, ct);
    }

    /// <summary>Move a candidate to a stage (any direction; pages decide when). Records who and when.</summary>
    public async Task SetStageAsync(int id, string stage, string user, string? note, CancellationToken ct)
    {
        if (!HiringStages.IsValid(stage)) return;
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        if (c.Stage == stage && string.IsNullOrWhiteSpace(note)) return;
        await ChangeStageAsync(db, c, stage, user, note, ct);
    }

    public async Task AddNoteAsync(int id, string note, string user, CancellationToken ct)
    {
        var db = Db;
        db.HiringEvents.Add(new HiringEvent { CandidateId = id, At = DateTime.UtcNow, Kind = "note", Detail = Clip(note, 2000), By = user });
        await db.SaveChangesAsync(ct);
    }

    // ---- Interview days and bookings ----

    public async Task<List<InterviewDay>> DaysAsync(CancellationToken ct) =>
        database.Available ? await Db.InterviewDays.AsNoTracking().OrderByDescending(d => d.Date).ToListAsync(ct) : [];

    public async Task<InterviewDay?> DayAsync(int id, CancellationToken ct) =>
        database.Available ? await Db.InterviewDays.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) : null;

    /// <summary>The next interview day today or later, if there is one.</summary>
    public async Task<InterviewDay?> NextDayAsync(CancellationToken ct)
    {
        if (!database.Available) return null;
        var today = LocalClock.Today;
        return await Db.InterviewDays.AsNoTracking().Where(d => d.Date >= today).OrderBy(d => d.Date).FirstOrDefaultAsync(ct);
    }

    public async Task<List<InterviewBooking>> BookingsAsync(CancellationToken ct) =>
        database.Available ? await Db.InterviewBookings.AsNoTracking().ToListAsync(ct) : [];

    public async Task<List<InterviewBooking>> BookingsForDayAsync(int dayId, CancellationToken ct) =>
        await Db.InterviewBookings.AsNoTracking().Where(b => b.DayId == dayId).OrderBy(b => b.Time).ToListAsync(ct);

    public async Task<InterviewDay> CreateDayAsync(DateOnly date, string location, TimeOnly start, TimeOnly end, int slotMinutes, string? notes, string user, CancellationToken ct)
    {
        var db = Db;
        var day = new InterviewDay
        {
            Date = date, Location = Clip(location, 300)!, Start = start, End = end > start ? end : start.AddMinutes(slotMinutes),
            SlotMinutes = Math.Clamp(slotMinutes, 5, 120), Notes = Clip(notes, 2000), CreatedAt = DateTime.UtcNow, CreatedBy = user,
        };
        db.InterviewDays.Add(day);
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "create-day", $"Interviews {date:M/d/yyyy}", new { day.Id, day.Location, start = day.Start.ToString("HH:mm"), end = day.End.ToString("HH:mm"), day.SlotMinutes }, ct);
        return day;
    }

    public async Task UpdateDayAsync(int id, DateOnly date, string location, TimeOnly start, TimeOnly end, int slotMinutes, string? notes, string user, CancellationToken ct)
    {
        var db = Db;
        var day = await db.InterviewDays.FirstAsync(d => d.Id == id, ct);
        day.Date = date; day.Location = Clip(location, 300)!; day.Start = start; day.End = end > start ? end : start.AddMinutes(slotMinutes);
        day.SlotMinutes = Math.Clamp(slotMinutes, 5, 120); day.Notes = Clip(notes, 2000);
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "edit-day", $"Interviews {date:M/d/yyyy}", new { day.Location, start = day.Start.ToString("HH:mm"), end = day.End.ToString("HH:mm"), day.SlotMinutes }, ct);
    }

    /// <summary>Deletes a day with nobody booked on it. False when someone is still booked.</summary>
    public async Task<bool> DeleteDayAsync(int id, string user, CancellationToken ct)
    {
        var db = Db;
        if (await db.InterviewBookings.AnyAsync(b => b.DayId == id, ct)) return false;
        if (await db.InterviewDays.FirstOrDefaultAsync(d => d.Id == id, ct) is not { } day) return true;
        db.InterviewDays.Remove(day);
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "delete-day", $"Interviews {day.Date:M/d/yyyy}", null, ct);
        return true;
    }

    /// <summary>
    /// Book a candidate into a slot. Moves them out of any other upcoming slot (one booking at a time) and
    /// moves an earlier-stage candidate to Booked. Returns an error message, or null when it worked.
    /// </summary>
    public async Task<string?> BookAsync(int dayId, int candidateId, TimeOnly time, string user, CancellationToken ct)
    {
        var db = Db;
        var day = await db.InterviewDays.FirstOrDefaultAsync(d => d.Id == dayId, ct);
        var c = await db.HiringCandidates.FirstOrDefaultAsync(x => x.Id == candidateId, ct);
        if (day is null || c is null) return "That day or person no longer exists.";
        if (!Slots(day).Contains(time)) return "That time isn't one of the day's slots.";
        if (await db.InterviewBookings.AnyAsync(b => b.DayId == dayId && b.Time == time && b.CandidateId != candidateId, ct)) return $"{SlotLabel(time)} is already taken.";

        var today = LocalClock.Today;
        var upcomingDays = await db.InterviewDays.Where(d => d.Date >= today || d.Id == dayId).Select(d => d.Id).ToListAsync(ct);
        var existing = await db.InterviewBookings.Where(b => b.CandidateId == candidateId && upcomingDays.Contains(b.DayId)).ToListAsync(ct);
        var sameDay = existing.FirstOrDefault(b => b.DayId == dayId);
        db.InterviewBookings.RemoveRange(existing.Where(b => b.DayId != dayId));
        if (sameDay is not null) sameDay.Time = time;
        else db.InterviewBookings.Add(new InterviewBooking { DayId = dayId, CandidateId = candidateId, Time = time, CreatedAt = DateTime.UtcNow, CreatedBy = user });

        var detail = $"Booked {day.Date:ddd M/d} at {SlotLabel(time)}";
        db.HiringEvents.Add(new HiringEvent { CandidateId = candidateId, At = DateTime.UtcNow, Kind = "booking", Detail = detail, By = user });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return $"{SlotLabel(time)} was just taken. Pick another slot."; }
        await LogAsync(user, "book", c.Name, new { day = day.Date.ToString("yyyy-MM-dd"), time = time.ToString("HH:mm") }, ct);

        // Rebooking a no-show puts them back to Booked; moving a confirmed person to another day needs a new confirmation.
        var toBooked = c.Stage is HiringStages.Texted or HiringStages.NoShow or HiringStages.Withdrew
            || (c.Stage == HiringStages.Confirmed && sameDay is null);
        if (toBooked) await ChangeStageAsync(db, c, HiringStages.Booked, user, null, ct);
        return null;
    }

    public async Task<string?> MoveAsync(int bookingId, TimeOnly time, string user, CancellationToken ct)
    {
        var db = Db;
        var b = await db.InterviewBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct);
        if (b is null) return "That booking no longer exists.";
        return await BookAsync(b.DayId, b.CandidateId, time, user, ct);
    }

    /// <summary>
    /// Take someone off the day's schedule. Their stage goes to <paramref name="toStage"/> (Texted to rebook later,
    /// No-show or Withdrew) unless they're already past the interview.
    /// </summary>
    public async Task CancelBookingAsync(int bookingId, string toStage, string user, CancellationToken ct)
    {
        var db = Db;
        var b = await db.InterviewBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct);
        if (b is null) return;
        var day = await db.InterviewDays.FirstAsync(d => d.Id == b.DayId, ct);
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == b.CandidateId, ct);
        db.InterviewBookings.Remove(b);
        db.HiringEvents.Add(new HiringEvent { CandidateId = c.Id, At = DateTime.UtcNow, Kind = "booking", Detail = $"Removed from {day.Date:ddd M/d} {SlotLabel(b.Time)}", By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "cancel-booking", c.Name, new { day = day.Date.ToString("yyyy-MM-dd"), time = b.Time.ToString("HH:mm"), toStage }, ct);
        if (c.Stage is HiringStages.Booked or HiringStages.Confirmed && HiringStages.IsValid(toStage) && c.Stage != toStage)
            await ChangeStageAsync(db, c, toStage, user, null, ct);
    }

    public async Task SetReminderAsync(int bookingId, bool sent, string user, CancellationToken ct)
    {
        var db = Db;
        var b = await db.InterviewBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct);
        if (b is null || (b.ReminderSentAt is not null) == sent) return;
        b.ReminderSentAt = sent ? DateTime.UtcNow : null;
        b.ReminderSentBy = sent ? user : null;
        db.HiringEvents.Add(new HiringEvent { CandidateId = b.CandidateId, At = DateTime.UtcNow, Kind = "booking", Detail = sent ? "Reminder text sent" : "Reminder unmarked", By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, sent ? "reminder-sent" : "reminder-unmarked", await NameAsync(db, b.CandidateId, ct), null, ct);
    }

    /// <summary>Day-of confirmation: Booked → Confirmed (and back to Booked when unticked).</summary>
    public async Task SetConfirmedAsync(int bookingId, bool confirmed, string user, CancellationToken ct)
    {
        var db = Db;
        var b = await db.InterviewBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct);
        if (b is null || (b.ConfirmedAt is not null) == confirmed) return;
        b.ConfirmedAt = confirmed ? DateTime.UtcNow : null;
        b.ConfirmedBy = confirmed ? user : null;
        await db.SaveChangesAsync(ct);
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == b.CandidateId, ct);
        if (confirmed && c.Stage is HiringStages.Texted or HiringStages.Booked) await ChangeStageAsync(db, c, HiringStages.Confirmed, user, "Confirmed day of", ct);
        else if (!confirmed && c.Stage == HiringStages.Confirmed) await ChangeStageAsync(db, c, HiringStages.Booked, user, "Confirmation unmarked", ct);
    }

    // ---- Interview, trial day, hire ----

    /// <summary>Save the interview sheet. Name, phone and position on the sheet update the candidate; the stage moves to Interviewed.</summary>
    public async Task SaveSheetAsync(int id, InterviewSheet sheet, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        var first = c.SheetJson is null;
        if (!string.IsNullOrWhiteSpace(sheet.Name)) c.Name = Clip(sheet.Name, 200)!;
        if (!string.IsNullOrWhiteSpace(sheet.Phone)) c.Phone = Clip(TextTemplates.PrettyPhone(sheet.Phone), 40);
        if (HiringLists.Positions.Any(p => p.Key == sheet.Position)) c.Position = sheet.Position!;
        c.SheetJson = sheet.ToJson();
        c.Rating = sheet.Rating is >= 1 and <= 5 ? sheet.Rating : null;
        c.InterviewedOn = sheet.Date ?? LocalClock.Today;
        c.Interviewer = Clip(sheet.Interviewer, 200);
        c.UpdatedAt = DateTime.UtcNow;
        db.HiringEvents.Add(new HiringEvent { CandidateId = id, At = DateTime.UtcNow, Kind = "sheet", Detail = (first ? "Interview sheet filled in" : "Interview sheet updated") + (c.Rating is { } r ? $" · {r}/5" : "") + (c.Interviewer is null ? "" : $" · by {c.Interviewer}"), By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "interview-sheet", c.Name, new { c.Rating, c.Interviewer, date = c.InterviewedOn?.ToString("yyyy-MM-dd") }, ct);
        if (HiringStages.IsBefore(c.Stage, HiringStages.Interviewed) || c.Stage is HiringStages.NoShow)
            await ChangeStageAsync(db, c, HiringStages.Interviewed, user, null, ct);
    }

    public async Task InviteToTrialAsync(int id, DateOnly date, string? supervisor, string? notes, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        c.TrialDate = date; c.TrialSupervisor = Clip(supervisor, 200); c.TrialNotes = Clip(notes, 2000); c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "trial-invite", c.Name, new { date = date.ToString("yyyy-MM-dd"), c.TrialSupervisor }, ct);
        await ChangeStageAsync(db, c, HiringStages.Trial, user, $"Trial day {date:ddd M/d}" + (c.TrialSupervisor is null ? "" : $" with {c.TrialSupervisor}"), ct);
    }

    public async Task SaveTrialAsync(int id, DateOnly? date, string? supervisor, string? notes, decimal? hours, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        var hoursChanged = c.TrialHours != hours;
        c.TrialDate = date ?? c.TrialDate; c.TrialSupervisor = Clip(supervisor, 200); c.TrialNotes = Clip(notes, 2000);
        c.TrialHours = hours is > 0 ? Math.Round(hours.Value, 2) : null; c.UpdatedAt = DateTime.UtcNow;
        if (hoursChanged && c.TrialHours is { } h) db.HiringEvents.Add(new HiringEvent { CandidateId = id, At = DateTime.UtcNow, Kind = "trial", Detail = $"Trial day hours: {h:0.##}", By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "trial-save", c.Name, new { date = c.TrialDate?.ToString("yyyy-MM-dd"), c.TrialSupervisor, c.TrialHours }, ct);
    }

    public async Task HireAsync(int id, DateOnly date, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        c.HiredOn = date; c.NotHiredReason = null; c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "hire", c.Name, new { date = date.ToString("yyyy-MM-dd"), position = c.Position }, ct);
        await ChangeStageAsync(db, c, HiringStages.Hired, user, $"Hired {date:M/d} as {HiringLists.Position(c.Position).Label}", ct);
    }

    public async Task SaveOnboardingAsync(int id, IEnumerable<string> done, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        var keys = HiringLists.Onboarding.Select(o => o.Key).Where(done.Contains).ToList();
        var before = OnboardingDone(c);
        c.Onboarding = keys.Count == 0 ? null : string.Join(',', keys);
        c.UpdatedAt = DateTime.UtcNow;
        var added = keys.Except(before).ToList();
        var removed = before.Except(keys).ToList();
        if (added.Count + removed.Count == 0) return;
        string Labels(List<string> k) => string.Join(", ", HiringLists.Onboarding.Where(o => k.Contains(o.Key)).Select(o => o.Label));
        db.HiringEvents.Add(new HiringEvent
        {
            CandidateId = id, At = DateTime.UtcNow, Kind = "hire", By = user,
            Detail = Clip(string.Join(" · ", new[] { added.Count > 0 ? "Done: " + Labels(added) : null, removed.Count > 0 ? "Unticked: " + Labels(removed) : null }.OfType<string>()), 2000),
        });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "onboarding", c.Name, new { done = keys }, ct);
    }

    public async Task NotHiredAsync(int id, string? reason, string user, CancellationToken ct)
    {
        var db = Db;
        var c = await db.HiringCandidates.FirstAsync(x => x.Id == id, ct);
        c.NotHiredReason = Clip(reason, 500); c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await ChangeStageAsync(db, c, HiringStages.NotHired, user, c.NotHiredReason, ct);
    }

    public static List<string> OnboardingDone(HiringCandidate c) => (c.Onboarding ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

    // ---- Documents (scanned sheets, resumes, timesheet photos) ----

    public const long MaxDocumentBytes = 10 * 1024 * 1024;
    public static readonly string[] DocumentKinds = ["Interview sheet", "Resume", "Trial timesheet", "Other"];

    /// <summary>The candidate's files without their bytes.</summary>
    public async Task<List<DocumentInfo>> DocumentsAsync(int candidateId, CancellationToken ct) =>
        await Db.CandidateDocuments.AsNoTracking().Where(d => d.CandidateId == candidateId).OrderByDescending(d => d.UploadedAt)
            .Select(d => new DocumentInfo(d.Id, d.CandidateId, d.FileName, d.ContentType, d.Size, d.Kind, d.UploadedAt, d.UploadedBy)).ToListAsync(ct);

    /// <summary>How many files each candidate has (for the list), without reading any bytes.</summary>
    public async Task<Dictionary<int, int>> DocumentCountsAsync(CancellationToken ct) =>
        database.Available
            ? await Db.CandidateDocuments.AsNoTracking().GroupBy(d => d.CandidateId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct)
            : [];

    public async Task<CandidateDocument?> DocumentAsync(int id, CancellationToken ct) =>
        database.Available ? await Db.CandidateDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) : null;

    /// <summary>Saves an uploaded file. Returns an error message, or null when it worked. Only PDF, JPG, PNG and HEIC are kept.</summary>
    public async Task<string?> AddDocumentAsync(int candidateId, string fileName, byte[] data, string? kind, string user, CancellationToken ct)
    {
        if (data.Length == 0) return "That file is empty.";
        if (data.Length > MaxDocumentBytes) return "Files can be up to 10 MB.";
        var type = DocumentType(fileName, data);
        if (type is null) return "Only PDF, JPG, PNG or HEIC files can be uploaded.";
        var db = Db;
        if (!await db.HiringCandidates.AnyAsync(c => c.Id == candidateId, ct)) return "That person no longer exists.";
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray());
        var doc = new CandidateDocument
        {
            CandidateId = candidateId, FileName = Clip(string.IsNullOrWhiteSpace(name) ? "document" : name, 260)!, ContentType = type,
            Size = data.Length, Data = data, Kind = DocumentKinds.Contains(kind) ? kind! : "Other", UploadedAt = DateTime.UtcNow, UploadedBy = user,
        };
        db.CandidateDocuments.Add(doc);
        db.HiringEvents.Add(new HiringEvent { CandidateId = candidateId, At = doc.UploadedAt, Kind = "document", Detail = $"Uploaded {doc.Kind.ToLowerInvariant()}: {doc.FileName}", By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "upload", await NameAsync(db, candidateId, ct), new { doc.Id, doc.FileName, doc.Kind, doc.Size }, ct);
        return null;
    }

    public async Task DeleteDocumentAsync(int id, string user, CancellationToken ct)
    {
        var db = Db;
        var info = await db.CandidateDocuments.Where(d => d.Id == id).Select(d => new { d.CandidateId, d.FileName }).FirstOrDefaultAsync(ct);
        if (info is null) return;
        await db.CandidateDocuments.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        db.HiringEvents.Add(new HiringEvent { CandidateId = info.CandidateId, At = DateTime.UtcNow, Kind = "document", Detail = $"Deleted file: {info.FileName}", By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "delete-file", await NameAsync(db, info.CandidateId, ct), new { id, info.FileName }, ct);
    }

    /// <summary>The content type from the extension, checked against the file's first bytes; null when it isn't an allowed type.</summary>
    public static string? DocumentType(string fileName, byte[] data)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        bool Starts(params byte[] sig) => data.Length >= sig.Length && data.AsSpan(0, sig.Length).SequenceEqual(sig);
        // HEIC/HEIF: an ISO media box "ftyp" at byte 4 (iPhone camera photos).
        var isoMedia = data.Length >= 12 && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p';
        return ext switch
        {
            ".pdf" when Starts(0x25, 0x50, 0x44, 0x46) => "application/pdf",
            ".jpg" or ".jpeg" when Starts(0xFF, 0xD8, 0xFF) => "image/jpeg",
            ".png" when Starts(0x89, 0x50, 0x4E, 0x47) => "image/png",
            ".heic" or ".heif" when isoMedia => ext == ".heic" ? "image/heic" : "image/heif",
            _ => null,
        };
    }

    /// <summary>Images the browser can show as a thumbnail (HEIC only shows in Safari, so it gets an icon).</summary>
    public static bool CanPreview(string contentType) => contentType is "image/jpeg" or "image/png";

    // ---- Slots ----

    /// <summary>Every slot start from the day's start up to (not including) its end.</summary>
    public static List<TimeOnly> Slots(InterviewDay day)
    {
        var slots = new List<TimeOnly>();
        var step = Math.Max(5, day.SlotMinutes);
        for (var t = day.Start; t < day.End && slots.Count < 96; t = t.AddMinutes(step))
        {
            slots.Add(t);
            if (t.AddMinutes(step) < t) break; // wrapped past midnight
        }
        return slots;
    }

    /// <summary>
    /// English speakers early in the day, Spanish speakers later (from the Task Manual). A suggestion only:
    /// English or both → the first open slot; Spanish → the first open slot in the second half, else the last open one.
    /// </summary>
    public static TimeOnly? SuggestSlot(InterviewDay day, IEnumerable<InterviewBooking> bookings, string language)
    {
        var slots = Slots(day);
        var taken = bookings.Where(b => b.DayId == day.Id).Select(b => b.Time).ToHashSet();
        var open = slots.Where(s => !taken.Contains(s)).ToList();
        if (open.Count == 0) return null;
        if (language != "es") return open[0];
        var half = slots[slots.Count / 2];
        return open.FirstOrDefault(s => s >= half) is var later && later != default ? later : open[^1];
    }

    /// <summary>True for slots in the later half of the day (where Spanish speakers go).</summary>
    public static bool IsLaterHalf(InterviewDay day, TimeOnly slot)
    {
        var slots = Slots(day);
        return slots.Count > 1 && slot >= slots[slots.Count / 2];
    }

    /// <summary>The next Wednesday after today without an interview day already on it.</summary>
    public static DateOnly DefaultDate(IEnumerable<InterviewDay> days)
    {
        var taken = days.Select(d => d.Date).ToHashSet();
        var d = LocalClock.Today.AddDays(1);
        while (d.DayOfWeek != DayOfWeek.Wednesday || taken.Contains(d)) d = d.AddDays(1);
        return d;
    }

    /// <summary>Past 10:00am on interview day (or the day has gone by): unconfirmed people should come off the schedule.</summary>
    public static bool PastConfirmDeadline(InterviewDay day)
    {
        var now = LocalClock.Now;
        var today = DateOnly.FromDateTime(now);
        return day.Date < today || (day.Date == today && TimeOnly.FromDateTime(now) >= HiringLists.ConfirmBy);
    }

    public static string SlotLabel(TimeOnly t) => t.ToString("h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    // ---- Helpers ----

    private async Task ChangeStageAsync(DashboardDb db, HiringCandidate c, string stage, string user, string? note, CancellationToken ct)
    {
        var from = c.Stage;
        c.Stage = stage;
        c.StageAt = DateTime.UtcNow;
        c.UpdatedAt = c.StageAt;
        db.HiringEvents.Add(new HiringEvent { CandidateId = c.Id, At = c.StageAt, Kind = "stage", FromStage = from, ToStage = stage, Detail = Clip(note, 2000), By = user });
        await db.SaveChangesAsync(ct);
        await LogAsync(user, "stage", c.Name, new { from = HiringStages.Label(from), to = HiringStages.Label(stage), note }, ct);
    }

    private static async Task<string?> NameAsync(DashboardDb db, int candidateId, CancellationToken ct) =>
        await db.HiringCandidates.Where(c => c.Id == candidateId).Select(c => c.Name).FirstOrDefaultAsync(ct);

    private Task LogAsync(string user, string action, string? target, object? details, CancellationToken ct) =>
        changeLog.WriteAsync([ChangeLogWriter.Entry(user, Area, action, target, details, "Done")], ct);

    private static string? PhoneKey(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : null;
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
