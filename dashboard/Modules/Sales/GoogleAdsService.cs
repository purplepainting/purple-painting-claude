using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.Google;

namespace PurpleDashboard.Modules.Sales;

/// <summary>Google Ads totals for one month (all campaigns). Leads = Google Ads conversions.</summary>
public sealed record AdsMonth(string Month, decimal Cost, long Impressions, long Clicks, decimal Conversions, long? PhoneCalls)
{
    public decimal? CostPerLead => Conversions > 0 ? Cost / Conversions : null;
    public decimal? CostPerClick => Clicks > 0 ? Cost / Clicks : null;
}

/// <summary>One campaign's totals over a period.</summary>
public sealed record AdsCampaign(string Id, string Name, string? Status, decimal Cost, long Impressions, long Clicks, decimal Conversions, long? PhoneCalls)
{
    public decimal? CostPerLead => Conversions > 0 ? Cost / Conversions : null;
    public decimal? ClickRate => Impressions > 0 ? 100m * Clicks / Impressions : null;
}

/// <summary>
/// What the Google Ads section can show. <see cref="Problem"/> is set (and <see cref="Report"/> null) when Google
/// isn't set up, connected, or answering; <see cref="ProblemKind"/> says which.
/// </summary>
public sealed record GoogleAdsView(GoogleConnection? Connection, AdsReport? Report, string? Problem, string? ProblemKind)
{
    public bool Ready => Report is not null;
}

/// <summary>Google Ads performance for Sales &amp; Marketing (read only, from the connected Google account).</summary>
public sealed class GoogleAdsService(GoogleClient google, ILogger<GoogleAdsService> logger)
{
    public string Channel => google.AdsChannel;

    public async Task<GoogleAdsView> GetAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) GoogleClient.ClearAdsCache();
        if (!google.IsConfigured) return new(null, null, "Google isn't set up yet (GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET). See docs/GOOGLE.md.", "not-configured");
        GoogleConnection? connection = null;
        try
        {
            connection = await google.GetConnectionAsync(ct);
            if (connection is null) return new(null, null, "Google isn't connected yet. Connect it on the Connections page to pull Google Ads spend automatically.", "not-connected");
            if (GoogleClient.MissingScopes(connection).Contains(GoogleOptions.AdsScope))
                return new(connection, null, "Google was connected without Google Ads access (the box was unticked on Google's page). Reconnect on Connections and allow Google Ads.", "scope");
            if (!google.HasDeveloperToken) return new(connection, null, "The Google Ads developer token isn't set yet (GOOGLE_ADS_DEVELOPER_TOKEN). See docs/GOOGLE.md step 5.", "developer-token");
            if (connection.AdsCustomerId is null) return new(connection, null, "Pick the Google Ads account on the Connections page.", "ads-account");
            return new(connection, await google.CampaignMonthsAsync(connection, ct), null, null);
        }
        catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading Google Ads failed");
            return new(connection, null, e is GoogleException ? e.Message : "Couldn't reach Google. Try again in a minute.", (e as GoogleException)?.Kind ?? "error");
        }
    }

    /// <summary>Totals per month for the 12 months in the report, oldest first; months without spend show as zero.</summary>
    public static List<AdsMonth> Months(AdsReport report)
    {
        var first = new DateOnly(LocalClock.Today.Year, LocalClock.Today.Month, 1).AddMonths(-11);
        var byMonth = report.Rows.GroupBy(r => r.Month).ToDictionary(g => g.Key);
        return Enumerable.Range(0, 12).Select(i => first.AddMonths(i).ToString("yyyy-MM")).Select(m =>
        {
            var rows = byMonth.TryGetValue(m, out var g) ? g.ToList() : [];
            return new AdsMonth(m, rows.Sum(r => r.Cost), rows.Sum(r => r.Impressions), rows.Sum(r => r.Clicks), rows.Sum(r => r.Conversions),
                report.PhoneCallsAvailable ? rows.Sum(r => r.PhoneCalls ?? 0) : null);
        }).ToList();
    }

    /// <summary>Campaign totals for months from..to (yyyy-MM, inclusive), most spend first.</summary>
    public static List<AdsCampaign> Campaigns(AdsReport report, string fromMonth, string toMonth) => report.Rows
        .Where(r => string.CompareOrdinal(r.Month, fromMonth) >= 0 && string.CompareOrdinal(r.Month, toMonth) <= 0)
        .GroupBy(r => r.CampaignId)
        .Select(g =>
        {
            var latest = g.OrderByDescending(r => r.Month).First();
            return new AdsCampaign(g.Key, latest.CampaignName, latest.Status, g.Sum(r => r.Cost), g.Sum(r => r.Impressions), g.Sum(r => r.Clicks), g.Sum(r => r.Conversions),
                report.PhoneCallsAvailable ? g.Sum(r => r.PhoneCalls ?? 0) : null);
        })
        .Where(c => c.Cost > 0 || c.Impressions > 0)
        .OrderByDescending(c => c.Cost).ThenBy(c => c.Name).ToList();

    public static string StatusLabel(string? status) => status switch
    {
        "ENABLED" => "Active",
        "PAUSED" => "Paused",
        "REMOVED" => "Removed",
        _ => "—",
    };
}
