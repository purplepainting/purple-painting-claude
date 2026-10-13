using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;

namespace PurpleDashboard.Integrations.QuickBooks;

/// <summary>
/// Read-only access to the connected QuickBooks Online company (Accounting API).
/// Connecting: /quickbooks/connect sends someone to Intuit to approve; Intuit sends them back to
/// /quickbooks/callback with a code, which is swapped for tokens here. Access tokens last an hour
/// and are kept in memory; the refresh token (100 days, replaced on each refresh) is saved encrypted
/// in the dashboard database. This class never writes to QuickBooks.
/// Query and report results are kept until the next 5am (QuickBooks only needs a daily refresh) or until
/// someone presses Refresh on a QuickBooks page (<see cref="ClearCache"/>).
/// </summary>
public sealed class QuickBooksClient(
    HttpClient http,
    IOptions<QuickBooksOptions> options,
    IServiceProvider services,
    DatabaseStatus database,
    IDataProtectionProvider protection,
    IMemoryCache cache,
    ILogger<QuickBooksClient> logger)
{
    private const string AuthorizeUrl = "https://appcenter.intuit.com/connect/oauth2";
    private const string TokenUrl = "https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer";
    private const string RevokeUrl = "https://developer.api.intuit.com/v2/oauth2/tokens/revoke";
    private const string MinorVersion = "75";

    // One access token for the whole app; refreshing is serialized so two requests don't both spend the refresh token.
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static (string Token, string RealmId, DateTime ExpiresAt)? access;

    private const int DailyRefreshHour = 5;
    private static CancellationTokenSource resetSignal = new();
    private static readonly ConcurrentDictionary<string, DateTime> LoadedAt = new();

    /// <summary>Drop all cached QuickBooks data (the Refresh button on QuickBooks pages).</summary>
    public static void ClearCache()
    {
        LoadedAt.Clear();
        var old = Interlocked.Exchange(ref resetSignal, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    /// <summary>When the oldest QuickBooks data currently shown was pulled (local time), or null if none is cached.</summary>
    public static DateTime? DataAsOf => LoadedAt.IsEmpty ? null : TimeZoneInfo.ConvertTimeFromUtc(LoadedAt.Values.Min(), LocalClock.Zone);

    private static DateTimeOffset NextDailyRefresh()
    {
        var now = LocalClock.Now;
        var next = now.Date.AddHours(DailyRefreshHour);
        if (next <= now) next = next.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next, LocalClock.Zone), TimeSpan.Zero);
    }

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> load)
    {
        key = "qb:" + key;
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpiration = NextDailyRefresh();
            entry.AddExpirationToken(new CancellationChangeToken(resetSignal.Token));
            entry.RegisterPostEvictionCallback((k, _, _, _) => LoadedAt.TryRemove((string)k, out _));
            var value = await load();
            LoadedAt[key] = DateTime.UtcNow;
            return value;
        }))!;
    }

    private IDataProtector Protector => protection.CreateProtector("QuickBooks.RefreshToken");
    private QuickBooksOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;

    /// <summary>QBO_REDIRECT_URI if set; otherwise pages build https://{host}/quickbooks/callback.</summary>
    public string? RedirectUriSetting => string.IsNullOrWhiteSpace(Options.RedirectUri) ? null : Options.RedirectUri;

    /// <summary>The saved connection, or null if QuickBooks isn't connected (or the database isn't available).</summary>
    public async Task<QuickBooksConnection?> GetConnectionAsync(CancellationToken ct)
    {
        if (Options.FixtureDirectory is not null)
            return new QuickBooksConnection { RealmId = "fixture", CompanyName = "Purple Painting (sample data)", RefreshTokenProtected = "", ConnectedAt = DateTime.UtcNow, RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(100) };
        if (!database.Available) return null;
        var db = services.GetRequiredService<DashboardDb>();
        return await db.QuickBooksConnections.AsNoTracking().OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>Intuit's approval page. <paramref name="state"/> is checked again in the callback.</summary>
    public string AuthorizeUri(string redirectUri, string state) =>
        $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(Options.ClientId!)}&response_type=code"
        + $"&scope={Uri.EscapeDataString(Options.Scopes)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

    /// <summary>Finish connecting: swap the code for tokens, save the connection, read the company name.</summary>
    public async Task<QuickBooksConnection> CompleteConnectAsync(string code, string realmId, string redirectUri, string user, CancellationToken ct)
    {
        var tokens = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        }, ct);

        var db = services.GetRequiredService<DashboardDb>();
        db.QuickBooksConnections.RemoveRange(db.QuickBooksConnections);
        var connection = new QuickBooksConnection
        {
            RealmId = realmId,
            RefreshTokenProtected = Protector.Protect(tokens.RefreshToken),
            RefreshTokenExpiresAt = DateTime.UtcNow.AddSeconds(tokens.RefreshExpiresIn),
            ConnectedAt = DateTime.UtcNow,
            ConnectedBy = user,
            Scopes = Options.Scopes,
        };
        db.QuickBooksConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        access = (tokens.AccessToken, realmId, DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 120));
        ClearCache();

        try
        {
            var info = await GetAsync($"companyinfo/{realmId}", ct);
            connection.CompanyName = info["CompanyInfo"]?["CompanyName"]?.GetValue<string>();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is QuickBooksException or HttpRequestException)
        {
            logger.LogWarning(e, "Connected to QuickBooks but reading the company name failed");
        }
        return connection;
    }

    /// <summary>Disconnect: revoke the token at Intuit (best effort) and forget the connection.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        ClearCache();
        var db = services.GetRequiredService<DashboardDb>();
        foreach (var c in await db.QuickBooksConnections.ToListAsync(ct))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUrl)
                {
                    Content = new StringContent(new JsonObject { ["token"] = Protector.Unprotect(c.RefreshTokenProtected) }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = BasicAuth();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var _ = await http.SendAsync(request, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Revoking the QuickBooks token failed; forgetting it anyway");
            }
            db.QuickBooksConnections.Remove(c);
        }
        await db.SaveChangesAsync(ct);
        access = null;
    }

    /// <summary>
    /// A query in QuickBooks' SQL-like language, e.g. "select * from Invoice where Balance &gt; '0'".
    /// <paramref name="entity"/> is the result list to read (e.g. "Invoice"). Follows pages of up to 1000 rows.
    /// </summary>
    public async Task<List<JsonNode>> QueryAsync(string entity, string query, CancellationToken ct) =>
        (await CachedAsync("query:" + query, () => LoadQueryAsync(entity, query, ct))).Select(n => n.DeepClone()).ToList();

    private async Task<List<JsonNode>> LoadQueryAsync(string entity, string query, CancellationToken ct)
    {
        if (Options.FixtureDirectory is not null)
            return ((await FixtureAsync($"query-{entity}", ct))["QueryResponse"]?[entity] as JsonArray ?? []).OfType<JsonNode>().Select(n => n.DeepClone()).ToList();
        var rows = new List<JsonNode>();
        for (var start = 1; ; start += 1000)
        {
            var page = await GetAsync($"query?query={Uri.EscapeDataString($"{query} STARTPOSITION {start} MAXRESULTS 1000")}", ct);
            var list = page["QueryResponse"]?[entity] as JsonArray;
            if (list is null || list.Count == 0) break;
            rows.AddRange(list.OfType<JsonNode>().Select(n => n.DeepClone()));
            if (list.Count < 1000) break;
        }
        return rows;
    }

    /// <summary>A report, e.g. ReportAsync("ProfitAndLoss", new() { ["start_date"] = "2026-01-01", ["end_date"] = "2026-10-12" }).</summary>
    public async Task<JsonNode> ReportAsync(string report, Dictionary<string, string> parameters, CancellationToken ct) =>
        (await CachedAsync("report:" + report + "?" + string.Join("&", parameters.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)), () => LoadReportAsync(report, parameters, ct))).DeepClone();

    private Task<JsonNode> LoadReportAsync(string report, Dictionary<string, string> parameters, CancellationToken ct) =>
        Options.FixtureDirectory is not null ? FixtureAsync(report + (parameters.TryGetValue("summarize_column_by", out var by) && by != "Month" ? "-" + by : ""), ct) : GetAsync($"reports/{report}?" + string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")), ct);

    private async Task<JsonNode> FixtureAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(Options.FixtureDirectory!, name + ".json");
        if (!File.Exists(path)) throw new QuickBooksException($"No QuickBooks fixture for '{name}' at {path}.");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
    }

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        var (token, realmId) = await GetAccessTokenAsync(ct);
        var url = $"{Options.ApiBase}v3/company/{realmId}/{path}{(path.Contains('?') ? "&" : "?")}minorversion={MinorVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var started = DateTime.UtcNow;
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("QuickBooks GET {Path} {Status} in {Ms} ms", path.Split('?')[0], (int)response.StatusCode, (DateTime.UtcNow - started).TotalMilliseconds);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) access = null;
        if (!response.IsSuccessStatusCode)
            throw new QuickBooksException($"QuickBooks returned {(int)response.StatusCode} for {path.Split('?')[0]}: {Trim(text)}");
        return JsonNode.Parse(text) ?? throw new QuickBooksException("QuickBooks returned an empty response.");
    }

    private async Task<(string Token, string RealmId)> GetAccessTokenAsync(CancellationToken ct)
    {
        if (access is { } a && a.ExpiresAt > DateTime.UtcNow) return (a.Token, a.RealmId);
        if (!IsConfigured) throw new QuickBooksException("QuickBooks isn't set up: the QBO_CLIENT_ID and QBO_CLIENT_SECRET settings are missing.");

        await RefreshLock.WaitAsync(ct);
        try
        {
            if (access is { } again && again.ExpiresAt > DateTime.UtcNow) return (again.Token, again.RealmId);

            if (!database.Available) throw new QuickBooksException("The dashboard database isn't available, so the QuickBooks connection can't be read.");
            var db = services.GetRequiredService<DashboardDb>();
            var connection = await db.QuickBooksConnections.OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct)
                ?? throw new QuickBooksException("QuickBooks isn't connected yet. Connect it on the Connections page.");

            string refreshToken;
            try { refreshToken = Protector.Unprotect(connection.RefreshTokenProtected); }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new QuickBooksException("The saved QuickBooks connection can't be read anymore. Reconnect QuickBooks on the Connections page.", e);
            }

            var tokens = await TokenRequestAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);
            connection.RefreshTokenProtected = Protector.Protect(tokens.RefreshToken);
            connection.RefreshTokenExpiresAt = DateTime.UtcNow.AddSeconds(tokens.RefreshExpiresIn);
            await db.SaveChangesAsync(ct);

            access = (tokens.AccessToken, connection.RealmId, DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 120));
            return (tokens.AccessToken, connection.RealmId);
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, int ExpiresIn, int RefreshExpiresIn);

    private async Task<Tokens> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = BasicAuth();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant: the refresh token expired or was revoked (or the code was already used).
            if (text.Contains("invalid_grant")) access = null;
            throw new QuickBooksException(text.Contains("invalid_grant")
                ? "QuickBooks needs to be reconnected (the saved approval expired or was revoked)."
                : $"QuickBooks sign-in failed ({(int)response.StatusCode}): {Trim(text)}");
        }
        var json = JsonNode.Parse(text)!;
        return new Tokens(
            json["access_token"]!.GetValue<string>(),
            json["refresh_token"]!.GetValue<string>(),
            json["expires_in"]?.GetValue<int>() ?? 3600,
            json["x_refresh_token_expires_in"]?.GetValue<int>() ?? 8_640_000);
    }

    private AuthenticationHeaderValue BasicAuth() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Options.ClientId}:{Options.ClientSecret}")));

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
