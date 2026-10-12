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
