using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Changes;
using PurpleDashboard.Modules.Financials;
using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Pages.Financials;

/// <summary>
/// Monthly bonuses: each person's year as of a month (reconcile), payments, payroll checks from QuickBooks and
/// Paid &amp; locked, jobs by month with clawbacks, and effort ratings / eligibility.
/// </summary>
public class BonusesModel(
    BonusService bonuses, PayrollPayments payroll, PeopleService people, FinancialsService financials,
    QuickBooksClient quickBooks, DatabaseStatus database, Permissions permissions, CurrentUser me, ILogger<BonusesModel> logger) : PageModel
{
    /// <summary>"yyyy-MM"; default last month (bonuses are paid for the month just finished).</summary>
    [BindProperty(SupportsGet = true)] public string? Month { get; set; }
    /// <summary>"reconcile" (default), "jobs", "effort".</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>Row to keep open after a save: "rec-{person}", "pay-{person}", "chk-{person}", "job-{id}", "ex-{person}", "rate".</summary>
    [BindProperty(SupportsGet = true)] public string? Open { get; set; }
    /// <summary>Jobs tab: show every job, not just the month's.</summary>
    [BindProperty(SupportsGet = true)] public bool All { get; set; }
    /// <summary>Reconcile: show just this person (link from Employees).</summary>
    [BindProperty(SupportsGet = true)] public string? Only { get; set; }

    public string CurrentTab => !CanView ? "effort" : Tab is "jobs" or "effort" ? Tab : "reconcile";
    public DateOnly Selected { get; private set; }
    public BonusLedger? Ledger { get; private set; }
    public List<PersonReconcile> Rows { get; private set; } = [];
    public PayrollView? Payroll { get; private set; }
    public List<PayrollRow> PayrollRows { get; private set; } = [];
    public IReadOnlyList<StaffMember> Staff { get; private set; } = [];
    public BonusOptions Rules => bonuses.Rules;
    public bool DbAvailable => database.Available;
    /// <summary>Payments, locks, clawbacks, adjustments, retention, exclusions (bonuses.edit).</summary>
    public bool CanEdit => database.Available && permissions.Can(Perm.BonusesEdit);
    /// <summary>Effort ratings (bonuses.rate: supervisors).</summary>
    public bool CanRate => database.Available && permissions.Can(Perm.BonusesRate);
    /// <summary>Everyone's bonus money (bonuses.view). Raters without it only see the effort screen.</summary>
    public bool CanView => permissions.Can(Perm.BonusesView);
    public string RaterName => me.DisplayName ?? me.Email ?? "you";
    public string? Error { get; private set; }
    [TempData] public string? Message { get; set; }
    /// <summary>QuickBooks net profit for the month (null when QuickBooks isn't connected).</summary>
    public decimal? CompanyNet { get; private set; }
    public bool Held => Rules.HoldWhenCompanyLoses && CompanyNet < 0;
    public DateTime? QbAsOf => QuickBooksClient.DataAsOf;

    public IReadOnlyList<DateOnly> Months => Ledger?.Months ?? bonuses.AllMonths();
    public static string Key(DateOnly m) => BonusService.Key(m);
    public static string Label(DateOnly m) => BonusService.Label(m);
    public string RaterKey => me.Email ?? me.DisplayName ?? "unknown";
    /// <summary>Raters are stored by email; this shows them by name.</summary>
    public PersonNames RaterNames => raterNames ??= new PersonNames(me.Email, me.DisplayName, new Dictionary<string, string>(), Staff);
    private PersonNames? raterNames;
    public decimal PayableNow(PersonReconcile r) => Held ? 0 : r.Owed;
    public decimal ClawbackTotal(BonusClawback c) => bonuses.ClawbackTotal(c);

    /// <summary>The month whose payroll pays the selected month (bonuses for August are paid with September's bonus run).</summary>
    public DateOnly PayMonth => Selected.AddMonths(1);

    /// <summary>
    /// Settling one person's month: Due = owed at the end of the selected month (in December, plus the year-end
    /// settlement); Recorded = payments dated in the next month; Payroll = their Bonus Only checks dated in the next month.
    /// </summary>
    public sealed record PayrollRow(string Name, decimal Due, decimal Recorded, decimal Payroll, IReadOnlyList<PayrollLine> Checks, PayLock? Lock, IReadOnlyList<BonusPayment> Payments)
    {
        public decimal Remaining => Math.Max(0, Due - Recorded);
        public string Status =>
            Lock is not null ? "Paid & locked"
            : Payroll != 0 ? (Math.Abs(Payroll - Due) < 1 ? "Check found: matches" : $"Check found: differs ({Format.Money(Payroll)} vs {Format.Money(Due)} due)")
            : Recorded != 0 ? "Not in payroll"
            : Due >= 1 ? "Owed" : "Nothing due";
        public string Chip => Status switch { "Paid & locked" or "Check found: matches" => "good", "Nothing due" => "", _ => "warn" };
        public string? RecordNote => Payroll == 0 || Lock is not null ? null
            : Recorded == 0 ? "Not recorded in the dashboard yet"
            : Math.Abs(Payroll - Recorded) < 1 ? "Recorded here" : Payroll > Recorded ? $"Payroll paid {Format.Money(Payroll - Recorded)} more than recorded" : $"Payroll paid {Format.Money(Recorded - Payroll)} less than recorded";
        public decimal Unrecorded => Payroll - Recorded >= 1 ? Payroll - Recorded : 0;
        public bool CanLock => Lock is null && (Payroll != 0 || Recorded != 0);
        /// <summary>Locked by "Lock month": the check matches what was due, or it's fully paid by recorded payments.</summary>
        public bool ReadyToLock => CanLock && (Status == "Check found: matches" || (Payroll == 0 && Due >= 0.01m && Remaining < 1));
    }

    public async Task OnGetAsync(bool refresh, bool qbRefresh, CancellationToken ct)
    {
        ViewData["Title"] = "Bonuses";
        ViewData["ModuleName"] = "Financials";
        if (qbRefresh) QuickBooksClient.ClearCache();
        await bonuses.LoadRulesAsync(ct);
        Selected = ParseMonth(Month);
        try
        {
            Ledger = await bonuses.GetAsync(refresh, ct);
            Staff = await people.GetStaffAsync(ct);
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading bonuses failed");
            Error = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return;
        }
        Rows = Ledger.Reconcile(Selected);
        if (Only is not null) Rows = Rows.Where(r => r.Name.Equals(Only, StringComparison.OrdinalIgnoreCase)).ToList();

        if (CurrentTab == "reconcile")
        {
            await LoadCompanyNetAsync(ct);
            await LoadPayrollAsync(ct);
        }
    }

    private async Task LoadCompanyNetAsync(CancellationToken ct)
    {
        try
        {
            if (await quickBooks.GetConnectionAsync(ct) is null) return;
            var report = await financials.GetAsync(Selected, Selected.AddMonths(1).AddDays(-1), "Accrual", false, ct);
            CompanyNet = report.Total.QbNet;
        }
        catch (Exception e) when (e is QuickBooksException or JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "QuickBooks net profit for bonuses failed");
        }
    }

    /// <summary>What settles the selected month: owed at its end (plus the year-end settlement in December).</summary>
    public decimal Due(PersonReconcile p) => Held ? 0 : Selected.Month == 12 ? Math.Max(0, p.Balance + p.Held) : p.Owed;

    private async Task LoadPayrollAsync(CancellationToken ct)
    {
        var end = PayMonth.AddMonths(1).AddDays(-1);
        var accounts = Rules.BonusAccounts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Payroll = await payroll.GetAsync(accounts.Length > 0 ? accounts : [PayrollPayments.BonusDefault], PayMonth, end, "Bonus", "Bonus rules → bonus account (Settings)", ct);
        var known = Rows.Select(r => r.Name).ToList();
        var mapped = Payroll.Lines.Select(l => (Line: l, Person: PayrollPayments.MatchPerson(l, Staff, known) ?? l.Name)).ToList();
        var paidNext = Ledger!.Payments.Where(p => p.PaidOn >= PayMonth && p.PaidOn <= end).ToList();
        var names = Rows.Select(r => r.Name).Concat(mapped.Select(m => m.Person)).Concat(paidNext.Select(p => p.Person)).Distinct(StringComparer.OrdinalIgnoreCase);
        PayrollRows = names.Select(n =>
        {
            var row = Rows.FirstOrDefault(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            var checks = mapped.Where(m => m.Person.Equals(n, StringComparison.OrdinalIgnoreCase)).Select(m => m.Line).ToList();
            var pays = paidNext.Where(p => p.Person.Equals(n, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.PaidOn).ThenBy(p => p.Id).ToList();
            return new PayrollRow(n, row is null ? 0 : Due(row), pays.Sum(p => p.Amount), checks.Sum(c => c.Amount), checks, Ledger.LockFor(n, Selected), pays);
        }).Where(r => r.Payroll != 0 || r.Recorded != 0 || r.Due >= 0.01m || r.Lock is not null)
          .OrderBy(r => r.Chip == "good" ? 1 : 0).ThenBy(r => r.Name).ToList();
    }

    public DateOnly ParseMonth(string? m)
    {
        var all = bonuses.AllMonths();
        if (m is not null && DateOnly.TryParseExact(m + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && all.Contains(d)) return d;
        var last = BonusService.CurrentMonth.AddMonths(-1);
        return all.Contains(last) ? last : all[^1];
    }

    public static string Slug(string name) => new(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private async Task<bool> StartAsync(CancellationToken ct)
    {
        await bonuses.LoadRulesAsync(ct);
        Selected = ParseMonth(Month);
        return CanEdit;
    }

    // ---- Payments ----

    public async Task<IActionResult> OnPostPayAsync(string person, string? amount, DateOnly? paidOn, string? covers, string? note, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("reconcile", null);
        if (!TryMoney(amount, out var a) || a == 0) { Message = "Enter the amount paid."; return Back("reconcile", "pay-" + Slug(person)); }
        var on = paidOn ?? DefaultPaidOn(PayMonth);
        await bonuses.AddPaymentAsync(person, a, on, covers, note, "manual", User(), ct);
        var settles = new DateOnly(on.Year, on.Month, 1).AddMonths(-1);
        Message = $"Recorded {Format.Money(a)} paid to {person} on {Format.Date(on)}."
            + (settles != Selected ? $" A payment dated {Format.Date(on)} settles {Label(settles)}, so it shows there; showing {Label(settles)}." : "");
        if (bonuses.AllMonths().Contains(settles)) Month = Key(settles);
        return Back("reconcile", "pay-" + Slug(person));
    }

    public async Task<IActionResult> OnPostPayFromPayrollAsync(string person, string? amount, DateOnly paidOn, string? refs, CancellationToken ct)
    {
        if (!await StartAsync(ct) || !TryMoney(amount, out var a) || a == 0) return Back("reconcile", null);
        await bonuses.AddPaymentAsync(person, a, paidOn, Label(Selected) + " bonus", $"From payroll {paidOn:M/d/yyyy}{(string.IsNullOrWhiteSpace(refs) ? "" : " check " + refs)}", "payroll", User(), ct);
        Message = $"Recorded {Format.Money(a)} for {person} from payroll{(string.IsNullOrWhiteSpace(refs) ? "" : " (check " + refs + ")")}.";
        return Back("reconcile", "chk-" + Slug(person));
    }

    public async Task<IActionResult> OnPostDeletePaymentAsync(int id, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("reconcile", null);
        var p = await bonuses.DeletePaymentAsync(id, User(), ct);
        Message = p is null ? "That payment was already deleted." : $"Deleted the {Format.Money(p.Amount)} payment to {p.Person} ({Format.Date(p.PaidOn)}).";
        return Back("reconcile", Open);
    }

    // ---- Paid & locked ----

    public async Task<IActionResult> OnPostLockAsync(string? person, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("reconcile", null);
        var ledger = await bonuses.GetAsync(false, ct);
        Staff = await people.GetStaffAsync(ct);
        Ledger = ledger;
        Rows = ledger.Reconcile(Selected);
        await LoadPayrollAsync(ct);
        var targets = PayrollRows.Where(r => person is null ? r.ReadyToLock : r.CanLock && r.Name == person).ToList();
        if (person is not null && targets.Count == 0) { Message = $"Nothing to lock for {person} in {Label(Selected)} (no payment recorded or payroll check found)."; return Back("reconcile", "chk-" + Slug(person)); }
        // A check that isn't recorded here yet is recorded as the payment first, so the dashboard and payroll agree.
        var recorded = 0;
        foreach (var t in targets.Where(t => t.Unrecorded > 0))
        {
            var last = t.Checks.OrderByDescending(c => c.Date).First();
            var refs = string.Join(", ", t.Checks.Select(x => x.Num).Where(n => n is not null));
            await bonuses.AddPaymentAsync(t.Name, t.Unrecorded, last.Date, Label(Selected) + " bonus", $"From payroll {last.Date:M/d/yyyy}{(refs.Length > 0 ? " check " + refs : "")}", "payroll", User(), ct);
            recorded++;
        }
        if (recorded > 0) ledger = await bonuses.GetAsync(false, ct);
        foreach (var t in targets)
            await bonuses.LockAsync(ledger, t.Name, Selected, t.Checks.OrderByDescending(c => c.Date).FirstOrDefault() is { } c ? c with { Num = string.Join(", ", t.Checks.Select(x => x.Num).Where(n => n is not null)) } : null,
                t.Payroll != 0 ? t.Payroll : null, User(), ct);
        Message = person is not null ? $"{person}'s {Label(Selected)} bonus is paid & locked." : targets.Count == 0 ? $"No one in {Label(Selected)} is ready to lock (a matching check, or a recorded payment with nothing left owed)." : $"Locked {targets.Count} {(targets.Count == 1 ? "person" : "people")} for {Label(Selected)}.";
        return Back("reconcile", person is null ? "checks" : "chk-" + Slug(person));
    }

    public async Task<IActionResult> OnPostUnlockAsync(string person, string? reason, CancellationToken ct)
    {
        if (!await StartAsync(ct)) { Message = "You can't unlock a paid month."; return Back("reconcile", "chk-" + Slug(person)); }
        if (string.IsNullOrWhiteSpace(reason)) { Message = "Say why you're unlocking it."; return Back("reconcile", "chk-" + Slug(person)); }
        await bonuses.UnlockAsync(person, Selected, reason, User(), ct);
        Message = $"Unlocked {person}'s {Label(Selected)} bonus: it's worked out from the current figures again.";
        return Back("reconcile", "chk-" + Slug(person));
    }

    // ---- Adjustments and retention (admin) ----

    public async Task<IActionResult> OnPostAdjustAsync(string person, string? amount, string? reason, CancellationToken ct)
    {
        if (!await StartAsync(ct)) { Message = "You can't adjust bonuses."; return Back("reconcile", "rec-" + Slug(person)); }
        if (!TryMoney(amount, out var a) || a == 0) { Message = "Enter the adjustment (negative takes away)."; return Back("reconcile", "rec-" + Slug(person)); }
        if (string.IsNullOrWhiteSpace(reason)) { Message = "Say why (reason is required)."; return Back("reconcile", "rec-" + Slug(person)); }
        await bonuses.AddAdjustmentAsync(person, Selected, a, reason, User(), ct);
        Message = $"Added a {Format.Money(a)} adjustment to {person}'s {Selected.Year} bonus ({Label(Selected)}).";
        return Back("reconcile", "rec-" + Slug(person));
    }

    public async Task<IActionResult> OnPostDeleteAdjustmentAsync(int id, string person, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("reconcile", "rec-" + Slug(person));
        var a = await bonuses.DeleteAdjustmentAsync(id, User(), ct);
        Message = a is null ? "That adjustment was already deleted." : $"Deleted the {Format.Money(a.Amount)} adjustment ({a.Reason}).";
        return Back("reconcile", "rec-" + Slug(person));
    }

    public async Task<IActionResult> OnPostReleaseAsync(string? person, string? reason, CancellationToken ct)
    {
        if (!await StartAsync(ct)) { Message = "You can't release retention."; return Back("reconcile", null); }
        await bonuses.ReleaseRetentionAsync(Selected, person, reason, User(), ct);
        Message = $"Released retention held for {person ?? "everyone"} in {Label(Selected)}.";
        return Back("reconcile", person is null ? null : "rec-" + Slug(person));
    }

    public async Task<IActionResult> OnPostUndoReleaseAsync(int id, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("reconcile", null);
        var r = await bonuses.UndoReleaseAsync(id, User(), ct);
        Message = r is null ? "Already undone." : $"Undid the early retention release for {r.Person ?? "everyone"}.";
        return Back("reconcile", Open);
    }

    // ---- Clawbacks ----

    public async Task<IActionResult> OnPostClawbackAsync(int? id, string jobId, string? jobName, string? clawMonth, string? kind, string? pool, string? amount, string? reason, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("jobs", "job-" + jobId);
        if (!TryMoney(amount, out var a) || a <= 0) { Message = "Enter the clawback amount."; return Back("jobs", "job-" + jobId); }
        if (string.IsNullOrWhiteSpace(reason)) { Message = "Say why (e.g. callback repaint not in JobTread)."; return Back("jobs", "job-" + jobId); }
        var m = ParseMonth(clawMonth);
        var c = await bonuses.SaveClawbackAsync(id, jobId, jobName, m, kind ?? "bonus", pool ?? "both", a, reason, User(), ct);
        Message = $"{(id is null ? "Added" : "Saved")} a clawback of {Format.Money(bonuses.ClawbackTotal(c))} in bonus on {jobName} ({Label(m)}).";
        return Back("jobs", "job-" + jobId);
    }

    public async Task<IActionResult> OnPostDeleteClawbackAsync(int id, string jobId, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("jobs", "job-" + jobId);
        var c = await bonuses.DeleteClawbackAsync(id, User(), ct);
        Message = c is null ? "That clawback was already deleted." : $"Deleted the clawback on {c.JobName} ({c.Reason}).";
        return Back("jobs", "job-" + jobId);
    }

    // ---- Exclusions and effort ----

    public async Task<IActionResult> OnPostExcludeAsync(string person, string? reason, bool include, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("effort", null);
        if (!include && string.IsNullOrWhiteSpace(reason)) { Message = "Say why they're excluded (e.g. write-up, off the schedule more than 2 weeks)."; return Back("effort", "ex-" + Slug(person)); }
        await bonuses.SetExclusionAsync(person, Selected, include ? null : reason, User(), ct);
        Message = include ? $"{person} counts for {Label(Selected)} bonuses again." : $"{person} is excluded from {Label(Selected)} bonuses: {reason!.Trim()}.";
        return Back("effort", "ex-" + Slug(person));
    }

    public async Task<IActionResult> OnPostLeaveOutJobAsync(string jobId, string? jobName, string? reason, bool include, CancellationToken ct)
    {
        if (!await StartAsync(ct)) return Back("jobs", null);
        var name = string.IsNullOrWhiteSpace(jobName) ? jobId : jobName.Trim();
        if (!include && string.IsNullOrWhiteSpace(reason)) { Message = "Say why the job is left out (e.g. paid out in the spreadsheet round)."; return Back("jobs", "job-" + jobId); }
        await bonuses.SetJobExclusionAsync(jobId, jobName, include ? null : reason, User(), ct);
        Message = include ? $"{name} counts toward bonuses again." : $"{name} is left out of bonuses: {reason!.Trim()}.";
        return Back("jobs", "left-out");
    }

    public async Task<IActionResult> OnPostRateAsync(List<string> membershipId, List<string?> score, List<string?> notes, CancellationToken ct)
    {
        await StartAsync(ct);
        if (!CanRate) { Message = "Only supervisors can rate effort."; return Back("effort", null); }
        var ledger = await bonuses.GetAsync(false, ct);
        if (ledger.AnyLocked(Selected)) { Message = $"{Label(Selected)} has paid & locked bonuses, so its ratings can't change."; return Back("effort", "rate"); }
        Staff = await people.GetStaffAsync(ct);
        var list = new List<(StaffMember, int?, string?)>();
        for (var i = 0; i < membershipId.Count; i++)
        {
            if (Staff.FirstOrDefault(s => s.MembershipId == membershipId[i]) is not { } s) continue;
            int? sc = int.TryParse(score.ElementAtOrDefault(i), out var v) ? v : null;
            list.Add((s, sc, notes.ElementAtOrDefault(i)));
        }
        var n = await bonuses.SaveRatingsAsync(Selected, list, RaterKey, ct);
        Message = n == 0 ? "No changes to save." : $"Saved {n} effort rating{(n == 1 ? "" : "s")} for {Label(Selected)}.";
        return Back("effort", "rate");
    }

    private IActionResult Back(string tab, string? open) =>
        RedirectToPage(null, null, new { month = Month ?? Key(Selected), tab = tab == "reconcile" ? null : tab, open, all = All ? true : (bool?)null, only = Only }, open);

    public static DateOnly DefaultPaidOn(DateOnly month)
    {
        var end = month.AddMonths(1).AddDays(-1);
        return LocalClock.Today < month ? month : LocalClock.Today > end ? end : LocalClock.Today;
    }

    private static bool TryMoney(string? s, out decimal value) =>
        decimal.TryParse(s?.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private string User() => me.Email ?? me.DisplayName ?? "unknown";
}
