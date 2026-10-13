using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;

namespace PurpleDashboard.Modules.Hiring;

/// <summary>
/// One-time import of past applicants from the old Interviews spreadsheets (2023 to 2025), read from SharePoint
/// into a JSON file (past-applicants.json) that someone uploads once on the Hiring page. The file has names and
/// phone numbers, so it's never kept in the repo.
///
/// Each person becomes one candidate in the "Past applicant" stage (same phone and first name = same person),
/// each interview row becomes a note in their history, and each interview date becomes an interview day with the
/// people who had a time booked. Rows from the old APPLICATION SPREADSHEET that describe an interview already on
/// an interview sheet ("sameEventAs") add their pay / rating / notes to that interview instead of a second one.
/// </summary>
public sealed partial class PastApplicantsImport(IServiceProvider services, ChangeLogWriter changeLog)
{
    public const string ImportedBy = "import:past-spreadsheets";

    public sealed record Entry(
        string Name, string? Phone, DateOnly? InterviewDate, string? Time, bool? English, bool? Confirmed,
        string? PositionExperience, string? Position, string? Notes, string? SourceFile, int? DupOf, int? SameEventAs);

    private sealed record FileShape(List<Entry> Applicants);

    public sealed record Result(int Candidates, int Interviews, int Days, int Bookings);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public async Task<bool> AlreadyImportedAsync(CancellationToken ct) =>
        await services.GetRequiredService<DashboardDb>().HiringCandidates.AnyAsync(c => c.CreatedBy == ImportedBy, ct);

    public static List<Entry> Parse(Stream json) =>
        JsonSerializer.Deserialize<FileShape>(json, Json)?.Applicants ?? throw new JsonException("No \"applicants\" list in the file.");

