using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PurpleDashboard.Data;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Modules.Financials;

/// <summary>
/// Bonus rules (settings section "Bonuses"). Default: 10% of job profit to supervisors and 10% to the field,
/// each split by hours worked on the job. A job is ready once it's Completed and fully billed and collected.
/// </summary>
public sealed class BonusOptions
{
    public decimal SupervisorPercent { get; set; } = 10;
    public decimal FieldPercent { get; set; } = 10;
    /// <summary>How far back to look for closed jobs.</summary>
    public int ClosedLookbackDays { get; set; } = 365;
}

public sealed record BonusLine(string Name, string Role, decimal Hours, decimal Amount);

public enum BonusStatus { Projected, Ready, Approved, Paid }

public sealed record JobBonus(
    string JobId, string JobName, string? Number, string? ProjectManager,
    decimal Billed, decimal Costs, decimal Profit,
    decimal SupervisorPool, decimal FieldPool,
    BonusStatus Status, string? Waiting,
    IReadOnlyList<BonusLine> Lines, BonusPayout? Payout)
{
    public decimal Total => SupervisorPool + FieldPool;
}

/// <summary>
/// Works out bonuses per job from JobTread (billed incl. retention − costs = profit; time entries for the
/// split) and keeps approved/paid bonuses in the dashboard database so they can't change afterwards.
/// </summary>
public sealed class BonusService(ProjectsService projects, IServiceProvider services, DatabaseStatus database, IOptions<BonusOptions> options, ChangeLogWriter changeLog)
{
    public BonusOptions Rules => options.Value;

    public async Task<List<JobBonus>> GetAsync(bool refresh, CancellationToken ct)
    {
        var open = await projects.GetOpenJobsAsync(refresh, ct);
        var closed = await projects.GetClosedJobsAsync(PurpleDashboard.Infrastructure.LocalClock.Today.AddDays(-Rules.ClosedLookbackDays), refresh, ct);
        var payouts = await PayoutsAsync(ct);

        var jobs = open.Concat(closed).Where(j => !j.IsInternal && j.Stage is not null).GroupBy(j => j.Id).Select(g => g.First()).ToList();
        var result = new List<JobBonus>();
        foreach (var j in jobs)
        {
            payouts.TryGetValue(j.Id, out var payout);
            if (payout is null && j.Profit <= 0 && !j.IsCompleted) continue;

            var ready = j.IsCompleted && !j.HasMoneyOutstanding;
            var status = payout?.Status == "paid" ? BonusStatus.Paid : payout is not null ? BonusStatus.Approved : ready ? BonusStatus.Ready : BonusStatus.Projected;
            var waiting = status != BonusStatus.Projected ? null
                : !j.IsCompleted ? $"Job is {j.Stage}"
                : j.LeftToBill >= 1 ? $"{PurpleDashboard.Infrastructure.Format.Money(j.LeftToBill)} left to bill"
                : j.Unpaid >= 1 ? $"{PurpleDashboard.Infrastructure.Format.Money(j.Unpaid)} unpaid"
                : j.RetentionHeld >= 1 ? $"{PurpleDashboard.Infrastructure.Format.Money(j.RetentionHeld)} retention held" : null;

            if (payout is not null)
            {
                result.Add(new JobBonus(j.Id, j.Name, j.Number, j.ProjectManager, j.Billed, j.ActualCost, payout.Profit,
                    payout.SupervisorPool, payout.FieldPool, status, null,
                    JsonSerializer.Deserialize<List<BonusLine>>(payout.LinesJson) ?? [], payout));
                continue;
            }

            var profit = Math.Max(0, j.Profit);
            var supPool = Math.Round(profit * Rules.SupervisorPercent / 100m, 2);
            var fieldPool = Math.Round(profit * Rules.FieldPercent / 100m, 2);
            // Splits need the job's time entries; only worked out for jobs that are ready (others show pools only).
            var lines = status == BonusStatus.Ready ? await SplitAsync(j, supPool, fieldPool, ct) : [];
            result.Add(new JobBonus(j.Id, j.Name, j.Number, j.ProjectManager, j.Billed, j.ActualCost, j.Profit, supPool, fieldPool, status, waiting, lines, null));
        }

        // Paid or approved jobs that are no longer in the job lists still show.
        foreach (var p in payouts.Values.Where(p => result.All(r => r.JobId != p.JobId)))
            result.Add(new JobBonus(p.JobId, p.JobName ?? p.JobId, null, null, 0, 0, p.Profit, p.SupervisorPool, p.FieldPool,
                p.Status == "paid" ? BonusStatus.Paid : BonusStatus.Approved, null, JsonSerializer.Deserialize<List<BonusLine>>(p.LinesJson) ?? [], p));

        return result.OrderBy(r => r.Status switch { BonusStatus.Ready => 0, BonusStatus.Approved => 1, BonusStatus.Projected => 2, _ => 3 })
            .ThenByDescending(r => r.Total).ToList();
    }

