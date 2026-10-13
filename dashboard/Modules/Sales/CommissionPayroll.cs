using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.Financials;
using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Modules.Sales;

/// <summary>
/// A change to a paid &amp; locked commission month: what the person earns on that month's jobs now (or at their next
/// lock) minus what it was when locked. It's already in their owed (owed = earned − paid); shown in the next open month.
/// </summary>
public sealed record CommissionAdjustment(DateOnly LockedMonth, decimal Amount, IReadOnlyList<string> Jobs);

/// <summary>
/// One salesperson's month on the payroll checks view. OwedNow = what the dashboard says they're owed today (all jobs,
/// after unallocated payments); Recorded = commission payments recorded with a date in the month; Payroll = gross of
/// their QuickBooks paycheck lines on the commission account (6490) dated in the month.
/// </summary>
public sealed record CommissionCheckRow(
    string Name, decimal OwedNow, decimal Recorded, decimal Payroll, IReadOnlyList<PayrollLine> Checks, PayLock? Lock,
    IReadOnlyList<CommissionPayment> Payments, CommissionAdjustment? Adjustment)
{
    public const decimal Tolerance = 1m;

    /// <summary>"Paid &amp; locked", "Matches", "Check differs", "Not recorded" (check, nothing recorded), "Not in payroll" (recorded, no check), "Owed", "Nothing this month".</summary>
    public string Status =>
        Lock is not null ? "Paid & locked"
        : Payroll != 0 && Recorded != 0 ? (Math.Abs(Payroll - Recorded) < Tolerance ? "Matches" : "Check differs")
        : Payroll != 0 ? "Not recorded"
        : Recorded != 0 ? "Not in payroll"
        : OwedNow >= Tolerance ? "Owed" : "Nothing this month";

    public string Chip => Status switch { "Paid & locked" or "Matches" => "good", "Nothing this month" or "Owed" => "", _ => "warn" };

    public string? Detail => Status switch
    {
        "Check differs" => Payroll > Recorded ? $"Payroll paid {Format.Money(Payroll - Recorded)} more than recorded" : $"Payroll paid {Format.Money(Recorded - Payroll)} less than recorded",
        "Not recorded" => "Paid in payroll, not recorded in the dashboard yet",
        "Not in payroll" => "Recorded here, but no Commission paycheck line this month",
        "Owed" => "Nothing paid this month",
        _ => null,
    };

    /// <summary>Paid in payroll but not recorded here (what "Record from payroll" records).</summary>
    public decimal Unrecorded => Payroll - Recorded >= Tolerance ? Math.Round(Payroll - Recorded, 2) : 0;
    public bool CanLock => Lock is null && (Payroll != 0 || Recorded != 0);
    /// <summary>"Lock month" locks only people whose check matches what's recorded.</summary>
    public bool ReadyToLock => CanLock && Status == "Matches";
}

public sealed record CommissionPayrollView(DateOnly Month, PayrollView Payroll, IReadOnlyList<CommissionCheckRow> Rows);

/// <summary>
/// Commissions against payroll, the same way as bonuses (Financials → Bonuses → Payroll checks). The QuickBooks payroll
/// "Commission" pay item posts to 6490 Sales Commissions Overhead (setting Sales:CommissionAccounts); each month's
/// paycheck lines there (<see cref="PayrollPayments"/>, read only) are matched to salespeople by QuickBooks employee id,
/// then name, and compared with the commission payments recorded in the dashboard for the month.
/// "Record from payroll" records the difference as payments, spread over the person's owed jobs oldest first (by
/// JobTread job number), anything left as an unallocated payment (JobId "unallocated") that lowers their owed total.
/// "Paid &amp; locked" (PayLocks, Kind "commission") snapshots the person's jobs for the month; payments dated in a locked
/// month can't be added or deleted, and later changes to what those jobs earn show in the next open month as an adjustment.
/// </summary>
public sealed class CommissionPayroll(PayrollPayments payroll, PeopleService people, IServiceProvider services, DatabaseStatus database, ChangeLogWriter changeLog)
{
    public const string Kind = "commission";
    /// <summary>JobId of a payment not tied to a job (payroll paid more than the person's owed jobs).</summary>
    public const string Unallocated = "unallocated";
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public sealed record LockLine(string JobId, string? JobName, decimal Earned, decimal PaidInMonth);
    public sealed record LockSnapshot(List<LockLine> Lines);

    public static string Key(DateOnly month) => month.ToString("yyyy-MM", Us);
    public static string Label(DateOnly month) => month.ToString("MMM yyyy", Us);
    public static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>Months to pick from: the last 12, newest first.</summary>
    public static IReadOnlyList<DateOnly> Months()
    {
        var now = MonthOf(LocalClock.Today);
        return Enumerable.Range(0, 12).Select(i => now.AddMonths(-i)).ToList();
    }

