using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;

namespace PurpleDashboard.Modules.Changes;

/// <summary>What the change log page asks for. Days is 7, 30 or 90; Page starts at 1.</summary>
public sealed record ChangeFilter(string? Area, string? Person, int Days, string? Q, int Page);

/// <summary>One page of entries (newest first), plus the areas and people seen in the date range for the filters.</summary>
public sealed record ChangePage(IReadOnlyList<ChangeLogEntry> Entries, bool HasOlder, int Total, IReadOnlyList<string> Areas, IReadOnlyList<string> People);

/// <summary>
/// Reads the ChangeLog table (written by <see cref="ChangeLogWriter"/>). Read only.
/// </summary>
public sealed class ChangeLogReader(IServiceProvider services, DatabaseStatus database)
{
    public const int PageSize = 50;

    public bool Available => database.Available;
    private DashboardDb Db => services.GetRequiredService<DashboardDb>();

    public async Task<ChangePage> GetAsync(ChangeFilter filter, IReadOnlyDictionary<int, string> itemNames, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-filter.Days);
        var inRange = Db.ChangeLog.AsNoTracking().Where(e => e.At >= since);

        var areas = await inRange.Select(e => e.Area).Distinct().ToListAsync(ct);
        var people = await inRange.Where(e => e.User != null).Select(e => e.User!).Distinct().ToListAsync(ct);

        var query = inRange;
        if (!string.IsNullOrWhiteSpace(filter.Area)) query = query.Where(e => e.Area == filter.Area);
        if (!string.IsNullOrWhiteSpace(filter.Person)) query = query.Where(e => e.User == filter.Person);
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var q = filter.Q.Trim();
            // Sundry receipts are logged with the item id as the target, so match item names too.
            var itemIds = itemNames.Where(i => i.Value.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(i => i.Key.ToString()).ToList();
            query = query.Where(e => (e.Target != null && e.Target.Contains(q)) || (e.Details != null && e.Details.Contains(q))
                || e.Action.Contains(q) || (e.Area == "sundries" && e.Action == "receive" && itemIds.Contains(e.Target!)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Skip((Math.Max(1, filter.Page) - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);

        return new ChangePage(rows.Take(PageSize).ToList(), rows.Count > PageSize, total,
            areas.Order(StringComparer.OrdinalIgnoreCase).ToList(), people.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Every sundry's name by id, archived ones included (older log entries name items by id).</summary>
    public async Task<Dictionary<int, string>> ItemNamesAsync(CancellationToken ct) =>
        await Db.InventoryItems.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Name, ct);

    /// <summary>Email → JobTread name for everyone who linked JobTread on the Connections page.</summary>
    public async Task<Dictionary<string, string>> LinkedNamesAsync(CancellationToken ct)
    {
        var rows = await Db.JobTreadUserKeys.AsNoTracking()
            .Where(k => k.UserEmail != null && k.JobTreadUserName != null)
            .Select(k => new { k.UserEmail, k.JobTreadUserName }).ToListAsync(ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) result.TryAdd(r.UserEmail!, r.JobTreadUserName!);
        return result;
    }
}