    /// <summary>
    /// Splits each pool by hours on the job: supervisors' pool among supervisors who clocked time (or all to the
    /// job's PM if none did), field pool among field crew. Rounding leftovers go to the person with most hours.
    /// </summary>
    public async Task<List<BonusLine>> SplitAsync(ProjectSummary job, decimal supPool, decimal fieldPool, CancellationToken ct)
    {
        var detail = await projects.GetDetailAsync(job.Id, false, ct);
        var lines = new List<BonusLine>();
        var sups = detail.Supervisors.Where(c => c.Hours > 0).ToList();
        if (sups.Count > 0) lines.AddRange(Share(sups, supPool, "Supervisor"));
        else if (supPool > 0) lines.Add(new BonusLine(job.ProjectManager ?? "Supervisor (no PM set)", "Supervisor", 0, supPool));
        lines.AddRange(Share(detail.FieldCrew.Where(c => c.Hours > 0).ToList(), fieldPool, "Field"));
        return lines;
    }

    private static IEnumerable<BonusLine> Share(List<CrewMember> people, decimal pool, string role)
    {
        var hours = people.Sum(p => p.Hours);
        if (hours <= 0 || pool <= 0) return [];
        var shares = people.Select(p => new BonusLine(p.Name, role, p.Hours, Math.Round(pool * p.Hours / hours, 2))).ToList();
        var leftover = pool - shares.Sum(s => s.Amount);
        if (leftover != 0) { var top = shares.MaxBy(s => s.Hours)!; shares[shares.IndexOf(top)] = top with { Amount = top.Amount + leftover }; }
        return shares;
    }

    /// <summary>Locks in a job's bonus as calculated now.</summary>
    public async Task ApproveAsync(JobBonus bonus, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        if (await db.BonusPayouts.AnyAsync(p => p.JobId == bonus.JobId, ct)) return;
        db.BonusPayouts.Add(new BonusPayout
        {
            JobId = bonus.JobId, JobName = bonus.JobName, Profit = bonus.Profit,
            SupervisorPool = bonus.SupervisorPool, FieldPool = bonus.FieldPool,
            Status = "approved", LinesJson = JsonSerializer.Serialize(bonus.Lines),
            ApprovedAt = DateTime.UtcNow, ApprovedBy = user,
        });
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "approve", bonus.JobName, new { bonus.Profit, bonus.SupervisorPool, bonus.FieldPool, bonus.Lines }, "Done")], ct);
    }

    public async Task SetStatusAsync(string jobId, string action, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var payout = await db.BonusPayouts.FirstOrDefaultAsync(p => p.JobId == jobId, ct);
        if (payout is null) return;
        switch (action)
        {
            case "paid": payout.Status = "paid"; payout.PaidAt = DateTime.UtcNow; payout.PaidBy = user; break;
            case "unpaid": payout.Status = "approved"; payout.PaidAt = null; payout.PaidBy = null; break;
            case "unapprove" when payout.Status == "approved": db.BonusPayouts.Remove(payout); break;
            default: return;
        }
        await db.SaveChangesAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", action, payout.JobName, null, "Done")], ct);
    }

    private async Task<Dictionary<string, BonusPayout>> PayoutsAsync(CancellationToken ct)
    {
        if (!database.Available) return [];
        var db = services.GetRequiredService<DashboardDb>();
        return await db.BonusPayouts.AsNoTracking().ToDictionaryAsync(p => p.JobId, ct);
    }
}
