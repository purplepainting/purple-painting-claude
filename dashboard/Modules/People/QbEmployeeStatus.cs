using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Modules.People;

/// <summary>A QuickBooks employee record: active or not, and the hire date (for the bonus probation period).</summary>
/// <param name="Name">QuickBooks display name, for matching people whose JobTread membership has no QuickBooks id.</param>
public sealed record QbEmployee(string Id, bool Active, DateOnly? HiredDate, DateOnly? ReleasedDate = null, string? Name = null);

/// <summary>
/// QuickBooks employee records by QuickBooks employee id (the id JobTread keeps on each membership as
/// qboEmployeeId): whether each is active, and their hire date. Someone who left stays "internal" in JobTread
/// until their membership is removed there, so the Employees list checks QuickBooks too. Read only; QuickBooks
/// results are cached for the day by QuickBooksClient. Empty when QuickBooks isn't connected or the query fails.
/// </summary>
public sealed class QbEmployeeStatus(QuickBooksClient quickBooks, ILogger<QbEmployeeStatus> logger)
{
    public async Task<IReadOnlyDictionary<string, QbEmployee>> EmployeesByIdAsync(CancellationToken ct)
    {
        try
        {
            if (await quickBooks.GetConnectionAsync(ct) is null) return new Dictionary<string, QbEmployee>();
            // QuickBooks only returns active employees unless asked for both.
            var rows = await quickBooks.QueryAsync("Employee", "select Id, DisplayName, HiredDate, ReleasedDate, Active from Employee where Active in (true, false)", ct);
            return rows.Where(r => r["Id"] is not null)
                .GroupBy(r => r["Id"]!.ToString())
                .ToDictionary(g => g.Key, g =>
                {
                    var r = g.First();
                    DateOnly? hired = DateOnly.TryParse(r["HiredDate"]?.ToString()?.Split('T')[0], out var d) ? d : null;
                    DateOnly? released = DateOnly.TryParse(r["ReleasedDate"]?.ToString()?.Split('T')[0], out var rd) ? rd : null;
                    return new QbEmployee(g.Key, r["Active"]?.GetValue<bool>() ?? true, hired, released, r["DisplayName"]?.ToString());
                });
        }
        catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(e, "Reading QuickBooks employees failed");
            return new Dictionary<string, QbEmployee>();
        }
    }

    public async Task<IReadOnlyDictionary<string, bool>> ActiveByIdAsync(CancellationToken ct) =>
        (await EmployeesByIdAsync(ct)).ToDictionary(e => e.Key, e => e.Value.Active);

    /// <summary>False only when QuickBooks says this person's employee record is inactive.</summary>
    public static bool IsCurrent(StaffMember s, IReadOnlyDictionary<string, bool> active) =>
        s.QboEmployeeId is not { } id || !active.TryGetValue(id, out var a) || a;
}
