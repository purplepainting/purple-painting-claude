using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.Meta;

namespace PurpleDashboard.Modules.Sales;

/// <summary>Ad results for one month, one campaign or one platform (a row of Meta insights).</summary>
public sealed record MetaStats(decimal Spend, long Impressions, long Reach, long Clicks, long LinkClicks, int Leads)
{
    public static readonly MetaStats Zero = new(0, 0, 0, 0, 0, 0);
    public decimal? CostPerLead => Leads > 0 ? Spend / Leads : null;
    public decimal? CostPerLinkClick => LinkClicks > 0 ? Spend / LinkClicks : null;
    public decimal? LinkCtr => Impressions > 0 ? 100m * LinkClicks / Impressions : null;

    /// <summary>Sum (reach is added up too, so a summed reach counts people once per row, not once overall).</summary>
    public static MetaStats Sum(IEnumerable<MetaStats> rows) => rows.Aggregate(Zero, (a, b) =>
        new(a.Spend + b.Spend, a.Impressions + b.Impressions, a.Reach + b.Reach, a.Clicks + b.Clicks, a.LinkClicks + b.LinkClicks, a.Leads + b.Leads));
}

public sealed record MetaMonth(string Month, MetaStats Stats);
public sealed record MetaCampaignMonth(string Month, string CampaignId, string Campaign, MetaStats Stats);
public sealed record MetaPlatformMonth(string Month, string Platform, MetaStats Stats);

/// <summary>The last 12 months of Facebook/Instagram ads for the picked ad account.</summary>
public sealed record MetaAdsReport(
    string AdAccountId, string? AdAccountName, string SinceMonth, string UntilMonth,
    List<MetaMonth> Months, List<MetaCampaignMonth> Campaigns, List<MetaPlatformMonth> Platforms)
{
    /// <summary>Months Meta's numbers cover (every month in the window, even ones with no spend).</summary>
    public bool Covers(string month) => string.CompareOrdinal(month, SinceMonth) >= 0 && string.CompareOrdinal(month, UntilMonth) <= 0;
}

public enum MetaAdsState { NotSetUp, NotConnected, NoAdAccount, Error, Ready }

/// <summary>What the Sales &amp; Marketing page shows in its Facebook/Instagram section.</summary>
public sealed class MetaAdsView
{
    public MetaAdsState State { get; init; }
    public string? Error { get; init; }
    public bool NeedsReconnect { get; init; }
    public MetaAdsReport? Report { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string FromMonth { get; init; } = "";
    public string ToMonth { get; init; } = "";

    public bool ExpiresSoon => ExpiresAt is { } e && e - DateTime.UtcNow < TimeSpan.FromDays(MetaClient.ReconnectWarningDays);

    public IEnumerable<MetaMonth> MonthsInPeriod => Report?.Months.Where(m => InPeriod(m.Month)) ?? [];

    /// <summary>Campaigns with results in the period, biggest spend first.</summary>
    public List<(string Campaign, MetaStats Stats)> CampaignsInPeriod => (Report?.Campaigns ?? [])
        .Where(c => InPeriod(c.Month))
        .GroupBy(c => c.CampaignId)
        .Select(g => (g.OrderByDescending(c => c.Month).First().Campaign, MetaStats.Sum(g.Select(c => c.Stats))))
        .OrderByDescending(c => c.Item2.Spend).ToList();

    public List<(string Platform, MetaStats Stats)> PlatformsInPeriod => (Report?.Platforms ?? [])
        .Where(p => InPeriod(p.Month))
        .GroupBy(p => p.Platform)
        .Select(g => (g.Key, MetaStats.Sum(g.Select(p => p.Stats))))
        .OrderByDescending(p => p.Item2.Spend).ToList();

    private bool InPeriod(string month) => string.CompareOrdinal(month, FromMonth) >= 0 && string.CompareOrdinal(month, ToMonth) <= 0;
}

/// <summary>
/// Facebook/Instagram ads performance from the Meta Marketing API (read only): monthly totals, per campaign and
/// per platform, and Meta spend fed into the ads → revenue view as the "Facebook/Instagram" channel.
/// </summary>
public sealed class MetaAdsService(MetaClient meta, ILogger<MetaAdsService> logger)
{
    /// <summary>Channel name Meta spend shows under in the ads view.</summary>
    public const string Channel = "Facebook/Instagram";

    /// <summary>Manual spend channels that mean the same thing as Meta's numbers (the API replaces them for months it covers).</summary>
    public static readonly HashSet<string> ChannelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        Channel, "Facebook / Instagram", "Facebook", "Instagram", "Facebook Ads", "Instagram Ads", "Meta", "Meta Ads", "Facebook & Instagram",
    };

    /// <summary>
    /// Meta lead action types. "lead" is Meta's all-leads total (instant forms + website pixel), so it's used when
    /// present; otherwise the form ("onsite_conversion.lead_grouped") and pixel ("offsite_conversion.fb_pixel_lead")
    /// counts are added. This avoids counting the same lead twice.
    /// </summary>
    public static readonly string[] LeadActions = ["lead", "onsite_conversion.lead_grouped", "offsite_conversion.fb_pixel_lead"];

    private const string Fields = "spend,impressions,reach,clicks,inline_link_clicks,actions";