    public static DateOnly ParseMonth(string? m) =>
        m is not null && DateOnly.TryParseExact(m + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && Months().Contains(d)
            ? d : MonthOf(LocalClock.Today).AddMonths(-1);

    private DashboardDb Db => services.GetRequiredService<DashboardDb>();

    public async Task<List<PayLock>> LocksAsync(CancellationToken ct) =>
        database.Available ? await Db.PayLocks.AsNoTracking().Where(l => l.Kind == Kind).ToListAsync(ct) : [];

    public async Task<List<CommissionPayment>> PaymentsInAsync(DateOnly month, CancellationToken ct)
    {
        if (!database.Available) return [];
        var end = month.AddMonths(1).AddDays(-1);
        return await Db.CommissionPayments.AsNoTracking().Where(p => p.PaidOn >= month && p.PaidOn <= end).OrderBy(p => p.PaidOn).ThenBy(p => p.Id).ToListAsync(ct);
    }

    public async Task<bool> IsLockedAsync(string person, DateOnly date, CancellationToken ct)
    {
        if (!database.Available) return false;
        var key = Key(MonthOf(date));
        return await Db.PayLocks.AnyAsync(l => l.Kind == Kind && l.Person == person && l.Month == key, ct);
    }

    /// <summary>The month's rows: everyone owed now, with a check, a recorded payment, a lock or an adjustment in the month.</summary>
    public async Task<CommissionPayrollView> GetAsync(DateOnly month, IReadOnlyList<JobCommission> jobs, IReadOnlyList<PersonCommissions> byPerson, CancellationToken ct)
    {
        var end = month.AddMonths(1).AddDays(-1);
        var view = await payroll.GetAsync(payroll.AccountsFrom(PayrollPayments.CommissionSetting, PayrollPayments.CommissionDefault), month, end, "Commission", PayrollPayments.CommissionSetting, ct);
        var staff = await people.GetStaffAsync(ct);
        var paid = await PaymentsInAsync(month, ct);
        var locks = await LocksAsync(ct);
        var known = byPerson.Select(p => p.Person).Concat(paid.Select(p => p.Person)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mapped = view.Lines.Select(l => (Line: l, Person: PayrollPayments.MatchPerson(l, staff, known) ?? l.Name)).ToList();
        var adjustments = Adjustments(locks, jobs, month);
        static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        var names = byPerson.Where(p => p.Owed >= CommissionCheckRow.Tolerance).Select(p => p.Person)
            .Concat(mapped.Select(m => m.Person)).Concat(paid.Select(p => p.Person))
            .Concat(locks.Where(l => l.Month == Key(month)).Select(l => l.Person)).Concat(adjustments.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var rows = names.Select(n =>
        {
            var checks = mapped.Where(m => Same(m.Person, n)).Select(m => m.Line).ToList();
            var pays = paid.Where(p => Same(p.Person, n)).ToList();
            return new CommissionCheckRow(n, byPerson.FirstOrDefault(p => Same(p.Person, n))?.Owed ?? 0, pays.Sum(p => p.Amount), checks.Sum(c => c.Amount), checks,
                locks.FirstOrDefault(l => l.Month == Key(month) && Same(l.Person, n)), pays, adjustments.GetValueOrDefault(n));
        }).OrderBy(r => r.Chip == "warn" ? 0 : r.Chip == "" ? 1 : 2).ThenBy(r => r.Name).ToList();
        return new CommissionPayrollView(month, view, rows);
    }

    /// <summary>
    /// Adjustments landing in <paramref name="month"/>: for each lock, what that month's jobs earn now (or at the person's
    /// next lock) minus the snapshot, shown in the first unlocked month after it.
    /// </summary>
    private static Dictionary<string, CommissionAdjustment> Adjustments(IReadOnlyList<PayLock> locks, IReadOnlyList<JobCommission> jobs, DateOnly month)
    {
        var result = new Dictionary<string, CommissionAdjustment>(StringComparer.OrdinalIgnoreCase);
        foreach (var personLocks in locks.GroupBy(l => l.Person, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = personLocks.OrderBy(l => l.Month, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var l = ordered[i];
                var lockedMonth = DateOnly.ParseExact(l.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var target = lockedMonth.AddMonths(1);
                while (ordered.Any(o => o.Month == Key(target))) target = target.AddMonths(1);
                if (target != month || Snapshot(l) is not { } snap) continue;
                var later = ordered.Skip(i + 1).Select(Snapshot).FirstOrDefault(s => s is not null);
                var changed = new List<string>();
                var diff = 0m;
                foreach (var line in snap.Lines.Where(x => x.JobId != Unallocated))
                {
                    decimal? now = later is not null
                        ? later.Lines.FirstOrDefault(x => x.JobId == line.JobId)?.Earned
                        : jobs.FirstOrDefault(j => j.JobId == line.JobId && j.Person.Equals(l.Person, StringComparison.OrdinalIgnoreCase))?.Earned;
                    if (now is not { } e || Math.Abs(e - line.Earned) < 0.005m) continue;
                    diff += e - line.Earned;
                    changed.Add(line.JobName ?? line.JobId);
                }
                if (Math.Abs(diff) >= 0.005m) result[l.Person] = new CommissionAdjustment(lockedMonth, Math.Round(diff, 2), changed);
            }
        }
        return result;
    }

    private static LockSnapshot? Snapshot(PayLock l)
    {
        try { return JsonSerializer.Deserialize<LockSnapshot>(l.LinesJson); } catch (JsonException) { return null; }
    }

    /// <summary>
    /// Records <paramref name="amount"/> paid through payroll: spread over the person's owed jobs oldest first (JobTread job
    /// number), anything left over as one unallocated payment. Returns the payments made.
    /// </summary>
    public async Task<List<CommissionPayment>> RecordFromPayrollAsync(string person, decimal amount, DateOnly paidOn, string? refs, IReadOnlyList<JobCommission> jobs, string user, CancellationToken ct)
    {
        var db = Db;
        var note = $"From payroll {paidOn:M/d/yyyy}{(string.IsNullOrWhiteSpace(refs) ? "" : " check " + refs)}";
        var left = Math.Round(amount, 2);
        var made = new List<CommissionPayment>();
        foreach (var j in jobs.Where(j => j.Person.Equals(person, StringComparison.OrdinalIgnoreCase) && j.Owed >= 0.01m)
                     .OrderBy(j => long.TryParse(j.Number, out var n) ? n : long.MaxValue).ThenBy(j => j.JobName))
        {
            if (left < 0.01m) break;
            var take = Math.Min(left, j.Owed);
            made.Add(new CommissionPayment { JobId = j.JobId, JobName = j.JobName, Person = person, Amount = take, PaidOn = paidOn, Note = note, CreatedAt = DateTime.UtcNow, CreatedBy = user });
            left -= take;
        }
        if (left >= 0.01m || made.Count == 0)
            made.Add(new CommissionPayment { JobId = Unallocated, JobName = "Unallocated (payroll)", Person = person, Amount = left, PaidOn = paidOn, Note = note, CreatedAt = DateTime.UtcNow, CreatedBy = user });
        db.CommissionPayments.AddRange(made);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "commissions", "pay-from-payroll", person, new
        {
            amount = Math.Round(amount, 2), paidOn, check = refs,
            jobs = made.Where(p => p.JobId != Unallocated).Select(p => new { job = p.JobName, amount = p.Amount }).ToList(),
            unallocated = made.Where(p => p.JobId == Unallocated).Sum(p => p.Amount),
        }, "Done")], ct);
        return made;
    }

    /// <summary>Locks a person's month: snapshots what each of their jobs earns and what was paid on it in the month.</summary>
    public async Task<PayLock> LockAsync(CommissionCheckRow row, DateOnly month, IReadOnlyList<JobCommission> jobs, IReadOnlyList<CommissionPayment> paidInMonth, string user, CancellationToken ct)
    {
        var db = Db;
        var key = Key(month);
        var existing = await db.PayLocks.FirstOrDefaultAsync(l => l.Kind == Kind && l.Person == row.Name && l.Month == key, ct);
        if (existing is not null) return existing;
        bool Mine(string p) => p.Equals(row.Name, StringComparison.OrdinalIgnoreCase);
        var mine = paidInMonth.Where(p => Mine(p.Person)).ToList();
        var lines = jobs.Where(j => Mine(j.Person)).Select(j => new LockLine(j.JobId, j.JobName, j.Earned, mine.Where(p => p.JobId == j.JobId).Sum(p => p.Amount))).ToList();
        if (mine.Where(p => p.JobId == Unallocated).Sum(p => p.Amount) is var un and not 0) lines.Add(new LockLine(Unallocated, "Unallocated (payroll)", 0, un));
        var checks = row.Checks.OrderByDescending(c => c.Date).ToList();
        var lk = new PayLock
        {
            Kind = Kind, Person = row.Name, Month = key, LinesJson = JsonSerializer.Serialize(new LockSnapshot(lines)),
            Net = mine.Sum(p => p.Amount), Owed = row.OwedNow,
            CheckNum = checks.Count == 0 ? null : string.Join(", ", checks.Select(c => c.Num).Where(n => n is not null)) is { Length: > 0 } nums ? nums : null,
            CheckDate = checks.FirstOrDefault()?.Date, Gross = row.Payroll != 0 ? row.Payroll : null, LockedAt = DateTime.UtcNow, LockedBy = user,
        };
        db.PayLocks.Add(lk);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "commissions", "lock", row.Name, new { month = key, paid = lk.Net, owed = lk.Owed, check = lk.CheckNum, gross = lk.Gross }, "Done")], ct);
        return lk;
    }

    public async Task<PayLock?> UnlockAsync(string person, DateOnly month, string reason, string user, CancellationToken ct)
    {
        var db = Db;
        var key = Key(month);
        var row = await db.PayLocks.FirstOrDefaultAsync(l => l.Kind == Kind && l.Person == person && l.Month == key, ct);
        if (row is null) return null;
        db.PayLocks.Remove(row);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "commissions", "unlock", person, new { month = key, reason = reason.Trim(), paid = row.Net }, "Done")], ct);
        return row;
    }
}
