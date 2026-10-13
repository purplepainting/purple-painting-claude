using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Modules.People;

/// <summary>
/// Whether each QuickBooks employee is active, by QuickBooks employee id (the id JobTread keeps on each
/// membership as qboEmployeeId). Someone who left stays "internal" in JobTread until their membership is
/// removed there, so the Employees list checks QuickBooks too. Read only; QuickBooks results are cached
/// for the day by QuickBooksClient. Empty when QuickBooks isn't connected or the query fails.
/// </summary>
public sealed class QbEmployeeStatus(QuickBooksClient quickBooks, ILogger<QbEmployeeStatus> logger)
{
    public async Task<IReadOnlyDictionary<string, bool>> ActiveByIdAsync(CancellationToken ct)
    {
        try
        {
            if (await quickBooks.GetConnectionAsync(ct) is null) return new Dictionary<string, bool>();
            // QuickBooks only returns active employees unless asked for both.
            var rows = await quickBooks.QueryAsync("Employee", "select Id, Active from Employee where Active in (true, false)", ct);
            return rows.Where(r => r["Id"] is not null)
                .GroupBy(r => r["Id"]!.ToString())
                .ToDictionary(g => g.Key, g => g.First()["Active"]?.GetValue<bool>() ?? true);
        }
        catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(e, "Reading QuickBooks employees failed; showing everyone JobTread lists");
            return new Dictionary<string, bool>();
        }
    }

    /// <summary>False only when QuickBooks says this person's employee record is inactive.</summary>
    public static bool IsCurrent(StaffMember s, IReadOnlyDictionary<string, bool> active) =>
        s.QboEmployeeId is not { } id || !active.TryGetValue(id, out var a) || a;
}