    public async Task<MetaAdsView> LoadAsync(DateOnly from, DateOnly to, bool refresh, CancellationToken ct)
    {
        var fromMonth = from.ToString("yyyy-MM");
        var toMonth = to.ToString("yyyy-MM");
        if (!meta.IsConfigured) return new MetaAdsView { State = MetaAdsState.NotSetUp, FromMonth = fromMonth, ToMonth = toMonth };
        if (refresh) MetaClient.ClearCache();

        MetaConnection? connection = null;
        try
        {
            connection = await meta.GetConnectionAsync(ct);
            if (connection is null) return new MetaAdsView { State = MetaAdsState.NotConnected, FromMonth = fromMonth, ToMonth = toMonth };
            if (connection.AdAccountId is null) return new MetaAdsView { State = MetaAdsState.NoAdAccount, ExpiresAt = connection.ExpiresAt, FromMonth = fromMonth, ToMonth = toMonth };

            var report = await ReportAsync(connection.AdAccountId, connection.AdAccountName, ct);
            return new MetaAdsView { State = MetaAdsState.Ready, Report = report, ExpiresAt = connection.ExpiresAt, FromMonth = fromMonth, ToMonth = toMonth };
        }
        catch (Exception e) when (e is MetaException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Loading Meta ads failed");
            return new MetaAdsView
            {
                State = MetaAdsState.Error, ExpiresAt = connection?.ExpiresAt, FromMonth = fromMonth, ToMonth = toMonth,
                Error = e is MetaException ? e.Message : "Couldn't reach Meta. Try again in a minute.",
                NeedsReconnect = e is MetaException { NeedsReconnect: true },
            };
        }
    }

    /// <summary>The last 12 months (this month and the 11 before), three insights calls, each cached until 5am.</summary>
    public async Task<MetaAdsReport> ReportAsync(string adAccountId, string? adAccountName, CancellationToken ct)
    {
        var today = LocalClock.Today;
        var since = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var range = JsonSerializer.Serialize(new { since = since.ToString("yyyy-MM-dd"), until = today.ToString("yyyy-MM-dd") });
        Dictionary<string, string> Params(string level, string fields, string? breakdowns = null)
        {
            var p = new Dictionary<string, string> { ["level"] = level, ["time_increment"] = "monthly", ["time_range"] = range, ["fields"] = fields };
            if (breakdowns is not null) p["breakdowns"] = breakdowns;
            return p;
        }

        var accountTask = meta.InsightsAsync(adAccountId, Params("account", Fields), ct);
        var campaignTask = meta.InsightsAsync(adAccountId, Params("campaign", "campaign_id,campaign_name," + Fields), ct);
        var platformTask = meta.InsightsAsync(adAccountId, Params("account", Fields, "publisher_platform"), ct);
        await Task.WhenAll(accountTask, campaignTask, platformTask);

        var months = (await accountTask).Select(r => new MetaMonth(Month(r), Stats(r)))
            .GroupBy(m => m.Month).Select(g => new MetaMonth(g.Key, MetaStats.Sum(g.Select(m => m.Stats))))
            .OrderByDescending(m => m.Month).ToList();
        var campaigns = (await campaignTask)
            .Select(r => new MetaCampaignMonth(Month(r), Str(r, "campaign_id") ?? "", Str(r, "campaign_name") ?? "(no name)", Stats(r))).ToList();
        var platforms = (await platformTask)
            .Select(r => new MetaPlatformMonth(Month(r), PlatformName(Str(r, "publisher_platform")), Stats(r))).ToList();

        return new MetaAdsReport(adAccountId, adAccountName, since.ToString("yyyy-MM"), today.ToString("yyyy-MM"), months, campaigns, platforms);
    }

    /// <summary>
    /// Meta as an <see cref="ApiSpendSource"/> for <see cref="SalesService.MergeApiSpend"/>: the "Facebook/Instagram" channel
    /// for every month of the 12-month window (a month with no spend is $0, not missing), replacing hand-entered rows
    /// named like it (<see cref="ChannelAliases"/>) for those months.
    /// </summary>
    public static ApiSpendSource SpendSource(MetaAdsReport report) => new(
        "Meta", Channel, ChannelAliases.Contains, report.Covers,
        report.Months.Where(m => m.Stats.Spend > 0 || m.Stats.Leads > 0)
            .Select(m => new SpendLine(m.Month, Channel, Math.Round(m.Stats.Spend, 2), m.Stats.Leads, "Meta")).ToList());

    // ---- Parsing insights rows ----

    private static string Month(JsonObject r) => (Str(r, "date_start") ?? "0000-00")[..7];

    private static MetaStats Stats(JsonObject r) => new(
        Dec(Str(r, "spend")), Long(Str(r, "impressions")), Long(Str(r, "reach")), Long(Str(r, "clicks")), Long(Str(r, "inline_link_clicks")), Leads(r["actions"] as JsonArray));

    public static int Leads(JsonArray? actions)
    {
        if (actions is null) return 0;
        var byType = actions.OfType<JsonObject>()
            .Select(a => (Type: Str(a, "action_type"), Value: Dec(Str(a, "value"))))
            .Where(a => a.Type is not null && LeadActions.Contains(a.Type))
            .GroupBy(a => a.Type!).ToDictionary(g => g.Key, g => g.Sum(a => a.Value));
        var total = byType.TryGetValue("lead", out var all) ? all
            : byType.GetValueOrDefault("onsite_conversion.lead_grouped") + byType.GetValueOrDefault("offsite_conversion.fb_pixel_lead");
        return (int)Math.Round(total);
    }

    public static string PlatformName(string? p) => p switch
    {
        "facebook" => "Facebook",
        "instagram" => "Instagram",
        "audience_network" => "Audience Network",
        "messenger" => "Messenger",
        "threads" => "Threads",
        null or "" => "Other",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p.Replace('_', ' ')),
    };

    // Meta sends numbers as strings ("123.45").
    private static string? Str(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
    private static decimal Dec(string? s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static long Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (long)Dec(s);
}
