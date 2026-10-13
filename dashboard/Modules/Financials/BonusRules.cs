using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Data;

namespace PurpleDashboard.Modules.Financials;

/// <summary>
/// Bonus rules. Edited by admins on Settings → Bonus rules (saved in the dashboard database, DashboardSettings
/// "bonus-rules"); the "Bonuses" settings section / appsettings are only the defaults until something is saved.
/// </summary>
public sealed class BonusOptions
{
    public decimal SupervisorPercent { get; set; } = 10;
    public decimal FieldPercent { get; set; } = 10;
    /// <summary>First month counted ("yyyy-MM"). Aug 2026: the last spreadsheet round paid through Jul 26, 2026.</summary>
    public string StartMonth { get; set; } = "2026-08";
    /// <summary>Field pool: percent split by hours, and percent split by effort score (the spreadsheets' base and "alternative" bonus).</summary>
    public decimal HoursShare { get; set; } = 25;
    public decimal EffortShare { get; set; } = 75;
    /// <summary>"hours-share-keep-excess" (nobody gets more than their share of hours; the excess stays with the company), "effort-multiple" (the effort part is capped at EffortCapMultiple × hours share, excess goes to others), or "none".</summary>
    public string CapMode { get; set; } = "hours-share-keep-excess";
    public decimal EffortCapMultiple { get; set; } = 2;
    /// <summary>"company": shares are worked out as if everyone were eligible, and the shares of people on probation, gone or excluded are kept by the company. "redistribute": they're left out and the pool goes to everyone else.</summary>
    public string ForfeitMode { get; set; } = "company";
    /// <summary>Percent of each person's year-to-date net bonus held back until it's released.</summary>
    public decimal RetentionPercent { get; set; } = 10;
    /// <summary>"year-end" (released in December's true-up) or "job-close" (each job's share released once the job is closed in JobTread).</summary>
    public string RetentionRelease { get; set; } = "year-end";
    /// <summary>A negative year-end balance carries into next year's ledger (otherwise it resets to 0).</summary>
    public bool NegativeCarryOver { get; set; } = true;
    /// <summary>Nothing is payable for a month when the company's QuickBooks net profit for that month is below zero (it stays owed).</summary>
    public bool HoldWhenCompanyLoses { get; set; }
    /// <summary>Days after the hire date (QuickBooks) before someone can earn a bonus.</summary>
    public int ProbationDays { get; set; } = 90;
    /// <summary>Effort score scale: whole numbers, every rater scores every field employee each month.</summary>
    public int EffortScaleMin { get; set; } = 1;
    public int EffortScaleMax { get; set; } = 10;
    /// <summary>Raters' scores this far apart get flagged ("worth a conversation").</summary>
    public int EffortSpreadFlag { get; set; } = 5;
    /// <summary>QuickBooks account(s) the Bonus pay item posts to (numbers or names, comma separated).</summary>
    public string BonusAccounts { get; set; } = "6114";

    public BonusOptions Copy() => (BonusOptions)MemberwiseClone();

    public static string CapModeLabel(string mode) => mode switch
    {
        "none" => "No cap",
        "effort-multiple" => "Effort part capped at a multiple of hours share (excess to others)",
        _ => "Capped at hours share (excess stays with the company)",
    };
}

public static class BonusPools
{
    public const string Supervisor = "Supervisor", Field = "Field";
}

/// <summary>
/// THE split formulas, in one place. Field pool, per job per month, among the people in the split:
///   share = HoursShare × hours / Σhours + EffortShare × effort / Σeffort
/// (effort = average rating that month; not rated = 0; if nobody is rated the effort part goes by hours), then the
/// cap (CapMode). Supervisors' pool: by supervisors' hours on the job that month. Pools can be negative (a losing
/// job-month): the same shares apply. Each person's amount is rounded to the cent; the pool isn't rounded.
/// </summary>
public static class BonusSplit
{
    /// <param name="Kept">Part of the pool nobody gets because of the cap (stays with the company).</param>
    public sealed record FieldResult(Dictionary<string, decimal> Amounts, HashSet<string> Capped, decimal Kept);

