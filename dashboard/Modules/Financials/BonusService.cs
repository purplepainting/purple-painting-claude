using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Modules.Financials;

/// <summary>
/// Monthly bonuses, kept as a yearly running ledger per person. Each job's profit is worked out per month from
/// JobTread (BonusMonths: invoices − costs). Every job-month adds the supervisors' and field pools (percent of that
/// month's profit; negative when the month lost money) to the people on the job, split by <see cref="BonusSplit"/>.
/// Clawbacks (costs not in JobTread, callbacks) charge the people on the job by their year-to-date shares of it.
/// <see cref="BonusLedger"/> turns the lines into each person's year: owed = year-to-date net − retention − paid,
/// never below zero. Rules come from Settings → Bonus rules (<see cref="BonusRulesStore"/>).
/// </summary>
public sealed class BonusService(
    BonusMonths months, ProjectsService projects, PeopleService people, QbEmployeeStatus qbEmployees, BonusRulesStore rulesStore,
    IServiceProvider services, DatabaseStatus database, ChangeLogWriter changeLog, ILogger<BonusService> logger)
{
    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly Lazy<Dictionary<string, decimal>> LastRoundRatings = new(LoadLastRound);
    private BonusOptions? rules;

    /// <summary>The rules in effect (call <see cref="LoadRulesAsync"/> first; until then the configured defaults).</summary>
    public BonusOptions Rules => rules ?? rulesStore.Defaults;

    public async Task<BonusOptions> LoadRulesAsync(CancellationToken ct) => rules = (await rulesStore.GetAsync(ct)).Rules;

    public DateOnly StartMonth => DateOnly.TryParseExact(Rules.StartMonth + "-01", "yyyy-MM-dd", Us, DateTimeStyles.None, out var d) ? d : new DateOnly(2026, 8, 1);
    public static DateOnly CurrentMonth => new(LocalClock.Today.Year, LocalClock.Today.Month, 1);
    public static string Key(DateOnly month) => month.ToString("yyyy-MM", Us);
    public static string Label(DateOnly month) => month.ToString("MMM yyyy", Us);

    public IReadOnlyList<DateOnly> AllMonths()
    {
        var list = new List<DateOnly>();
        for (var m = StartMonth; m <= CurrentMonth; m = m.AddMonths(1)) list.Add(m);
        if (list.Count == 0) list.Add(CurrentMonth);
        return list;
    }

    public async Task<BonusLedger> GetAsync(bool refresh, CancellationToken ct)
    {
        await LoadRulesAsync(ct);
        await ImportOldApprovalsAsync(ct);
        var monthList = AllMonths();
        var data = new Dictionary<DateOnly, IReadOnlyDictionary<string, JobMonth>>();
        foreach (var m in monthList) data[m] = await months.GetAsync(m, refresh, ct);

        var open = await projects.GetOpenJobsAsync(refresh, ct);
        var closed = await projects.GetClosedJobsAsync(monthList[0], refresh, ct);
        var info = open.Concat(closed).GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.First());
        var staff = await people.GetStaffAsync(ct);
        var supervisorIds = staff.Where(s => !s.IsField).Select(s => s.UserId).ToHashSet();
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in staff) roles[s.Name] = s.IsField ? BonusPools.Field : BonusPools.Supervisor;

        var stored = await StoredAsync(ct);

        // QuickBooks employee records (hire/release dates, active) by the qboEmployeeId on each JobTread membership.
        var qb = await qbEmployees.EmployeesByIdAsync(ct);
        var qbByName = new Dictionary<string, QbEmployee>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in staff)
            if (s.QboEmployeeId is { } q && qb.TryGetValue(q, out var e)) qbByName[s.Name] = e;
        var eligibility = new Eligibility(qbByName, stored.Exclusions, Rules.ProbationDays);

        var byMembership = staff.ToDictionary(s => s.MembershipId, s => s.Name);
        var scores = stored.Ratings.GroupBy(r => (r.Month, Name: byMembership.GetValueOrDefault(r.MembershipId) ?? r.Person))
            .ToDictionary(g => g.Key, g => g.Average(r => r.Score));

        var jobIds = data.Values.SelectMany(d => d.Keys).Concat(stored.Clawbacks.Select(c => c.JobId)).Distinct()
            .Where(id => id != "none" && !(info.TryGetValue(id, out var j) && j.IsInternal));
        var jobs = new List<JobBonusView>();
        foreach (var id in jobIds)
        {
            info.TryGetValue(id, out var summary);
            var any = data.Values.Select(d => d.GetValueOrDefault(id)).FirstOrDefault(x => x is not null);
            var view = new JobBonusView
            {
                JobId = id,
                Name = summary?.Name ?? any?.JobName ?? stored.Clawbacks.FirstOrDefault(c => c.JobId == id)?.JobName ?? id,
                Number = summary?.Number ?? any?.JobNumber,
                ProjectManager = summary?.ProjectManager,
                Stage = summary?.Stage,
                ClosedOn = summary?.ClosedOn,
            };
            view.Clawbacks.AddRange(stored.Clawbacks.Where(c => c.JobId == id).OrderBy(c => c.Month).ThenBy(c => c.Id));
            Calculate(view, monthList, data, supervisorIds, roles, eligibility, scores);
            if (view.Months.Count > 0) jobs.Add(view);
        }
        var entries = jobs.SelectMany(j => j.Entries).ToList();
        foreach (var e in entries) roles.TryAdd(e.Name, e.Role);
        var adjustments = new List<BonusAdjustment>(stored.Adjustments);
        ApplyLocks(stored.Locks, entries, adjustments, jobs);

        return new BonusLedger
        {
            Rules = Rules,
            Months = monthList,
            Jobs = jobs.OrderByDescending(j => j.LastMonth).ThenByDescending(j => j.Net).ToList(),
            Entries = entries,
            Payments = stored.Payments,
            Adjustments = adjustments,
            Locks = stored.Locks,
            Releases = stored.Releases,
            Roles = roles,
            UnpaidApprovals = stored.Unpaid,
            Eligibility = eligibility,
            Ratings = stored.Ratings,
            FieldStaff = staff.Where(s => s.IsField && !(qbByName.TryGetValue(s.Name, out var q) && !q.Active && q.ReleasedDate is null)).ToList(),
            LastRound = LastRoundRatings.Value,
        };
    }

    /// <summary>Works out one job month by month.</summary>
    private void Calculate(JobBonusView job, IReadOnlyList<DateOnly> monthList, Dictionary<DateOnly, IReadOnlyDictionary<string, JobMonth>> data,
        HashSet<string> supervisorIds, Dictionary<string, string> roles, Eligibility eligibility, Dictionary<(string Month, string Name), decimal> scores)
    {
        var pools = new[] { BonusPools.Supervisor, BonusPools.Field };
        var pct = new Dictionary<string, decimal> { [BonusPools.Supervisor] = Rules.SupervisorPercent, [BonusPools.Field] = Rules.FieldPercent };
        var pending = pools.ToDictionary(p => p, _ => 0m);
        var forfeitToCompany = Rules.ForfeitMode != "redistribute";
        var pm = job.ProjectManager;
        // Year-to-date per pool: each person's share on this job and their hours (clawbacks are charged by these).
        var ytdShare = pools.ToDictionary(p => p, _ => new Dictionary<string, decimal>());
        var ytdHours = pools.ToDictionary(p => p, _ => new Dictionary<string, decimal>());

        foreach (var month in monthList)
        {
            if (month.Month == 1) foreach (var p in pools) { ytdShare[p].Clear(); ytdHours[p].Clear(); }
            var jm = data[month].GetValueOrDefault(job.JobId);
            var claws = job.Clawbacks.Where(c => c.Month == Key(month)).ToList();
            var mb = new JobMonthBonus { Month = month, Income = jm?.Income ?? 0, Costs = jm?.Costs ?? 0 };
            decimal? Score(string name) => scores.TryGetValue((Key(month), name), out var sc) ? sc : null;
            string? Why(string name) => eligibility.Why(name, month);

            var monthHours = pools.ToDictionary(p => p, _ => new Dictionary<string, decimal>());
            foreach (var p in jm?.People ?? [])
            {
                var pool = supervisorIds.Contains(p.UserId) ? BonusPools.Supervisor : BonusPools.Field;
                roles.TryAdd(p.Name, pool);
                monthHours[pool][p.Name] = monthHours[pool].GetValueOrDefault(p.Name) + p.Hours;
                ytdHours[pool][p.Name] = ytdHours[pool].GetValueOrDefault(p.Name) + p.Hours;
            }
            foreach (var name in monthHours.Values.SelectMany(d => d.Keys).Distinct())
                if (Why(name) is { } why) mb.Excluded.Add((name, why));

            var shares = new Dictionary<(string Name, string Role), BonusEntry>();
            BonusEntry Entry(string name, string pool) => shares.TryGetValue((name, pool), out var x) ? x
                : shares[(name, pool)] = new BonusEntry { JobId = job.JobId, Month = month, Name = name, Role = pool, Hours = monthHours[pool].GetValueOrDefault(name) };
            // Who's in the split: everyone who worked it (ineligible shares then go to the company), or only eligible people.
            Dictionary<string, decimal> InSplit(Dictionary<string, decimal> d) =>
                d.Where(x => x.Value > 0 && (forfeitToCompany || Why(x.Key) is null)).ToDictionary(x => x.Key, x => x.Value);

            if (jm is not null && mb.Profit < 0)
                mb.Notes.Add($"Lost {Format.Money(-mb.Profit)} this month, so the pools are negative: each person's share comes off their year's bonus total (it lowers what they're paid later; nobody pays cash back).");
            foreach (var pool in pools)
            {
                var amount = mb.Profit * pct[pool] / 100m;
                if (pool == BonusPools.Supervisor) mb.SupervisorPool = amount; else mb.FieldPool = amount;
                amount += pending[pool];
                if (amount == 0) continue;

                Dictionary<string, decimal>? split = null;
                var hours = InSplit(monthHours[pool]);
                if (pool == BonusPools.Supervisor)
                {
                    if (hours.Count > 0) split = BonusSplit.ByWeight(amount, hours);
                    else if (pm is not null && (forfeitToCompany || Why(pm) is null))
                    {
                        split = new() { [pm] = Math.Round(amount, 2) };
                        roles.TryAdd(pm, BonusPools.Supervisor);
                        mb.Notes.Add($"No supervisor clocked time on the job this month, so the supervisors' pool goes to the PM ({pm}).");
                    }
                }
                else
                {
                    if (hours.Count == 0 && InSplit(ytdHours[pool]).Count > 0)
                    {
                        hours = InSplit(ytdHours[pool]);
                        mb.Notes.Add("No field hours on the job this month, so the field pool is split by field hours on the job so far this year.");
                    }
                    if (hours.Count > 0)
                    {
                        var result = BonusSplit.Field(amount, hours, Score, Rules);
                        split = result.Amounts;
                        mb.CapKept += result.Kept;
                        foreach (var n in result.Capped) Entry(n, pool).Capped = true;
                        var unrated = hours.Keys.Where(n => Score(n) is null && Why(n) is null).ToList();
                        if (unrated.Count > 0)
                            mb.Notes.Add(hours.Keys.All(n => Score(n) is null)
                                ? $"No effort ratings for {Label(month)} yet: the effort part is split by hours until people are rated."
                                : $"Not rated for {Label(month)}, so no effort part: {string.Join(", ", unrated)}.");
                    }
                }
                if (split is null)
                {
                    pending[pool] = amount;
                    mb.Notes.Add($"No one in the {pool.ToLowerInvariant()} pool has clocked time on the job yet; {Format.Money(amount)} waits for the first month someone does.");
                    continue;
                }
                pending[pool] = 0;
                foreach (var (name, amt) in split)
                {
                    if (Why(name) is { } why) { mb.Forfeited.Add((name, pool, amt, why)); continue; }
                    Entry(name, pool).Amount += amt;
                    ytdShare[pool][name] = ytdShare[pool].GetValueOrDefault(name) + amt;
                }
            }

            foreach (var c in claws)
            {
                foreach (var (pool, amount) in ClawbackSplit(c))
                {
                    if (amount == 0) continue;
                    if (pool == BonusPools.Supervisor) mb.SupervisorClawback += amount; else mb.FieldClawback += amount;
                    var weights = ytdShare[pool].Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value);
                    if (weights.Count == 0) weights = ytdHours[pool].Where(x => x.Value > 0 && Why(x.Key) is null).ToDictionary(x => x.Key, x => x.Value);
                    if (weights.Count == 0 && pool == BonusPools.Supervisor && pm is not null) weights = new() { [pm] = 1 };
                    if (weights.Count == 0)
                    {
                        pending[pool] -= amount;
                        mb.Notes.Add($"Clawback of {Format.Money(amount)} from the {pool.ToLowerInvariant()} pool comes off the next bonus earned on this job (nobody has a share of it this year yet).");
                        continue;
                    }
                    foreach (var (name, amt) in BonusSplit.ByWeight(amount, weights)) Entry(name, pool).Clawback += amt;
                }
            }

            mb.People.AddRange(shares.Values.Where(e => e.Amount != 0 || e.Clawback != 0 || e.Capped));
            if (jm is not null || claws.Count > 0) job.Months.Add(mb);
        }
        job.UnassignedSupervisor = pending[BonusPools.Supervisor];
        job.UnassignedField = pending[BonusPools.Field];
    }

    public sealed record LockLine(string JobId, string? JobName, string Role, decimal Hours, decimal Amount, decimal Clawback);
    public sealed record LockSnapshot(List<LockLine> Lines, decimal Adjustments);

    /// <summary>
    /// Paid &amp; locked months: the person's lines for a locked month are the snapshot taken at lock time. If the month
    /// works out differently now (JobTread edited after payment, ratings, rules), the difference goes into their next
    /// unlocked month as an automatic adjustment, so the locked month never changes.
    /// </summary>
    private static void ApplyLocks(IReadOnlyList<PayLock> locks, List<BonusEntry> entries, List<BonusAdjustment> adjustments, List<JobBonusView> jobs)
    {
        var names = jobs.ToDictionary(j => j.JobId, j => j.Name);
        foreach (var l in locks.Where(l => l.Kind == "bonus").OrderBy(l => l.Month, StringComparer.Ordinal))
        {
            LockSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<LockSnapshot>(l.LinesJson); } catch (JsonException) { snap = null; }
            if (snap is null) continue;
            var month = DateOnly.ParseExact(l.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool Mine(string person) => person.Equals(l.Person, StringComparison.OrdinalIgnoreCase);
            var now = entries.Where(e => Mine(e.Name) && e.Month == month).ToList();
            var nowAdj = adjustments.Where(a => Mine(a.Person) && a.Month == l.Month).ToList();
            var diff = now.Sum(e => e.Net) + nowAdj.Sum(a => a.Amount) - snap.Lines.Sum(x => x.Amount - x.Clawback) - snap.Adjustments;

            foreach (var e in now) entries.Remove(e);
            foreach (var a in nowAdj) adjustments.Remove(a);
            entries.AddRange(snap.Lines.Select(x => new BonusEntry { JobId = x.JobId, Month = month, Name = l.Person, Role = x.Role, Hours = x.Hours, Amount = x.Amount, Clawback = x.Clawback }));
            if (snap.Adjustments != 0)
                adjustments.Add(new BonusAdjustment { Person = l.Person, Month = l.Month, Amount = snap.Adjustments, Reason = "Adjustments (as locked)", CreatedBy = "lock" });

            if (Math.Abs(diff) < 0.005m) continue;
            var target = month.AddMonths(1);
            while (locks.Any(o => o.Kind == "bonus" && Mine(o.Person) && o.Month == Key(target))) target = target.AddMonths(1);
            var changed = now.GroupBy(e => e.JobId).Select(g => (Job: g.Key, Net: g.Sum(e => e.Net)))
                .Where(x => Math.Abs(x.Net - snap.Lines.Where(s => s.JobId == x.Job).Sum(s => s.Amount - s.Clawback)) >= 0.005m)
                .Select(x => names.GetValueOrDefault(x.Job) ?? x.Job)
                .Concat(snap.Lines.Where(s => now.All(e => e.JobId != s.JobId)).Select(s => s.JobName ?? s.JobId))
                .Distinct().ToList();
            adjustments.Add(new BonusAdjustment
            {
                Person = l.Person, Month = Key(target), Amount = Math.Round(diff, 2), CreatedBy = "automatic",
                Reason = $"Adjustment for {Label(month)} ({(changed.Count > 0 ? string.Join(", ", changed) : "figures")} changed after payment)",
            });
        }
    }

    /// <summary>Locks a person's month: snapshots their lines and adjustments as the ledger has them now.</summary>
    public async Task<PayLock> LockAsync(BonusLedger ledger, string person, DateOnly month, PayrollLine? check, decimal? gross, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var key = Key(month);
        var existing = await db.PayLocks.FirstOrDefaultAsync(l => l.Kind == "bonus" && l.Person == person && l.Month == key, ct);
        if (existing is not null) return existing;
        var lines = ledger.Entries.Where(e => e.Name.Equals(person, StringComparison.OrdinalIgnoreCase) && e.Month == month)
            .Select(e => new LockLine(e.JobId, ledger.Jobs.FirstOrDefault(j => j.JobId == e.JobId)?.Name, e.Role, e.Hours, e.Amount, e.Clawback)).ToList();
        var adj = ledger.Adjustments.Where(a => a.Person.Equals(person, StringComparison.OrdinalIgnoreCase) && a.Month == key).Sum(a => a.Amount);
        var status = ledger.Status(person, month);
        var row = new PayLock
        {
            Person = person, Month = key, LinesJson = JsonSerializer.Serialize(new LockSnapshot(lines, adj)),
            Net = lines.Sum(x => x.Amount - x.Clawback) + adj, Owed = status.Owed,
            CheckNum = check?.Num, CheckDate = check?.Date, Gross = gross, LockedAt = DateTime.UtcNow, LockedBy = user,
        };
        db.PayLocks.Add(row);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "lock", person, new { month = key, net = row.Net, owed = row.Owed, check = row.CheckNum, gross = row.Gross }, "Done")], ct);
        return row;
    }

    public async Task<PayLock?> UnlockAsync(string person, DateOnly month, string reason, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var key = Key(month);
        var row = await db.PayLocks.FirstOrDefaultAsync(l => l.Kind == "bonus" && l.Person == person && l.Month == key, ct);
        if (row is null) return null;
        db.PayLocks.Remove(row);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "unlock", person, new { month = key, reason = reason.Trim(), net = row.Net }, "Done")], ct);
        return row;
    }

    /// <summary>How much of a clawback comes from each pool.</summary>
    public IEnumerable<(string Pool, decimal Amount)> ClawbackSplit(BonusClawback c)
    {
        if (c.Kind == "cost")
        {
            yield return (BonusPools.Supervisor, Math.Round(c.Amount * Rules.SupervisorPercent / 100m, 2));
            yield return (BonusPools.Field, Math.Round(c.Amount * Rules.FieldPercent / 100m, 2));
            yield break;
        }
        switch (c.Pool)
        {
            case "supervisor": yield return (BonusPools.Supervisor, c.Amount); break;
            case "field": yield return (BonusPools.Field, c.Amount); break;
            default:
                var total = Rules.SupervisorPercent + Rules.FieldPercent;
                var sup = total == 0 ? Math.Round(c.Amount / 2, 2) : Math.Round(c.Amount * Rules.SupervisorPercent / total, 2);
                yield return (BonusPools.Supervisor, sup);
                yield return (BonusPools.Field, c.Amount - sup);
                break;
        }
    }

    public decimal ClawbackTotal(BonusClawback c) => ClawbackSplit(c).Sum(x => x.Amount);

    /// <summary>The 2026 Q1–Q2 spreadsheet round's average effort ratings (embedded), by normalized name.</summary>
    private static Dictionary<string, decimal> LoadLastRound()
    {
        try
        {
            using var stream = typeof(BonusService).Assembly.GetManifestResourceStream("PurpleDashboard.Financials.effort-last-round.json");
            if (stream is null) return [];
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.GetProperty("ratings").EnumerateObject()
                .ToDictionary(p => PayrollPayments.Normalize(p.Name), p => p.Value.GetDecimal());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    // ---- Stored data ----

    private sealed record Stored(List<BonusClawback> Clawbacks, List<BonusPayment> Payments, List<BonusPayout> Unpaid, List<EffortRating> Ratings,
        List<BonusExclusion> Exclusions, List<BonusRetentionRelease> Releases, List<BonusAdjustment> Adjustments, List<PayLock> Locks);

    private async Task<Stored> StoredAsync(CancellationToken ct)
    {
        if (!database.Available) return new([], [], [], [], [], [], [], []);
        var db = services.GetRequiredService<DashboardDb>();
        return new(await db.BonusClawbacks.AsNoTracking().ToListAsync(ct),
            await db.BonusPayments.AsNoTracking().ToListAsync(ct),
            await db.BonusPayouts.AsNoTracking().Where(p => p.Status != "paid").ToListAsync(ct),
            await db.EffortRatings.AsNoTracking().ToListAsync(ct),
            await db.BonusExclusions.AsNoTracking().ToListAsync(ct),
            await db.BonusRetentionReleases.AsNoTracking().ToListAsync(ct),
            await db.BonusAdjustments.AsNoTracking().ToListAsync(ct),
            await db.PayLocks.AsNoTracking().Where(l => l.Kind == "bonus").ToListAsync(ct));
    }

    /// <summary>
    /// Old per-job approvals that were marked paid become payments (one per person, dated the day they were marked
    /// paid), once. Approved-but-unpaid ones stay as history: their jobs are now counted month by month instead.
    /// </summary>
    private async Task ImportOldApprovalsAsync(CancellationToken ct)
    {
        if (!database.Available) return;
        await ImportLock.WaitAsync(ct);
        try
        {
            var db = services.GetRequiredService<DashboardDb>();
            var todo = await db.BonusPayouts.Where(p => p.MigratedAt == null).ToListAsync(ct);
            if (todo.Count == 0) return;
            var made = new List<object>();
            foreach (var p in todo)
            {
                if (p.Status == "paid")
                {
                    var paidOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(p.PaidAt ?? p.ApprovedAt, DateTimeKind.Utc), LocalClock.Zone));
                    foreach (var l in (JsonSerializer.Deserialize<List<BonusLine>>(p.LinesJson) ?? []).Where(l => l.Amount != 0))
                    {
                        db.BonusPayments.Add(new BonusPayment
                        {
                            Person = l.Name, Amount = l.Amount, PaidOn = paidOn, JobId = p.JobId, JobName = p.JobName,
                            Covers = "Job bonus (before monthly bonuses)",
                            Note = $"Approved {p.ApprovedAt:MMM d} by {p.ApprovedBy}, marked paid by {p.PaidBy}",
                            Source = "migrated", CreatedAt = DateTime.UtcNow, CreatedBy = "migration",
                        });
                        made.Add(new { person = l.Name, amount = l.Amount, paidOn, job = p.JobName });
                    }
                }
                p.MigratedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            if (made.Count > 0)
                await changeLog.WriteAsync([ChangeLogWriter.Entry("migration", "bonuses", "migrate", $"{made.Count} payments", new { payments = made }, "Done")], ct);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException or JsonException)
        {
            logger.LogError(e, "Copying old bonus approvals into payments failed");
        }
        finally
        {
            ImportLock.Release();
        }
    }

    public async Task<BonusPayment> AddPaymentAsync(string person, decimal amount, DateOnly paidOn, string? covers, string? note, string source, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var p = new BonusPayment
        {
            Person = person.Trim(), Amount = Math.Round(amount, 2), PaidOn = paidOn, Covers = Clean(covers),
            Note = Clean(note), Source = source, CreatedAt = DateTime.UtcNow, CreatedBy = user,
        };
        db.BonusPayments.Add(p);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", source == "payroll" ? "pay-from-payroll" : "pay", p.Person,
            new { amount = p.Amount, paidOn, covers = p.Covers, note = p.Note }, "Done")], ct);
        return p;
    }

    public async Task<BonusPayment?> DeletePaymentAsync(int id, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var p = await db.BonusPayments.FindAsync([id], ct);
        if (p is null) return null;
        db.BonusPayments.Remove(p);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "delete-payment", p.Person, new { amount = p.Amount, paidOn = p.PaidOn, note = p.Note }, "Done")], ct);
        return p;
    }

    public async Task<BonusClawback> SaveClawbackAsync(int? id, string jobId, string? jobName, DateOnly month, string kind, string pool, decimal amount, string reason, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var c = id is { } i ? await db.BonusClawbacks.FindAsync([i], ct) : null;
        object? before = c is null ? null : new { month = c.Month, kind = c.Kind, pool = c.Pool, amount = c.Amount, reason = c.Reason };
        if (c is null)
        {
            c = new BonusClawback { JobId = jobId, JobName = jobName, Month = Key(month), Reason = reason, CreatedAt = DateTime.UtcNow, CreatedBy = user };
            db.BonusClawbacks.Add(c);
        }
        else { c.UpdatedAt = DateTime.UtcNow; c.UpdatedBy = user; }
        c.Month = Key(month);
        c.Kind = kind == "cost" ? "cost" : "bonus";
        c.Pool = pool is "supervisor" or "field" ? pool : "both";
        c.Amount = Math.Round(amount, 2);
        c.Reason = reason.Trim();
        c.JobName ??= jobName;
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", before is null ? "clawback-add" : "clawback-edit", c.JobName ?? c.JobId,
            new { month = c.Month, kind = c.Kind, pool = c.Pool, amount = c.Amount, reason = c.Reason, bonus = ClawbackTotal(c), before }, "Done")], ct);
        return c;
    }

    public async Task<BonusClawback?> DeleteClawbackAsync(int id, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var c = await db.BonusClawbacks.FindAsync([id], ct);
        if (c is null) return null;
        db.BonusClawbacks.Remove(c);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "clawback-delete", c.JobName ?? c.JobId,
            new { month = c.Month, kind = c.Kind, pool = c.Pool, amount = c.Amount, reason = c.Reason }, "Done")], ct);
        return c;
    }

    /// <summary>A manual line on someone's year (waive a negative balance, correct something), reason required.</summary>
    public async Task<BonusAdjustment> AddAdjustmentAsync(string person, DateOnly month, decimal amount, string reason, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var a = new BonusAdjustment { Person = person.Trim(), Month = Key(month), Amount = Math.Round(amount, 2), Reason = reason.Trim(), CreatedAt = DateTime.UtcNow, CreatedBy = user };
        db.BonusAdjustments.Add(a);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "adjust", a.Person, new { month = a.Month, amount = a.Amount, reason = a.Reason }, "Done")], ct);
        return a;
    }

    public async Task<BonusAdjustment?> DeleteAdjustmentAsync(int id, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var a = await db.BonusAdjustments.FindAsync([id], ct);
        if (a is null) return null;
        db.BonusAdjustments.Remove(a);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "delete-adjustment", a.Person, new { month = a.Month, amount = a.Amount, reason = a.Reason }, "Done")], ct);
        return a;
    }

    /// <summary>Releases held retention early in a month, for one person or everyone (person null).</summary>
    public async Task ReleaseRetentionAsync(DateOnly month, string? person, string? reason, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        db.BonusRetentionReleases.Add(new BonusRetentionRelease { Month = Key(month), Person = Clean(person), Reason = Clean(reason), CreatedAt = DateTime.UtcNow, CreatedBy = user });
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "release-retention", Clean(person) ?? "everyone", new { month = Key(month), reason = Clean(reason) }, "Done")], ct);
    }

    public async Task<BonusRetentionRelease?> UndoReleaseAsync(int id, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var r = await db.BonusRetentionReleases.FindAsync([id], ct);
        if (r is null) return null;
        db.BonusRetentionReleases.Remove(r);
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "undo-release", r.Person ?? "everyone", new { month = r.Month }, "Done")], ct);
        return r;
    }

    /// <summary>Excludes someone from bonuses for a month (or takes the exclusion back when reason is null).</summary>
    public async Task SetExclusionAsync(string person, DateOnly month, string? reason, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var key = Key(month);
        var row = await db.BonusExclusions.FirstOrDefaultAsync(e => e.Month == key && e.Person == person, ct);
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (row is null) return;
            db.BonusExclusions.Remove(row);
            await db.SaveChangesAsync(ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "include", person, new { month = key, reason = row.Reason }, "Done")], ct);
            return;
        }
        if (row is null) { row = new BonusExclusion { Month = key, Person = person, Reason = reason.Trim() }; db.BonusExclusions.Add(row); }
        row.Reason = reason.Trim(); row.CreatedAt = DateTime.UtcNow; row.CreatedBy = user;
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "exclude", person, new { month = key, reason = row.Reason }, "Done")], ct);
    }

    /// <summary>Saves one rater's effort scores for a month (one row per month, person and rater; saving again replaces it). A blank score removes that rater's score.</summary>
    public async Task<int> SaveRatingsAsync(DateOnly month, IReadOnlyList<(StaffMember Person, int? Score, string? Notes)> ratings, string rater, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var key = Key(month);
        var existing = await db.EffortRatings.Where(r => r.Month == key && r.RatedBy == rater).ToListAsync(ct);
        var changed = new List<object>();
        foreach (var (person, score, notes) in ratings)
        {
            var row = existing.FirstOrDefault(r => r.MembershipId == person.MembershipId);
            if (score is null)
            {
                if (row is not null) { db.EffortRatings.Remove(row); changed.Add(new { person = person.Name, score = (int?)null, before = (decimal?)row.Score }); }
                continue;
            }
            var s = Math.Clamp(score.Value, Rules.EffortScaleMin, Rules.EffortScaleMax);
            var n = Clean(notes);
            if (row is not null && row.Score == s && row.Notes == n) continue;
            decimal? before = row?.Score;
            if (row is null) { row = new EffortRating { Month = key, MembershipId = person.MembershipId, Person = person.Name, RatedBy = rater }; db.EffortRatings.Add(row); }
            changed.Add(new { person = person.Name, score = (int?)s, before, notes = n });
            row.Score = s; row.Notes = n; row.Person = person.Name; row.RatedAt = DateTime.UtcNow;
        }
        if (changed.Count == 0) return 0;
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(rater, "bonuses", "rate-effort", Label(month), new { ratings = changed }, "Done")], ct);
        return changed.Count;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
