using System.Globalization;

namespace PurpleDashboard.Infrastructure;

/// <summary>Number and date formatting used across pages. Always US formatting, whatever the server culture.</summary>
public static class Format
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal? value) => value is null ? "—" : value.Value.ToString("$#,##0;-$#,##0", Us);
    public static string Percent(decimal? value) => value is null ? "—" : Math.Round(value.Value).ToString("0", Us) + "%";
    public static string Number(decimal? value) => value is null ? "—" : value.Value.ToString("#,##0.#", Us);
    public static string Date(DateOnly? d) => d is null ? "—" : d.Value.ToString("MMM d", Us);
    /// <summary>"6:02 AM" today, otherwise "Oct 12, 6:02 AM".</summary>
    public static string QbAsOf(DateTime local) => local.Date == LocalClock.Today.ToDateTime(TimeOnly.MinValue) ? local.ToString("h:mm tt", Us) : local.ToString("MMM d, h:mm tt", Us);
    public static string Day(DateOnly d) => d.ToString("ddd M/d", Us);

    public static string DateRange(DateOnly? start, DateOnly? end) => (start, end) switch
    {
        (null, null) => "Not scheduled",
        ({ } s, null) => Date(s) + " –",
        (null, { } e) => "– " + Date(e),
        ({ } s, { } e) when s == e => Date(s),
        ({ } s, { } e) => $"{Date(s)} – {Date(e)}",
    };
}
