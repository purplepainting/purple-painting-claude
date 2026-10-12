using System.Text.Json;

namespace PurpleDashboard.Data;

/// <summary>
/// Writes to the ChangeLog table. Does nothing without a database, and a failed write is logged,
/// never thrown: the change itself already happened.
/// </summary>
public sealed class ChangeLogWriter(IServiceProvider services, DatabaseStatus database, ILogger<ChangeLogWriter> logger)
{
    public static ChangeLogEntry Entry(string? user, string area, string action, string? target, object? details, string? result) => new()
    {
        At = DateTime.UtcNow,
        User = Trim(user, 200),
        Area = area,
        Action = action,
        Target = Trim(target, 300),
        Details = details is null ? null : JsonSerializer.Serialize(details),
        Result = Trim(result, 40),
    };

    public async Task WriteAsync(IReadOnlyCollection<ChangeLogEntry> entries, CancellationToken ct)
    {
        if (!database.Available || entries.Count == 0) return;
        try
        {
            var db = services.GetRequiredService<DashboardDb>();
            db.ChangeLog.AddRange(entries);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Writing {Count} change log entries failed", entries.Count);
        }
    }

    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
