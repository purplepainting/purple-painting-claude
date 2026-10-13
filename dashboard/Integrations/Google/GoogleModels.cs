namespace PurpleDashboard.Integrations.Google;

/// <summary>
/// A Google Ads account the connected login can read. <see cref="LoginCustomerId"/> is the manager account the calls go
/// through (null when the login has direct access to the account).
/// </summary>
public sealed record AdsAccount(string CustomerId, string Name, string? LoginCustomerId, bool IsManager, string? Currency, bool IsTest)
{
    /// <summary>"123-456-7890", the way Google Ads shows account ids.</summary>
    public string DisplayId => FormatId(CustomerId);

    /// <summary>Form value for the account picker: "customerId|loginCustomerId".</summary>
    public string Value => CustomerId + "|" + LoginCustomerId;

    public static string FormatId(string id) => id.Length == 10 ? $"{id[..3]}-{id[3..6]}-{id[6..]}" : id;
}

/// <summary>A Business Profile location the connected login manages. Account = "accounts/123", Location = "locations/456".</summary>
public sealed record BusinessLocation(string Account, string AccountName, string Location, string Title, string? Address, string? MapsUri, string? NewReviewUri)
{
    public string Value => Account + "|" + Location;
}

/// <summary>One campaign's totals for one month (Google Ads API, segments.month). Cost is in the account's currency.</summary>
public sealed record AdsCampaignMonth(string Month, string CampaignId, string CampaignName, string? Status, decimal Cost, long Impressions, long Clicks, decimal Conversions, long? PhoneCalls);

/// <summary>Campaign-by-month rows for the last 12 months of the chosen Ads account.</summary>
public sealed record AdsReport(string CustomerId, IReadOnlyList<AdsCampaignMonth> Rows, bool PhoneCallsAvailable, DateTime LoadedAtUtc);

/// <summary>A Google review of the business (Business Profile API v4). Stars 1-5 (0 if Google didn't say).</summary>
public sealed record GoogleReview(string Id, string Reviewer, bool Anonymous, int Stars, string? Comment, DateTimeOffset Created, DateTimeOffset? Updated, string? Reply, DateTimeOffset? RepliedAt);

/// <summary>All reviews for the chosen location, newest first, with Google's own average and count.</summary>
public sealed record GoogleReviews(string Location, IReadOnlyList<GoogleReview> Reviews, decimal? AverageRating, int TotalCount, DateTime LoadedAtUtc);