    public async Task<Result> ImportAsync(List<Entry> rows, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        if (await AlreadyImportedAsync(ct)) return new Result(0, 0, 0, 0);

        // Extra details from the application log go onto the interview they describe.
        var extra = new Dictionary<int, List<Entry>>();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].SameEventAs is { } same && same >= 0 && same < rows.Count && same != i)
                (extra.TryGetValue(same, out var l) ? l : extra[same] = []).Add(rows[i]);

        // Same person: the file links repeats by phone (dupOf); a shared phone with a different first name is someone else.
        var group = new int[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            group[i] = rows[i].DupOf is { } d && d >= 0 && d < i && FirstName(rows[d].Name) == FirstName(rows[i].Name) ? group[d] : i;

        var interviews = Enumerable.Range(0, rows.Count).Where(i => rows[i].SameEventAs is null).ToList();
        var now = DateTime.UtcNow;
        var candidates = new Dictionary<int, HiringCandidate>();
        foreach (var people in interviews.GroupBy(i => group[i]))
        {
            var ordered = people.OrderBy(i => rows[i].InterviewDate ?? DateOnly.MinValue).ToList();
            var latest = rows[ordered[^1]];
            var all = ordered.SelectMany(i => new[] { rows[i] }.Concat(extra.GetValueOrDefault(i) ?? [])).ToList();
            var english = all.Select(r => r.English).Where(e => e is not null).ToList();
            var lastDate = all.Select(r => r.InterviewDate).Where(d => d is not null).DefaultIfEmpty().Max();
            candidates[people.Key] = new HiringCandidate
            {
                Name = Trim(latest.Name, 200)!,
                Phone = Trim(all.Select(r => r.Phone).LastOrDefault(p => !string.IsNullOrWhiteSpace(p)), 40),
                Position = PositionKey(all.Select(r => r.Position).LastOrDefault(p => p is not null)) ?? "painter",
                Language = english.Count == 0 ? "en" : english.All(e => e == true) ? "en" : english.All(e => e == false) ? "es" : "both",
                Experience = Trim(all.Select(r => r.PositionExperience).LastOrDefault(p => !string.IsNullOrWhiteSpace(p)), 500),
                Stage = HiringStages.Past,
                StageAt = Utc(lastDate) ?? now,
                Rating = all.Select(r => Rating(r.Notes)).LastOrDefault(r => r is not null),
                InterviewedOn = lastDate,
                CreatedAt = now,
                CreatedBy = ImportedBy,
                UpdatedAt = now,
            };
        }
        db.HiringCandidates.AddRange(candidates.Values);
        await db.SaveChangesAsync(ct);

        // History: one note per interview row.
        foreach (var i in interviews)
        {
            var r = rows[i];
            var parts = new List<string?>
            {
                r.Time is { } t ? $"time {t}" : null,
                r.Confirmed switch { true => "confirmed", false => "not confirmed", _ => null },
                r.English switch { true => "English", false => "Spanish", _ => null },
                r.PositionExperience, r.Notes,
            };
            parts.AddRange((extra.GetValueOrDefault(i) ?? []).Select(x => x.Notes));
            db.HiringEvents.Add(new HiringEvent
            {
                CandidateId = candidates[group[i]].Id,
                At = Utc(r.InterviewDate) ?? now,
                Kind = "note",
                Detail = Trim($"Interview list {(r.InterviewDate is { } d ? d.ToString("M/d/yyyy", CultureInfo.InvariantCulture) : "(no date)")}: "
                    + string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct()) + (r.SourceFile is { } f ? $" ({f})" : ""), 2000),
                By = ImportedBy,
            });
        }

        // Interview days, with a booking for everyone who had a time.
        int dayCount = 0, bookingCount = 0;
        foreach (var date in interviews.GroupBy(i => rows[i].InterviewDate).Where(g => g.Key is not null).OrderBy(g => g.Key))
        {
            var timed = date.Select(i => (i, Time: ParseTime(rows[i].Time))).Where(x => x.Time is not null).ToList();
            var start = timed.Count > 0 ? timed.Min(x => x.Time!.Value) : HiringLists.DefaultStart;
            var end = timed.Count > 0 ? timed.Max(x => x.Time!.Value).AddMinutes(HiringLists.DefaultSlotMinutes) : HiringLists.DefaultEnd;
            var day = new InterviewDay
            {
                Date = date.Key!.Value,
                Location = HiringLists.DefaultLocation,
                Start = start, End = end < start ? HiringLists.DefaultEnd : end,
                SlotMinutes = HiringLists.DefaultSlotMinutes,
                Notes = "Imported from " + string.Join(", ", date.Select(i => rows[i].SourceFile).Where(f => f is not null).Distinct()),
                CreatedAt = now, CreatedBy = ImportedBy,
            };
            db.InterviewDays.Add(day);
            await db.SaveChangesAsync(ct);
            dayCount++;

            var takenTimes = new HashSet<TimeOnly>();
            var takenPeople = new HashSet<int>();
            foreach (var (i, time) in timed.OrderBy(x => x.Time))
            {
                var candidateId = candidates[group[i]].Id;
                if (!takenTimes.Add(time!.Value) || !takenPeople.Add(candidateId)) continue;
                db.InterviewBookings.Add(new InterviewBooking
                {
                    DayId = day.Id, CandidateId = candidateId, Time = time.Value,
                    ConfirmedAt = rows[i].Confirmed == true ? Utc(day.Date) : null,
                    ConfirmedBy = rows[i].Confirmed == true ? ImportedBy : null,
                    CreatedAt = now, CreatedBy = ImportedBy,
                });
                bookingCount++;
            }
        }
        await db.SaveChangesAsync(ct);

        var result = new Result(candidates.Count, interviews.Count, dayCount, bookingCount);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, HiringService.Area, "import-past", "Past applicants (2023 to 2025 spreadsheets)",
            new { result.Candidates, result.Interviews, result.Days, result.Bookings }, "Done")], ct);
        return result;
    }

    private static string FirstName(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";

    private static string? PositionKey(string? position) => position switch
    {
        "Helper" => "helper", "Painter" => "painter", "Foreman" => "foreman", "Sales Rep" => "sales", _ => null,
    };

    /// <summary>The old application log rated 1 to 10 ("Rating: 7"); the dashboard uses 1 to 5 stars.</summary>
    private static int? Rating(string? notes) =>
        notes is not null && RatingPattern().Match(notes) is { Success: true } m && decimal.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var r)
            ? (int)Math.Clamp(Math.Round(r > 5 ? r / 2 : r, MidpointRounding.AwayFromZero), 1, 5)
            : null;

    [GeneratedRegex(@"Rating:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RatingPattern();

    private static TimeOnly? ParseTime(string? time) =>
        TimeOnly.TryParseExact(time ?? "", ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>Noon Pacific on that day, so it lands on the right date everywhere.</summary>
    private static DateTime? Utc(DateOnly? date) => date is { } d ? LocalClock.StartOfDayUtc(d).AddHours(12).UtcDateTime : null;

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}
