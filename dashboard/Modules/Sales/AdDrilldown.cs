using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Modules.Sales;

/// <summary>
/// One new JobTread account in a leads drill-down: the account, the lead source row it's counted in (null = no Lead
/// Source) and its sold jobs (the same sold-job list the counts use: open + closed in the last year, with a contract).
/// </summary>
public sealed record LeadRow(LeadAccount Account, string? Key, IReadOnlyList<ProjectSummary> Jobs)
{
    public bool Sold => Jobs.Count > 0;
    public decimal Contract => Jobs.Sum(j => j.Contract ?? 0);
    /// <summary>"How Did Bid Come In" from its sold job(s), when there is one.</summary>
    public string? HowBidCameIn => Jobs.Select(j => j.HowBidCameIn).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
    /// <summary>The ad click id the website form turns into this Lead Source (gclid → Google Ad, fbclid → Meta, ndclid → NextDoor).</summary>
    public string? ClickId => AdDrilldown.ClickIdFor(Account.LeadSource);
}

/// <summary>
/// The JobTread records behind one lead or jobs-sold figure on Sales → Ads &amp; lead sources (<c>?list=leads|sold</c>,
/// with <c>group=facebook|google|other|none</c> or <c>source=&lt;lead source row&gt;</c>, none of them = the total). Built
/// from the same lists and the same grouping as the figures (<see cref="SalesService.SourceKey(ProjectSummary,string)"/>,
/// <see cref="AdChannels.SourcesIn"/>), so the list length is the number clicked; Expected is that number, from the
/// summary rows, and the page warns if they ever differ.
/// </summary>
public sealed record AdDrilldown(string List, string Scope, int? Expected, IReadOnlyList<LeadRow> Leads, IReadOnlyList<ProjectSummary> Jobs, bool Found = true)
{
    public const string LeadsList = "leads";
    public const string SoldList = "sold";

    public bool IsLeads => List == LeadsList;
    public int Count => IsLeads ? Leads.Count : Jobs.Count;
    public decimal Contract => IsLeads ? Leads.Sum(l => l.Contract) : Jobs.Sum(j => j.Contract ?? 0);
    public bool Matches => Expected is null || Expected == Count;

    public static string? ParseList(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        LeadsList => LeadsList,
        SoldList or "jobs" => SoldList,
        _ => null,
    };

    public static string? ClickIdFor(string? leadSource) => leadSource?.Trim().ToLowerInvariant() switch
    {
        "google ad" => "gclid",
        "meta" => "fbclid",
        "nextdoor" => "ndclid",
        _ => null,
    };

    /// <summary>
    /// The drill-down for <paramref name="list"/> in a scope: a lead source row (<paramref name="source"/>, matched like the
    /// table: case-insensitive), a pill group or "none" (<paramref name="group"/>), else everything (the Total row).
    /// <paramref name="jobs"/> is the sold-job list the figures were built from.
    /// </summary>
    public static AdDrilldown Build(string list, string? group, string? source, IReadOnlyList<LeadSource> sources, IReadOnlyList<ChannelSummary> channels,
        JobTreadLeads? jtLeads, IReadOnlyList<ProjectSummary> jobs, string googleChannel)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var accounts = jtLeads?.Accounts ?? [];
        var jobsByCustomer = jobs.Where(j => j.CustomerId is not null).GroupBy(j => j.CustomerId!).ToDictionary(g => g.Key, g => (IReadOnlyList<ProjectSummary>)g.ToList());
        LeadRow Row(LeadAccount a) => new(a, SalesService.SourceKey(a, googleChannel), jobsByCustomer.GetValueOrDefault(a.Id) ?? []);

        AdDrilldown Make(string scope, Func<string?, bool> leadKeyIn, Func<string, bool> jobKeyIn, int? expectedLeads, int? expectedJobs, bool found = true)
        {
            var leads = list == LeadsList ? accounts.Select(Row).Where(r => leadKeyIn(r.Key)).ToList() : [];
            var sold = list == SoldList
                ? jobs.Where(j => jobKeyIn(SalesService.SourceKey(j, googleChannel)))
                    .OrderByDescending(j => j.FirstApprovedOrderOn ?? DateOnly.MinValue).ThenByDescending(j => int.TryParse(j.Number, out var n) ? n : 0).ToList()
                : [];
            return new AdDrilldown(list, scope, list == LeadsList ? expectedLeads : expectedJobs, leads, sold, found);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var row = sources.FirstOrDefault(s => cmp.Equals(s.Source, source.Trim()));
            if (row is null) return Make(source.Trim(), _ => false, _ => false, 0, 0, found: false);
            // Rows without a Lead Source ("No lead source (bid: …)") hold sold jobs only; new accounts without one are under group=none.
            // JobTread has a Lead Source called "Other"; say so, so it doesn't read as the Other pill.
            var scope = AdChannels.Pills.Any(p => cmp.Equals(p.Label, row.Source)) ? $"Lead source “{row.Source}”" : row.Source;
            return Make(scope, k => !row.NoLeadSource && k is not null && cmp.Equals(k, row.Source), k => cmp.Equals(k, row.Source), row.JtLeads, row.Jobs);
        }

        var g = group?.Trim().ToLowerInvariant();
        if (g is AdChannels.Facebook or AdChannels.Google or AdChannels.Other)
        {
            var keys = AdChannels.SourcesIn(sources, g, googleChannel).Select(s => s.Source).ToHashSet(cmp);
            var summary = channels.FirstOrDefault(c => c.Key == g);
            return Make(AdChannels.Label(g), k => k is not null && keys.Contains(k), keys.Contains, summary?.JtLeads, summary?.Jobs ?? 0);
        }
        if (g == AdChannels.NoLeadSource)
        {
            var keys = sources.Where(s => s.NoLeadSource).Select(s => s.Source).ToHashSet(cmp);
            var summary = channels.FirstOrDefault(c => c.Key == AdChannels.NoLeadSource);
            return Make("No lead source", k => k is null, keys.Contains, summary?.JtLeads ?? 0, summary?.Jobs ?? 0);
        }
        var total = channels.FirstOrDefault(c => c.Key == AdChannels.Total);
        return Make("All channels", _ => true, _ => true, total?.JtLeads, total?.Jobs);
    }
}