    public static FieldResult Field(decimal pool, IReadOnlyDictionary<string, decimal> hours, Func<string, decimal?> score, BonusOptions rules)
    {
        var people = hours.Where(h => h.Value > 0).Select(h => h.Key).ToList();
        if (people.Count == 0 || pool == 0) return new([], [], 0);
        var totalHours = people.Sum(p => hours[p]);
        var hoursShare = people.ToDictionary(p => p, p => hours[p] / totalHours);
        var scores = people.ToDictionary(p => p, p => Math.Max(0, score(p) ?? 0));
        var totalScore = scores.Values.Sum();
        var effortShare = people.ToDictionary(p => p, p => totalScore > 0 ? scores[p] / totalScore : hoursShare[p]);
        var weightTotal = Math.Max(0, rules.HoursShare) + Math.Max(0, rules.EffortShare);
        var h = weightTotal == 0 ? 1 : Math.Max(0, rules.HoursShare) / weightTotal;
        var e = 1 - h;
        var effortPart = people.ToDictionary(p => p, p => e * effortShare[p]);
        var share = people.ToDictionary(p => p, p => h * hoursShare[p] + effortPart[p]);
        var capped = new HashSet<string>();
        var kept = 0m;
        const decimal Tiny = 0.0000001m;

        if (rules.CapMode == "hours-share-keep-excess")
        {
            foreach (var p in people.Where(p => share[p] - hoursShare[p] > Tiny))
            {
                kept += share[p] - hoursShare[p];
                share[p] = hoursShare[p];
                capped.Add(p);
            }
        }
        else if (rules.CapMode == "effort-multiple")
        {
            var cap = people.ToDictionary(p => p, p => e * Math.Max(0, rules.EffortCapMultiple) * hoursShare[p]);
            var part = new Dictionary<string, decimal>(effortPart);
            for (var round = 0; round < 50; round++)
            {
                var over = people.Where(p => part[p] - cap[p] > Tiny).ToList();
                if (over.Count == 0) break;
                var excess = 0m;
                foreach (var p in over) { excess += part[p] - cap[p]; part[p] = cap[p]; capped.Add(p); }
                var under = people.Where(p => cap[p] - part[p] > Tiny).ToList();
                if (under.Count == 0) { kept += excess; break; }
                var weight = under.Sum(p => effortPart[p]);
                var room = under.Sum(p => cap[p] - part[p]);
                foreach (var p in under) part[p] += excess * (weight > 0 ? effortPart[p] / weight : (cap[p] - part[p]) / room);
            }
            foreach (var p in people) share[p] = h * hoursShare[p] + part[p];
        }
        return new(people.ToDictionary(p => p, p => Math.Round(pool * share[p], 2)), capped, Math.Round(pool * kept, 2));
    }

    public static Dictionary<string, decimal> ByWeight(decimal pool, IReadOnlyDictionary<string, decimal> weights)
    {
        var total = weights.Values.Where(v => v > 0).Sum();
        if (total <= 0 || pool == 0) return [];
        return weights.Where(w => w.Value > 0).ToDictionary(w => w.Key, w => Math.Round(pool * w.Value / total, 2));
    }
}

/// <summary>Saved bonus rules (Settings → Bonus rules), over the defaults from configuration. Cached until saved again.</summary>
public sealed class BonusRulesStore(IOptions<BonusOptions> defaults, IServiceProvider services, DatabaseStatus database, IMemoryCache cache, ChangeLogWriter changeLog)
{
    public const string Key = "bonus-rules";
    private const string CacheKey = "settings:" + Key;

    public sealed record Saved(BonusOptions Rules, DateTime? UpdatedAt, string? UpdatedBy);

    public BonusOptions Defaults => defaults.Value;

    public async Task<Saved> GetAsync(CancellationToken ct)
    {
        if (!database.Available) return new(defaults.Value.Copy(), null, null);
        return (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var db = services.GetRequiredService<DashboardDb>();
            var row = await db.DashboardSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
            var rules = defaults.Value.Copy();
            if (row is not null)
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<BonusOptions>(row.Json);
                    if (saved is not null) rules = saved;
                }
                catch (JsonException) { }
            }
            return new Saved(rules, row?.UpdatedAt, row?.UpdatedBy);
        }))!;
    }

    /// <summary>Saves the rules; logs each changed rule as before → after. Returns how many changed.</summary>
    public async Task<int> SaveAsync(BonusOptions rules, string user, CancellationToken ct)
    {
        var before = (await GetAsync(ct)).Rules;
        var changes = new Dictionary<string, object?>();
        foreach (var prop in typeof(BonusOptions).GetProperties().Where(p => p.CanWrite))
        {
            var a = Convert.ToString(prop.GetValue(before), CultureInfo.InvariantCulture);
            var b = Convert.ToString(prop.GetValue(rules), CultureInfo.InvariantCulture);
            if (a != b) changes[prop.Name] = new { before = a, after = b };
        }
        if (changes.Count == 0) return 0;
        var db = services.GetRequiredService<DashboardDb>();
        var row = await db.DashboardSettings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) { row = new DashboardSetting { Key = Key, Json = "" }; db.DashboardSettings.Add(row); }
        row.Json = JsonSerializer.Serialize(rules);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "bonuses", "rules", "Bonus rules", changes, "Done")], ct);
        return changes.Count;
    }
}
