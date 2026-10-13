using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Financials;

namespace PurpleDashboard.Pages.Financials;

public class IndexModel(FinancialsService financials, QuickBooksClient quickBooks, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"overview" (default), "burden", "overhead".</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>Period preset: "3m" (default), "month", "last", "ytd", "lastyear", "jt" (since JobTread), or "custom" with From/To.</summary>
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    /// <summary>"Accrual" (default) or "Cash".</summary>
    [BindProperty(SupportsGet = true)] public string? Basis { get; set; }

    public static readonly DateOnly JobTreadStart = new(2026, 5, 1);

    public static readonly (string Key, string Label)[] Periods =
    [
        ("month", "This month"), ("last", "Last month"), ("3m", "Last 3 months"),
        ("jt", "Since JobTread (May)"), ("ytd", "Year to date"), ("lastyear", "Last year"),
    ];

    public FinancialsReport? Report { get; private set; }
    public bool Connected { get; private set; }
    public string? Error { get; private set; }
    public DateOnly RangeFrom { get; private set; }
    public DateOnly RangeTo { get; private set; }
    public string CurrentTab => Tab is "burden" or "overhead" ? Tab : "overview";
    public string CurrentBasis => Basis == "Cash" ? "Cash" : "Accrual";

    public async Task OnGetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) PurpleDashboard.Integrations.QuickBooks.QuickBooksClient.ClearCache();
        ViewData["Title"] = "Financials";
        ViewData["ModuleName"] = "Financials";
        (RangeFrom, RangeTo) = Range();

        Connected = await quickBooks.GetConnectionAsync(ct) is not null;
        if (!Connected) return;
        try
        {
            Report = await financials.GetAsync(RangeFrom, RangeTo, CurrentBasis, refresh, ct);
        }
        catch (Exception e) when (e is QuickBooksException or JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Loading financials failed");
            Error = e is QuickBooksException or JobTreadException ? e.Message : "Couldn't reach QuickBooks or JobTread. Try again in a minute.";
        }
    }

    private (DateOnly, DateOnly) Range()
    {
        var today = LocalClock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return Period switch
        {
            "month" => (monthStart, today),
            "last" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "ytd" => (new DateOnly(today.Year, 1, 1), today),
            "lastyear" => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            "jt" => (JobTreadStart, today),
            "custom" when From is { } f && To is { } t && f <= t => (f, t),
            _ => (monthStart.AddMonths(-2), today),
        };
    }

    public Dictionary<string, string?> RouteFor(string? tab = null, string? period = null, string? basis = null) => new()
    {
        ["tab"] = (tab ?? CurrentTab) == "overview" ? null : tab ?? CurrentTab,
        ["period"] = period ?? Period,
        ["from"] = (period ?? Period) == "custom" ? From?.ToString("yyyy-MM-dd") : null,
        ["to"] = (period ?? Period) == "custom" ? To?.ToString("yyyy-MM-dd") : null,
        ["basis"] = (basis ?? CurrentBasis) == "Cash" ? "Cash" : null,
    };

    public static string Pct(decimal part, decimal whole) => whole == 0 ? "—" : Format.Percent(100m * part / whole);
    public static string PerHour(decimal? v) => v is null ? "—" : v.Value.ToString("$#,##0.00", System.Globalization.CultureInfo.InvariantCulture) + "/hr";
    public static string Gap(decimal qb, decimal jt) => Format.Money(qb - jt);
}
