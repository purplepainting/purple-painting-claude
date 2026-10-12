namespace PurpleDashboard.Infrastructure;

/// <summary>
/// "Today" for Purple Painting (Santa Barbara). Azure runs in UTC, so never use DateTime.Today directly.
/// </summary>
public static class LocalClock
{
    private static readonly TimeZoneInfo Zone = Find();

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static DateOnly Today => DateOnly.FromDateTime(Now);

    public static DateOnly ToLocalDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);

    /// <summary>Monday of the week containing the date.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("Pacific", TimeSpan.FromHours(-8), "Pacific", "Pacific"); }
    }
}
