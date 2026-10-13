using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;

namespace PurpleDashboard.Integrations.Google;

/// <summary>
/// Read-only access to the company's Google account: Google Ads (spend, clicks, conversions per campaign) and the
/// Google Business Profile (reviews). One shared connection, like QuickBooks.
/// Connecting: the Connections page sends someone to Google (offline access, consent screen every time so a refresh
/// token is always returned); Google sends them back to /connections/google/callback with a code, swapped for tokens
/// here. Access tokens (1 hour) stay in memory; the refresh token is saved encrypted (Data Protection) in the
/// GoogleConnections table. This class only reads: it never creates or changes ads, campaigns, the profile or replies.
/// Ads results are kept until the next 5am; reviews for 3 hours; Refresh on a page clears them.
/// </summary>
public sealed class GoogleClient(
    HttpClient http,
    IOptions<GoogleOptions> options,
    IServiceProvider services,
    DatabaseStatus database,
    IDataProtectionProvider protection,
    IMemoryCache cache,
    ILogger<GoogleClient> logger)
{
    /// <summary>
    /// Google Ads API version in every Ads URL. Google ships a major version about every 3 months and turns each off
    /// about a year later: bump this when Google's sunset email arrives (release notes:
    /// https://developers.google.com/google-ads/api/docs/release-notes). v25 = July 2026.
    /// </summary>
    public const string AdsApiVersion = "v25";

    private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string AdsBase = "https://googleads.googleapis.com/" + AdsApiVersion + "/";
    private const string AccountsBase = "https://mybusinessaccountmanagement.googleapis.com/v1/";
    private const string LocationsBase = "https://mybusinessbusinessinformation.googleapis.com/v1/";
    private const string ReviewsBase = "https://mybusiness.googleapis.com/v4/";

    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static (string Token, DateTime ExpiresAt)? access;

    private const int DailyRefreshHour = 5;
    private static readonly TimeSpan ReviewsFor = TimeSpan.FromHours(3);
    private static readonly TimeSpan ListsFor = TimeSpan.FromHours(1);
    private static CancellationTokenSource adsReset = new(), reviewsReset = new(), listsReset = new();
    private static readonly ConcurrentDictionary<string, DateTime> AdsLoadedAt = new(), ReviewsLoadedAt = new();

    // ---- Caches ----

    /// <summary>Drop cached Google Ads data (Refresh on Sales &amp; Marketing).</summary>
    public static void ClearAdsCache() => Reset(ref adsReset, AdsLoadedAt);

    /// <summary>Drop cached reviews (Refresh on Customers).</summary>
    public static void ClearReviewsCache() => Reset(ref reviewsReset, ReviewsLoadedAt);

    /// <summary>Drop everything, including the account and location lists (connect, disconnect, new selection).</summary>
    public static void ClearAll()
    {
        ClearAdsCache();
        ClearReviewsCache();
        Reset(ref listsReset, null);
    }

    private static void Reset(ref CancellationTokenSource source, ConcurrentDictionary<string, DateTime>? loaded)
    {
        loaded?.Clear();
        // Cancel only, never Dispose: another request may still be reading the old source's Token
        // (disposing it made that request throw ObjectDisposedException).
        var old = Interlocked.Exchange(ref source, new CancellationTokenSource());
        try { old.Cancel(); } catch (AggregateException) { }
    }

    /// <summary>When the Google Ads data shown was pulled (local time), or null.</summary>
    public static DateTime? AdsAsOf => AsOf(AdsLoadedAt);
    /// <summary>When the reviews shown were pulled (local time), or null.</summary>
    public static DateTime? ReviewsAsOf => AsOf(ReviewsLoadedAt);

    private static DateTime? AsOf(ConcurrentDictionary<string, DateTime> loaded) =>
        loaded.IsEmpty ? null : TimeZoneInfo.ConvertTimeFromUtc(loaded.Values.Min(), LocalClock.Zone);

    private static DateTimeOffset NextDailyRefresh()
    {
        var now = LocalClock.Now;
        var next = now.Date.AddHours(DailyRefreshHour);
        if (next <= now) next = next.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next, LocalClock.Zone), TimeSpan.Zero);
    }

    private async Task<T> CachedAsync<T>(string key, Action<ICacheEntry> expire, CancellationTokenSource reset, ConcurrentDictionary<string, DateTime>? loaded, Func<Task<T>> load)
    {
        key = "google:" + key;
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            expire(entry);
            entry.AddExpirationToken(new CancellationChangeToken(reset.Token));
            if (loaded is not null) entry.RegisterPostEvictionCallback((k, _, _, _) => loaded.TryRemove((string)k, out _));
            var value = await load();
            if (loaded is not null) loaded[key] = DateTime.UtcNow;
            return value;
        }))!;
    }

    // ---- Connection ----

    private IDataProtector Protector => protection.CreateProtector("Google.RefreshToken");
    private GoogleOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;
    public bool HasDeveloperToken => Options.HasDeveloperToken;
    public bool UsesFixtures => Options.UseFixtures;
    public string AdsChannel => Options.AdsChannel;
    /// <summary>GOOGLE_ADS_LOGIN_CUSTOMER_ID (digits only) when set.</summary>
    public string? LoginCustomerIdSetting => Digits(Options.AdsLoginCustomerId);
    public string? RedirectUriSetting => string.IsNullOrWhiteSpace(Options.RedirectUri) ? null : Options.RedirectUri;

    /// <summary>The saved connection, or null when Google isn't connected (or the database isn't available).</summary>
    public async Task<GoogleConnection?> GetConnectionAsync(CancellationToken ct)
    {
        if (database.Available)
        {
            var db = services.GetRequiredService<DashboardDb>();
            return await db.GoogleConnections.AsNoTracking().OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct);
        }
        if (!Options.UseFixtures) return null;

        // Fixtures without a database: act connected, with the first account and location picked.
        var email = (await FixtureAsync("userinfo", ct))["email"]?.GetValue<string>() ?? "owner@example.com";
        var connection = new GoogleConnection { Email = email, RefreshTokenProtected = "", Scopes = GoogleOptions.Scopes, ConnectedAt = DateTime.UtcNow, ConnectedBy = "sample data" };
        if ((await ListAdsAccountsAsync(ct)).Accounts.FirstOrDefault(a => !a.IsManager) is { } ads)
            (connection.AdsCustomerId, connection.AdsLoginCustomerId, connection.AdsCustomerName) = (ads.CustomerId, ads.LoginCustomerId, ads.Name);
        if ((await ListLocationsAsync(ct)).FirstOrDefault() is { } loc)
            (connection.BusinessAccount, connection.BusinessLocation, connection.BusinessLocationTitle) = (loc.Account, loc.Location, loc.Title);
        return connection;
    }

    /// <summary>Google's consent page. <paramref name="state"/> is checked again in the callback.</summary>
    public string AuthorizeUri(string redirectUri, string state) =>
        $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(Options.ClientId!)}&response_type=code"
        + $"&scope={Uri.EscapeDataString(GoogleOptions.Scopes)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&access_type=offline&prompt=consent&include_granted_scopes=true&state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// Finish connecting: swap the code for tokens, read the account's email from the ID token, save the connection.
    /// Reconnecting the same Google account keeps the Ads account and location already picked.
    /// </summary>
    public async Task<GoogleConnection> CompleteConnectAsync(string code, string redirectUri, string user, CancellationToken ct)
    {
        var json = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        }, ct);
        var refresh = json["refresh_token"]?.GetValue<string>()
            ?? throw new GoogleException("Google didn't send a long-lived approval. Click Connect Google again (and approve every box on Google's page).");
        var email = EmailFromIdToken(json["id_token"]?.GetValue<string>()) ?? "unknown";
        var scopes = json["scope"]?.GetValue<string>() ?? "";
        var connection = await SaveConnectionAsync(email, Protector.Protect(refresh), scopes, user, ct);
        access = (json["access_token"]!.GetValue<string>(), DateTime.UtcNow.AddSeconds((json["expires_in"]?.GetValue<int>() ?? 3600) - 120));
        return connection;
    }

    /// <summary>Local development with fixtures: "connects" without leaving the dashboard, as the fixture's account (userinfo.json).</summary>
    public async Task<GoogleConnection> ConnectWithFixturesAsync(string user, CancellationToken ct)
    {
        var email = (await FixtureAsync("userinfo", ct))["email"]?.GetValue<string>() ?? "owner@example.com";
        return await SaveConnectionAsync(email, Protector.Protect("fixture"), GoogleOptions.Scopes, user, ct);
    }

    private async Task<GoogleConnection> SaveConnectionAsync(string email, string refreshProtected, string scopes, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var previous = await db.GoogleConnections.OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct);
        var keep = previous is not null && string.Equals(previous.Email, email, StringComparison.OrdinalIgnoreCase);
        db.GoogleConnections.RemoveRange(db.GoogleConnections);
        var connection = new GoogleConnection
        {
            Email = email,
            RefreshTokenProtected = refreshProtected,
            Scopes = scopes,
            ConnectedAt = DateTime.UtcNow,
            ConnectedBy = user,
            AdsCustomerId = keep ? previous!.AdsCustomerId : null,
            AdsLoginCustomerId = keep ? previous!.AdsLoginCustomerId : null,
            AdsCustomerName = keep ? previous!.AdsCustomerName : null,
            BusinessAccount = keep ? previous!.BusinessAccount : null,
            BusinessLocation = keep ? previous!.BusinessLocation : null,
            BusinessLocationTitle = keep ? previous!.BusinessLocationTitle : null,
        };
        db.GoogleConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        access = null;
        ClearAll();
        return connection;
    }

    /// <summary>Disconnect: revoke the approval at Google (best effort) and forget the connection.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        ClearAll();
        var db = services.GetRequiredService<DashboardDb>();
        foreach (var c in await db.GoogleConnections.ToListAsync(ct))
        {
            if (!Options.UseFixtures)
            {
                try
                {
                    var token = Protector.Unprotect(c.RefreshTokenProtected);
                    using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUrl) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }) };
                    using var _ = await http.SendAsync(request, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Revoking the Google token failed; forgetting it anyway");
                }
            }
            db.GoogleConnections.Remove(c);
        }
        await db.SaveChangesAsync(ct);
        access = null;
    }

    /// <summary>Save which Google Ads account the dashboard reads.</summary>
    public async Task SelectAdsAccountAsync(AdsAccount account, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var c = await db.GoogleConnections.OrderByDescending(x => x.ConnectedAt).FirstOrDefaultAsync(ct) ?? throw new GoogleException("Google isn't connected.");
        (c.AdsCustomerId, c.AdsLoginCustomerId, c.AdsCustomerName) = (account.CustomerId, account.LoginCustomerId, account.Name);
        await db.SaveChangesAsync(ct);
        ClearAdsCache();
    }

    /// <summary>Save which Business Profile location the dashboard reads reviews from.</summary>
    public async Task SelectLocationAsync(BusinessLocation location, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var c = await db.GoogleConnections.OrderByDescending(x => x.ConnectedAt).FirstOrDefaultAsync(ct) ?? throw new GoogleException("Google isn't connected.");
        (c.BusinessAccount, c.BusinessLocation, c.BusinessLocationTitle) = (location.Account, location.Location, location.Title);
        await db.SaveChangesAsync(ct);
        ClearReviewsCache();
    }

    /// <summary>Scopes from the wanted list that the person unticked on Google's consent page.</summary>
    public static List<string> MissingScopes(GoogleConnection c)
    {
        var granted = (c.Scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return new[] { GoogleOptions.AdsScope, GoogleOptions.BusinessScope }.Where(s => !granted.Contains(s)).ToList();
    }

    // ---- Google Ads ----

    /// <summary>
    /// Every Ads account the login can read: accounts it has direct access to, plus the client accounts under any
    /// manager account it has (read through that manager). Problems with single accounts are returned as notes.
    /// </summary>
    public Task<(List<AdsAccount> Accounts, List<string> Notes)> ListAdsAccountsAsync(CancellationToken ct) =>
        CachedAsync("ads-accounts", e => e.AbsoluteExpirationRelativeToNow = ListsFor, listsReset, null, () => LoadAdsAccountsAsync(ct));

    private async Task<(List<AdsAccount>, List<string>)> LoadAdsAccountsAsync(CancellationToken ct)
    {
        RequireDeveloperToken();
        var list = await SendAsync("ads-listAccessibleCustomers", "ads", AdsRequest(HttpMethod.Get, "customers:listAccessibleCustomers", null, null), ct);
        var ids = (list["resourceNames"] as JsonArray ?? []).Select(n => n?.GetValue<string>()?.Replace("customers/", "")).OfType<string>().ToList();
        var accounts = new List<AdsAccount>();
        var notes = new List<string>();
        var setting = LoginCustomerIdSetting;
        foreach (var id in ids)
        {
            try
            {
                var rows = await SearchStreamAsync(id, setting ?? id,
                    "SELECT customer.id, customer.descriptive_name, customer.manager, customer.currency_code, customer.test_account FROM customer LIMIT 1",
                    $"ads-customer-{id}", ct);
                var c = rows.FirstOrDefault()?["customer"];
                if (c is null) continue;
                var manager = Bool(c["manager"]);
                accounts.Add(new AdsAccount(id, Str(c["descriptiveName"]) ?? AdsAccount.FormatId(id), setting, manager, Str(c["currencyCode"]), Bool(c["testAccount"])));
                if (!manager) continue;

                var clients = await SearchStreamAsync(id, id,
                    "SELECT customer_client.id, customer_client.descriptive_name, customer_client.manager, customer_client.currency_code, customer_client.test_account, customer_client.status "
                    + "FROM customer_client WHERE customer_client.level = 1 AND customer_client.status = 'ENABLED'",
                    $"ads-customer_client-{id}", ct);
                foreach (var row in clients)
                {
                    var cc = row["customerClient"];
                    var clientId = Str(cc?["id"]);
                    if (cc is null || clientId is null || Bool(cc["manager"])) continue;
                    accounts.Add(new AdsAccount(clientId, Str(cc["descriptiveName"]) ?? AdsAccount.FormatId(clientId), setting ?? id, false, Str(cc["currencyCode"]), Bool(cc["testAccount"])));
                }
            }
            catch (GoogleException e) when (e.Kind is not ("developer-token" or "api-disabled" or "reconnect"))
            {
                notes.Add($"{AdsAccount.FormatId(id)}: {e.Message}");
            }
        }
        // An account reachable both directly and through a manager: keep the direct one.
        var distinct = accounts.GroupBy(a => a.CustomerId).Select(g => g.FirstOrDefault(a => a.LoginCustomerId == setting) ?? g.First())
            .OrderBy(a => a.IsManager).ThenBy(a => a.Name).ToList();
        return (distinct, notes);
    }

    /// <summary>Campaign totals per month, from the first day of the month 11 months ago through today.</summary>
    public async Task<AdsReport> CampaignMonthsAsync(GoogleConnection c, CancellationToken ct)
    {
        if (c.AdsCustomerId is null) throw new GoogleException("No Google Ads account is picked yet. Pick one on the Connections page.") { Kind = "ads-account" };
        var from = new DateOnly(LocalClock.Today.Year, LocalClock.Today.Month, 1).AddMonths(-11);
        var login = LoginCustomerIdSetting ?? c.AdsLoginCustomerId;
        return await CachedAsync($"ads-months:{c.AdsCustomerId}:{login}:{from:yyyy-MM}", e => e.AbsoluteExpiration = NextDailyRefresh(), adsReset, AdsLoadedAt,
            () => LoadCampaignMonthsAsync(c.AdsCustomerId, login, from, ct));
    }

    private async Task<AdsReport> LoadCampaignMonthsAsync(string customerId, string? login, DateOnly from, CancellationToken ct)
    {
        RequireDeveloperToken();
        string Query(bool calls) =>
            "SELECT campaign.id, campaign.name, campaign.status, segments.month, metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions"
            + (calls ? ", metrics.phone_calls" : "")
            + $" FROM campaign WHERE segments.date BETWEEN '{from:yyyy-MM-dd}' AND '{LocalClock.Today:yyyy-MM-dd}' ORDER BY segments.month";

        List<JsonNode> rows;
        var calls = true;
        try
        {
            rows = await SearchStreamAsync(customerId, login, Query(true), "ads-campaign-months", ct);
        }
        catch (GoogleException e) when (e.Message.Contains("phone_calls", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("PROHIBITED", StringComparison.Ordinal))
        {
            // Phone calls aren't available for this account or report: carry on without them.
            logger.LogWarning("Google Ads phone_calls not available: {Message}", e.Message);
            calls = false;
            rows = await SearchStreamAsync(customerId, login, Query(false), "ads-campaign-months-nocalls", ct);
        }

        var list = rows.Select(r =>
        {
            var m = r["metrics"];
            var month = Str(r["segments"]?["month"]) ?? "";
            return new AdsCampaignMonth(
                month.Length >= 7 ? month[..7] : month,
                Str(r["campaign"]?["id"]) ?? "",
                Str(r["campaign"]?["name"]) ?? "(no name)",
                Str(r["campaign"]?["status"]),
                Long(m?["costMicros"]) / 1_000_000m,
                Long(m?["impressions"]),
                Long(m?["clicks"]),
                Dec(m?["conversions"]),
                calls ? Long(m?["phoneCalls"]) : null);
        }).ToList();
        return new AdsReport(customerId, list, calls, DateTime.UtcNow);
    }

    /// <summary>Runs a GAQL query with googleAds:searchStream and returns every result row.</summary>
    private async Task<List<JsonNode>> SearchStreamAsync(string customerId, string? loginCustomerId, string query, string fixture, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query }.ToJsonString();
        var response = await SendAsync(fixture, "ads", AdsRequest(HttpMethod.Post, $"customers/{customerId}/googleAds:searchStream", body, loginCustomerId), ct);
        var batches = response as JsonArray ?? [response];
        return batches.SelectMany(b => b?["results"] as JsonArray ?? []).OfType<JsonNode>().ToList();
    }

    private Func<string, HttpRequestMessage> AdsRequest(HttpMethod method, string path, string? body, string? loginCustomerId) => token =>
    {
        var request = new HttpRequestMessage(method, AdsBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("developer-token", Options.AdsDeveloperToken);
        if (!string.IsNullOrEmpty(loginCustomerId)) request.Headers.TryAddWithoutValidation("login-customer-id", loginCustomerId);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    };

    private void RequireDeveloperToken()
    {
        if (!Options.HasDeveloperToken)
            throw new GoogleException("The Google Ads developer token isn't set: add GOOGLE_ADS_DEVELOPER_TOKEN in Azure (docs/GOOGLE.md step 5).") { Kind = "developer-token" };
    }

    // ---- Business Profile ----

    /// <summary>Every location in every Business Profile account the login manages.</summary>
    public Task<List<BusinessLocation>> ListLocationsAsync(CancellationToken ct) =>
        CachedAsync("business-locations", e => e.AbsoluteExpirationRelativeToNow = ListsFor, listsReset, null, () => LoadLocationsAsync(ct));

    private async Task<List<BusinessLocation>> LoadLocationsAsync(CancellationToken ct)
    {
        var accounts = await PagedAsync("business-accounts", AccountsBase + "accounts?pageSize=20", "accounts", "business", ct);
        var result = new List<BusinessLocation>();
        foreach (var a in accounts)
        {
            var account = Str(a["name"]);
            if (account is null) continue;
            var fixture = "business-locations-" + account.Replace("accounts/", "");
            var locations = await PagedAsync(fixture,
                LocationsBase + account + "/locations?pageSize=100&readMask=name,title,storefrontAddress,metadata", "locations", "business", ct);
            foreach (var l in locations)
            {
                var name = Str(l["name"]);
                if (name is null) continue;
                var addr = l["storefrontAddress"];
                var lines = (addr?["addressLines"] as JsonArray ?? []).Select(x => x?.GetValue<string>()).Append(Str(addr?["locality"])).Where(s => !string.IsNullOrWhiteSpace(s));
                result.Add(new BusinessLocation(account, Str(a["accountName"]) ?? account, name, Str(l["title"]) ?? name, string.Join(", ", lines) is { Length: > 0 } s ? s : null,
                    Str(l["metadata"]?["mapsUri"]), Str(l["metadata"]?["newReviewUri"])));
            }
        }
        return result;
    }

    /// <summary>All reviews for the picked location (paged, up to 2,000), newest first.</summary>
    public async Task<GoogleReviews> ReviewsAsync(GoogleConnection c, CancellationToken ct)
    {
        if (c.BusinessAccount is null || c.BusinessLocation is null)
            throw new GoogleException("No Business Profile location is picked yet. Pick one on the Connections page.") { Kind = "location" };
        return await CachedAsync($"reviews:{c.BusinessLocation}", e => e.AbsoluteExpirationRelativeToNow = ReviewsFor, reviewsReset, ReviewsLoadedAt,
            () => LoadReviewsAsync(c.BusinessAccount, c.BusinessLocation, ct));
    }

    private async Task<GoogleReviews> LoadReviewsAsync(string account, string location, CancellationToken ct)
    {
        var reviews = new List<GoogleReview>();
        decimal? average = null;
        var total = 0;
        string? pageToken = null;
        for (var page = 0; page < 40; page++)
        {
            var url = $"{ReviewsBase}{account}/{location}/reviews?pageSize=50&orderBy=updateTime%20desc" + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var json = await SendAsync("business-reviews" + (pageToken is null ? "" : "-" + pageToken), "reviews", Get(url), ct);
            if (page == 0)
            {
                average = json["averageRating"] is { } avg ? Math.Round(Dec(avg), 1) : null;
                total = (int)Long(json["totalReviewCount"]);
            }
            foreach (var r in json["reviews"] as JsonArray ?? [])
            {
                if (r is null) continue;
                reviews.Add(new GoogleReview(
                    Str(r["reviewId"]) ?? Str(r["name"]) ?? Guid.NewGuid().ToString("N"),
                    Str(r["reviewer"]?["displayName"]) ?? "A Google user",
                    Bool(r["reviewer"]?["isAnonymous"]),
                    Str(r["starRating"]) switch { "ONE" => 1, "TWO" => 2, "THREE" => 3, "FOUR" => 4, "FIVE" => 5, _ => 0 },
                    Str(r["comment"]),
                    Time(r["createTime"]) ?? DateTimeOffset.MinValue,
                    Time(r["updateTime"]),
                    Str(r["reviewReply"]?["comment"]),
                    Time(r["reviewReply"]?["updateTime"])));
            }
            pageToken = Str(json["nextPageToken"]);
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        if (total == 0) total = reviews.Count;
        return new GoogleReviews(location, reviews.OrderByDescending(r => r.Created).ToList(), average, total, DateTime.UtcNow);
    }

    private async Task<List<JsonNode>> PagedAsync(string fixture, string url, string listName, string api, CancellationToken ct)
    {
        var items = new List<JsonNode>();
        string? pageToken = null;
        for (var page = 0; page < 20; page++)
        {
            var json = await SendAsync(fixture + (pageToken is null ? "" : "-" + pageToken), api,
                Get(url + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken))), ct);
            items.AddRange((json[listName] as JsonArray ?? []).OfType<JsonNode>());
            pageToken = Str(json["nextPageToken"]);
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        return items;
    }

    private static Func<string, HttpRequestMessage> Get(string url) => token =>
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    };

    // ---- HTTP, tokens, errors ----

    /// <summary>
    /// Sends a request with a fresh access token (or reads {fixture}.json with fixtures). A fixture whose top level is
    /// Google's error shape ({"error": {"code": 429, ...}}) is treated like that error response.
    /// </summary>
    private async Task<JsonNode> SendAsync(string fixture, string api, Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        if (Options.UseFixtures)
        {
            var node = await FixtureAsync(fixture, ct);
            if (ErrorObject(node) is { } err)
                throw Error(api, (int)Long(err["code"]), node.ToJsonString());
            return node;
        }

        var token = await GetAccessTokenAsync(ct);
        using var request = build(token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var started = DateTime.UtcNow;
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("Google {Api} {Method} {Path} {Status} in {Ms} ms", api, request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode, (DateTime.UtcNow - started).TotalMilliseconds);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) access = null;
        if (!response.IsSuccessStatusCode) throw Error(api, (int)response.StatusCode, text);
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private async Task<JsonNode> FixtureAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(Options.FixtureDirectory!, name + ".json");
        if (!File.Exists(path)) throw new GoogleException($"No Google fixture for '{name}' at {path}.");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (access is { } a && a.ExpiresAt > DateTime.UtcNow) return a.Token;
        if (!IsConfigured) throw new GoogleException("Google isn't set up: the GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET settings are missing.") { Kind = "not-configured" };

        await RefreshLock.WaitAsync(ct);
        try
        {
            if (access is { } again && again.ExpiresAt > DateTime.UtcNow) return again.Token;
            if (!database.Available) throw new GoogleException("The dashboard database isn't available, so the Google connection can't be read.");
            var db = services.GetRequiredService<DashboardDb>();
            var connection = await db.GoogleConnections.OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct)
                ?? throw new GoogleException("Google isn't connected yet. Connect it on the Connections page.") { Kind = "not-connected" };

            string refreshToken;
            try { refreshToken = Protector.Unprotect(connection.RefreshTokenProtected); }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new GoogleException("The saved Google connection can't be read anymore. Reconnect Google on the Connections page.", e) { Kind = "reconnect" };
            }

            var json = await TokenRequestAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);
            if (json["refresh_token"]?.GetValue<string>() is { } rotated && rotated != refreshToken)
            {
                connection.RefreshTokenProtected = Protector.Protect(rotated);
                await db.SaveChangesAsync(ct);
            }
            var token = json["access_token"]!.GetValue<string>();
            access = (token, DateTime.UtcNow.AddSeconds((json["expires_in"]?.GetValue<int>() ?? 3600) - 120));
            return token;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private async Task<JsonNode> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = Options.ClientId ?? "";
        form["client_secret"] = Options.ClientSecret ?? "";
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if (text.Contains("invalid_grant"))
            {
                access = null;
                throw new GoogleException("Google needs to be reconnected (the saved approval expired or was removed). If this keeps happening every week, the OAuth consent screen is still in Testing: publish it (docs/GOOGLE.md step 3).") { Kind = "reconnect" };
            }
            throw new GoogleException(text.Contains("invalid_client")
                ? "Google doesn't recognise the dashboard's OAuth client: check GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET."
                : $"Google sign-in failed ({(int)response.StatusCode}): {Trim(text)}");
        }
        return JsonNode.Parse(text)!;
    }

    /// <summary>Google's {"error": {...}}; searchStream wraps it in an array: [{"error": {...}}].</summary>
    private static JsonObject? ErrorObject(JsonNode? node) =>
        (node is JsonArray a ? a.FirstOrDefault() : node) is JsonObject o ? o["error"] as JsonObject : null;

    /// <summary>Turns a Google error response into a message that says what to do about it.</summary>
    public static GoogleException Error(string api, int status, string text)
    {
        string? message = null;
        try { message = ErrorObject(JsonNode.Parse(text))?["message"]?.GetValue<string>(); } catch (JsonException) { }
        message ??= Trim(text);
        bool Has(string s) => text.Contains(s, StringComparison.Ordinal);
        var label = api switch { "ads" => "Google Ads API", "reviews" => "Google My Business API (reviews)", _ => "My Business Account Management / Business Information API" };

        if (Has("SERVICE_DISABLED") || Has("has not been used in project") || Has("it is disabled"))
            return new($"The {label} isn't turned on in the Google Cloud project. Enable it (docs/GOOGLE.md step 2), wait a few minutes, then try again.") { Kind = "api-disabled" };
        if (status == 401)
            return new("Google didn't accept the saved sign-in. Reconnect Google on the Connections page.") { Kind = "reconnect" };

        if (api == "ads")
        {
            if (Has("DEVELOPER_TOKEN_NOT_APPROVED"))
                return new("The Google Ads developer token only has test access, so it can't read the real Ads account yet. Apply for Basic access in the manager account's API Center (docs/GOOGLE.md step 5).") { Kind = "developer-token" };
            if (Has("DEVELOPER_TOKEN_INVALID") || Has("DEVELOPER_TOKEN_PARAMETER_MISSING"))
                return new("Google Ads doesn't accept the developer token: check GOOGLE_ADS_DEVELOPER_TOKEN (copy it again from API Center).") { Kind = "developer-token" };
            if (Has("DEVELOPER_TOKEN_PROHIBITED"))
                return new("This developer token is already tied to a different Google Cloud project. Use the OAuth client from that project, or ask Google to move the token.") { Kind = "developer-token" };
            if (Has("NOT_ADS_USER"))
                return new("The connected Google account isn't a user on any Google Ads account. Connect with the login that owns the Ads account.") { Kind = "ads-access" };
            if (Has("USER_PERMISSION_DENIED") || Has("CUSTOMER_NOT_FOUND"))
                return new("The connected Google account can't read that Ads account through the manager account used. Pick the account again, or check GOOGLE_ADS_LOGIN_CUSTOMER_ID.") { Kind = "ads-access" };
            if (Has("CUSTOMER_NOT_ENABLED"))
                return new("That Google Ads account isn't active (cancelled, suspended or never finished setting up).") { Kind = "ads-access" };
            if (Has("PROHIBITED_") || Has("phone_calls"))
                return new($"Google Ads rejected the report: {message}");
        }
        else
        {
            var quotaZero = Has("quota_limit_value\": \"0\"") || Has("quota_limit_value\":\"0\"");
            if (quotaZero)
                return new("Google hasn't approved Business Profile API access for this project yet (new projects get 0 requests a minute until the access request is approved, docs/GOOGLE.md step 6).") { Kind = "business-access" };
            if (status == 429)
                return new("Google says too many Business Profile requests right now. Try again in a minute.");
            if (status == 403)
                return new("The connected Google account can't manage that Business Profile (it needs to be an owner or manager of the profile).") { Kind = "business-access" };
        }
        return new($"Google returned {status}: {message}");
    }

    // ---- JSON helpers ----

    /// <summary>The email in a Google ID token. It comes straight from Google's token endpoint over TLS, so it isn't re-verified.</summary>
    private static string? EmailFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(payload))?["email"]?.GetValue<string>();
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    private static string? Digits(string? s) => string.IsNullOrWhiteSpace(s) ? null : new string(s.Where(char.IsDigit).ToArray()) is { Length: > 0 } d ? d : null;

    private static string? Str(JsonNode? n) => n is null ? null : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

    private static bool Bool(JsonNode? n) => n is not null && (n.GetValueKind() == JsonValueKind.True || n.GetValueKind() == JsonValueKind.String && n.GetValue<string>() == "true");

    private static long Long(JsonNode? n) => n is null ? 0
        : n.GetValueKind() == JsonValueKind.Number ? (long)n.GetValue<double>()
        : long.TryParse(n.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal Dec(JsonNode? n) => n is null ? 0
        : n.GetValueKind() == JsonValueKind.Number ? (decimal)n.GetValue<double>()
        : decimal.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static DateTimeOffset? Time(JsonNode? n) =>
        DateTimeOffset.TryParse(Str(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
