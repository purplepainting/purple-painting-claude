using PurpleDashboard.Data;
using PurpleDashboard.Integrations.Google;

namespace PurpleDashboard.Modules.Customers;

/// <summary>
/// Google reviews for the Customers page. <see cref="Problem"/> is set (and <see cref="Data"/> null) when Google isn't set
/// up, connected or answering. ByCustomer holds each matched customer's newest review, keyed by <see cref="CustomersService.Key(CustomerView)"/>.
/// </summary>
public sealed record GoogleReviewsView(
    GoogleConnection? Connection, GoogleReviews? Data, string? Problem, string? ProblemKind,
    IReadOnlyList<ReviewMatch> Matches, IReadOnlyList<UnmatchedReview> Unmatched, IReadOnlyDictionary<string, ReviewMatch> ByCustomer)
{
    public bool Ready => Data is not null;

    public static GoogleReviewsView Fail(GoogleConnection? c, string problem, string kind) => new(c, null, problem, kind, [], [], new Dictionary<string, ReviewMatch>());

    public ReviewMatch? For(CustomerView c) => ByCustomer.GetValueOrDefault(CustomersService.Key(c));
}

/// <summary>
/// Reads the Business Profile's Google reviews (read only) and matches reviewers to customers by name. Display only:
/// nothing is written to JobTread (its "Left Review" field stays the record, updated by hand) or to Google.
/// </summary>
public sealed class GoogleReviewsService(GoogleClient google, ILogger<GoogleReviewsService> logger)
{
    public async Task<GoogleReviewsView> GetAsync(IReadOnlyList<CustomerView> customers, bool refresh, CancellationToken ct)
    {
        if (refresh) GoogleClient.ClearReviewsCache();
        if (!google.IsConfigured) return GoogleReviewsView.Fail(null, "Google isn't set up yet (see docs/GOOGLE.md).", "not-configured");
        GoogleConnection? connection = null;
        try
        {
            connection = await google.GetConnectionAsync(ct);
            if (connection is null) return GoogleReviewsView.Fail(null, "Connect Google on the Connections page to see Google reviews here.", "not-connected");
            if (GoogleClient.MissingScopes(connection).Contains(GoogleOptions.BusinessScope))
                return GoogleReviewsView.Fail(connection, "Google was connected without Business Profile access. Reconnect on Connections and allow it.", "scope");
            if (connection.BusinessLocation is null)
            {
                // Say why when the locations can't be listed yet (e.g. Business Profile API access not approved).
                await google.ListLocationsAsync(ct);
                return GoogleReviewsView.Fail(connection, "Pick the Business Profile location on the Connections page.", "location");
            }

            var data = await google.ReviewsAsync(connection, ct);
            var (matches, unmatched) = ReviewMatcher.Match(data.Reviews, customers);
            var byCustomer = matches.GroupBy(m => CustomersService.Key(m.Customer))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Review.Created).First());
            return new GoogleReviewsView(connection, data, null, null, matches, unmatched, byCustomer);
        }
        catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading Google reviews failed");
            return GoogleReviewsView.Fail(connection, e is GoogleException ? e.Message : "Couldn't reach Google. Try again in a minute.", (e as GoogleException)?.Kind ?? "error");
        }
    }
}
