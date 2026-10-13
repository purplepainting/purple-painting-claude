namespace PurpleDashboard.Modules.Sales;

/// <summary>
/// One row of the "By channel" comparison on Sales → Ads &amp; lead sources: a channel group (Facebook, Google, Other), a
/// single channel inside Other, the "No lead source" row, or the total. Spend is the counted figure (platform API, else
/// QuickBooks, else entered by hand); Clicks are platform-reported (null when no platform reports them); JtLeads are new
/// JobTread customer accounts in the period by Lead Source; WonLeads = of those, how many already have a sold job; Jobs and
/// Contract are sold jobs (open + last year) by the customer's Lead Source. SpendFrom = counted spend by source label.
/// </summary>
public sealed record ChannelSummary(string Key, string Label, decimal Spend, long? Clicks, int? JtLeads, int WonLeads, int Jobs, decimal Contract,
    IReadOnlyList<(string Source, decimal Amount)> SpendFrom, string? Kind = null)
{
    public decimal? CostPerLead => Spend > 0 && JtLeads > 0 ? Spend / JtLeads.Value : null;
    /// <summary>Spend ÷ new customers from the period who already have a sold job (period-matched, unlike Jobs).</summary>
    public decimal? CostPerSoldJob => Spend > 0 && WonLeads > 0 ? Spend / WonLeads : null;
    public decimal? CloseRate => JtLeads > 0 ? (decimal)WonLeads / JtLeads.Value : null;
    /// <summary>"QuickBooks", "Meta API · QuickBooks" etc., most spend first; "—" when nothing was counted.</summary>
    public string SpendFromLabel => SpendFrom.Count == 0 ? "—" : string.Join(" · ", SpendFrom.Select(s => s.Source));
}

/// <summary>
/// The channel pills on the Ads tab (All · Facebook · Google · Other) and the one place a spend channel or JobTread Lead
/// Source is put in one of them: whatever <see cref="SalesService.ChannelFor"/> maps to the Google Ads channel is Google,
/// to "Facebook/Instagram" is Facebook, anything else (Nextdoor, Yelp, printing, SEO, referrals, door knocking…) is Other.
/// </summary>
public static class AdChannels
{
    public const string Facebook = "facebook";
    public const string Google = "google";
    public const string Other = "other";
    public const string NoLeadSource = "none";
    public const string Total = "total";

    /// <summary>The pills in order: query value (null = All) and label.</summary>
    public static readonly (string? Key, string Label)[] Pills = [(null, "All"), (Facebook, "Facebook"), (Google, "Google"), (Other, "Other")];

    /// <summary>The <c>channel</c> query value as a pill key; null (All) for anything else.</summary>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Facebook or "meta" or "fb" or "instagram" => Facebook,
        Google => Google,
        Other => Other,
        _ => null,
    };

    public static string Label(string? key) => key switch
    {
        Facebook => "Facebook",
        Google => "Google",
        Other => "Other",
        _ => "All",
    };

    /// <summary>The pill a spend channel or Lead Source value belongs to.</summary>
    public static string GroupOf(string channel, string googleChannel)
    {
        var c = SalesService.ChannelFor(channel, googleChannel);
        if (string.Equals(c, googleChannel, StringComparison.OrdinalIgnoreCase)) return Google;
        if (string.Equals(c, MetaAdsService.Channel, StringComparison.OrdinalIgnoreCase)) return Facebook;
        return Other;
    }

    /// <summary>Lead source rows in a pill (rows with no Lead Source belong to none of them).</summary>
    public static IEnumerable<LeadSource> SourcesIn(IEnumerable<LeadSource> sources, string group, string googleChannel) =>
        sources.Where(s => !s.NoLeadSource && GroupOf(s.Source, googleChannel) == group);

    /// <summary>Counted spend lines in a pill.</summary>
    public static IEnumerable<SpendLine> SpendIn(IEnumerable<SpendLine> lines, string group, string googleChannel) =>
        lines.Where(l => GroupOf(l.Channel, googleChannel) == group);

    /// <summary>Counted spend by source label ("Meta API", "QuickBooks", "Entered by hand"), most spend first.</summary>
    public static List<(string Source, decimal Amount)> SpendFrom(IEnumerable<SpendLine> lines) => lines
        .GroupBy(l => SalesService.SourceLabel(l.ApiSource))
        .Select(g => (g.Key, g.Sum(l => l.Amount))).Where(g => g.Item2 != 0)
        .OrderByDescending(g => g.Item2).ToList();

    /// <summary>
    /// The comparison rows: Facebook, Google, Other (each a group total), then "No lead source" (sold jobs and new accounts
    /// without a Lead Source) when there is any, then the total. Clicks: platform-reported (Google Ads clicks, Meta link
    /// clicks), null when that platform isn't connected or loaded. Totals add up across the rows.
    /// </summary>
    public static List<ChannelSummary> Compare(IReadOnlyList<LeadSource> sources, IReadOnlyList<SpendLine> counted, string googleChannel,
        long? googleClicks, long? metaClicks, JobTreadLeads? jtLeads)
    {
        var rows = new List<ChannelSummary>
        {
            Group(Facebook, sources, counted, googleChannel, metaClicks, jtLeads),
            Group(Google, sources, counted, googleChannel, googleClicks, jtLeads),
            Group(Other, sources, counted, googleChannel, null, jtLeads),
        };
        var none = sources.Where(s => s.NoLeadSource).ToList();
        int? untagged = jtLeads?.Untagged;
        if (none.Count > 0 || untagged > 0)
            rows.Add(new ChannelSummary(NoLeadSource, "No lead source", 0, null, untagged, 0, none.Sum(s => s.Jobs), none.Sum(s => s.Contract), [], NoLeadSource));
        long? clicks = googleClicks is null && metaClicks is null ? null : (googleClicks ?? 0) + (metaClicks ?? 0);
        rows.Add(new ChannelSummary(Total, "Total", counted.Sum(l => l.Amount), clicks, jtLeads?.Total, rows.Sum(r => r.WonLeads),
            rows.Sum(r => r.Jobs), rows.Sum(r => r.Contract), SpendFrom(counted), Total));
        return rows;
    }

    /// <summary>One pill's totals (see <see cref="Compare"/>).</summary>
    public static ChannelSummary Group(string group, IReadOnlyList<LeadSource> sources, IReadOnlyList<SpendLine> counted, string googleChannel, long? clicks, JobTreadLeads? jtLeads)
    {
        var rows = SourcesIn(sources, group, googleChannel).ToList();
        var spend = SpendIn(counted, group, googleChannel).ToList();
        return new ChannelSummary(group, Label(group), spend.Sum(l => l.Amount), clicks, jtLeads is null ? null : rows.Sum(r => r.JtLeads ?? 0),
            rows.Sum(r => r.WonLeads), rows.Sum(r => r.Jobs), rows.Sum(r => r.Contract), SpendFrom(spend), group);
    }

    /// <summary>A single channel row inside Other (from its lead source row and counted spend).</summary>
    public static ChannelSummary Single(LeadSource s, IReadOnlyList<SpendLine> counted, string googleChannel) => new(
        s.Source, s.Source, s.Spend, null, s.JtLeads, s.WonLeads, s.Jobs, s.Contract,
        SpendFrom(counted.Where(l => string.Equals(SalesService.ChannelFor(l.Channel, googleChannel), s.Source, StringComparison.OrdinalIgnoreCase))));
}
