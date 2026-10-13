using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;

namespace PurpleDashboard.Integrations.Meta;

public sealed record MetaAdAccount(string Id, string Name, string? Business, string? Currency, bool Active);

/// <summary>
/// Read-only access to Facebook/Instagram ads through the Meta Marketing API (Graph API, version in
/// <see cref="MetaOptions.GraphVersion"/>). One shared company connection.
/// Connecting: /connections/meta?handler=connect sends someone to Facebook Login (scope ads_read); Facebook sends
/// them back to /connections/meta/callback with a code, which is swapped for a short-lived token and then for a
/// long-lived one (about 60 days), saved encrypted. Meta can't renew that token without the person signing in
/// again, so the Connections card asks for a reconnect when fewer than 10 days are left.
/// Every call sends appsecret_proof. Insights are kept until the next 5am (Pacific) or until someone presses Refresh
/// (<see cref="ClearCache"/>). This class never changes anything in the ad account.
/// </summary>
public sealed class MetaClient(
    HttpClient http,
    IOptions<MetaOptions> options,
    IServiceProvider services,
    DatabaseStatus database,
    IDataProtectionProvider protection,
    IMemoryCache cache,
    ILogger<MetaClient> logger)
{
    /// <summary>Days before the token expires when the Connections card starts asking for a reconnect.</summary>
    public const int ReconnectWarningDays = 10;

    private const int DailyRefreshHour = 5;
    private static CancellationTokenSource resetSignal = new();
    private static readonly ConcurrentDictionary<string, DateTime> LoadedAt = new();

    /// <summary>Drop all cached Meta data (the Refresh link in the Facebook/Instagram section).</summary>
    public static void ClearCache()
    {
        LoadedAt.Clear();
        var old = Interlocked.Exchange(ref resetSignal, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    /// <summary>When the oldest Meta data currently cached was pulled (local time), or null if none is cached.</summary>
    public static DateTime? DataAsOf => LoadedAt.IsEmpty ? null : TimeZoneInfo.ConvertTimeFromUtc(LoadedAt.Values.Min(), LocalClock.Zone);

    private static DateTimeOffset NextDailyRefresh()
    {
        var now = LocalClock.Now;
        var next = now.Date.AddHours(DailyRefreshHour);
        if (next <= now) next = next.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next, LocalClock.Zone), TimeSpan.Zero);
    }

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> load, TimeSpan? lifetime = null)
    {
        key = "meta:" + key;
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            if (lifetime is { } l) entry.AbsoluteExpirationRelativeToNow = l;
            else entry.AbsoluteExpiration = NextDailyRefresh();
            entry.AddExpirationToken(new CancellationChangeToken(resetSignal.Token));
            entry.RegisterPostEvictionCallback((k, _, _, _) => LoadedAt.TryRemove((string)k, out _));
            var value = await load();
            if (lifetime is null) LoadedAt[key] = DateTime.UtcNow;
            return value;
        }))!;
    }

    private IDataProtector Protector => protection.CreateProtector("Meta.AccessToken");
    private MetaOptions Options => options.Value;
    private bool Fixtures => Options.FixtureDirectory is not null;

    public bool IsConfigured => Options.IsConfigured;
    public bool HasAppId => !string.IsNullOrWhiteSpace(Options.AppId);
    public bool HasAppSecret => !string.IsNullOrWhiteSpace(Options.AppSecret);
    public bool UsingFixtures => Fixtures;

    /// <summary>META_REDIRECT_URI if set; otherwise pages build https://{host}/connections/meta/callback.</summary>
    public string? RedirectUriSetting => string.IsNullOrWhiteSpace(Options.RedirectUri) ? null : Options.RedirectUri;

    /// <summary>The saved connection, or null if Meta isn't connected (or the database isn't available).</summary>
    public async Task<MetaConnection?> GetConnectionAsync(CancellationToken ct)
    {
        MetaConnection? saved = null;
        if (database.Available)
            saved = await services.GetRequiredService<DashboardDb>().MetaConnections.AsNoTracking().OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct);
        if (saved is not null || !Fixtures) return saved;

        // Local sample data: pretend someone connected and picked the first ad account.
        var first = (await AdAccountsFromFixtureAsync(ct)).FirstOrDefault();
        return new MetaConnection
        {
            UserName = "Sample User", AccessTokenProtected = "", ConnectedAt = DateTime.UtcNow.AddDays(-12), ConnectedBy = "dev@localhost",
            ExpiresAt = DateTime.UtcNow.AddDays(48), AdAccountId = first?.Id, AdAccountName = first?.Name,
        };
    }

    /// <summary>Facebook's login dialog. <paramref name="state"/> is checked again in the callback.</summary>
    public string AuthorizeUri(string redirectUri, string state)
    {
        var url = $"{MetaOptions.DialogUrl}?client_id={Uri.EscapeDataString(Options.AppId!)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}&response_type=code";
        return string.IsNullOrWhiteSpace(Options.LoginConfigId)
            ? url + $"&scope={Uri.EscapeDataString(MetaOptions.Scopes)}"
            : url + $"&config_id={Uri.EscapeDataString(Options.LoginConfigId)}";
    }

    /// <summary>
    /// Finish connecting: code → short-lived token → long-lived token, read the person's name, save.
    /// Keeps the previously picked ad account if this person can still see it; picks the only one if there's just one.
    /// </summary>
    public async Task<MetaConnection> CompleteConnectAsync(string code, string redirectUri, string user, CancellationToken ct)
    {
        var shortLived = await TokenAsync(new()
        {
            ["client_id"] = Options.AppId!, ["client_secret"] = Options.AppSecret!, ["redirect_uri"] = redirectUri, ["code"] = code,
        }, ct);
        var longLived = await TokenAsync(new()
        {
            ["grant_type"] = "fb_exchange_token", ["client_id"] = Options.AppId!, ["client_secret"] = Options.AppSecret!, ["fb_exchange_token"] = shortLived.AccessToken,
        }, ct);

        var token = longLived.AccessToken;
        var me = await GetJsonAsync("me?fields=id,name", token, ct);
        DateTime? expiresAt = longLived.ExpiresIn is > 0 ? DateTime.UtcNow.AddSeconds(longLived.ExpiresIn.Value) : await DebugExpiryAsync(token, ct);

        var db = services.GetRequiredService<DashboardDb>();
        var previous = await db.MetaConnections.OrderByDescending(c => c.ConnectedAt).FirstOrDefaultAsync(ct);
        var accounts = await LoadAdAccountsAsync(token, ct);
        var keep = accounts.FirstOrDefault(a => a.Id == previous?.AdAccountId) ?? (accounts.Count(a => a.Active) == 1 ? accounts.First(a => a.Active) : null);

        db.MetaConnections.RemoveRange(db.MetaConnections);
        var connection = new MetaConnection
        {
            UserId = me["id"]?.GetValue<string>(),
            UserName = me["name"]?.GetValue<string>(),
            AccessTokenProtected = Protector.Protect(token),
            ExpiresAt = expiresAt,
            AdAccountId = keep?.Id,
            AdAccountName = keep?.Name,
            ConnectedAt = DateTime.UtcNow,
            ConnectedBy = user,
        };
        db.MetaConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        ClearCache();
        return connection;
    }

    /// <summary>Disconnect: remove the app's permission for that Facebook user (best effort) and forget the token.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        ClearCache();
        var db = services.GetRequiredService<DashboardDb>();
        foreach (var c in await db.MetaConnections.ToListAsync(ct))
        {
            if (!Fixtures && c.AccessTokenProtected.Length > 0)
            {
                try
                {
                    var token = Protector.Unprotect(c.AccessTokenProtected);
                    using var request = new HttpRequestMessage(HttpMethod.Delete, $"{MetaOptions.GraphBase}me/permissions?access_token={Uri.EscapeDataString(token)}&appsecret_proof={Proof(token)}");
                    using var _ = await http.SendAsync(request, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Revoking the Meta permission failed; forgetting the token anyway");
                }
            }
            db.MetaConnections.Remove(c);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ad accounts the connected person can see (/me/adaccounts). Kept 10 minutes.</summary>
    public async Task<List<MetaAdAccount>> AdAccountsAsync(CancellationToken ct)
    {
        if (Fixtures) return await AdAccountsFromFixtureAsync(ct);
        var token = await TokenFromDbAsync(ct);
        return await CachedAsync("adaccounts", () => LoadAdAccountsAsync(token, ct), TimeSpan.FromMinutes(10));
    }

    public async Task SelectAdAccountAsync(MetaAdAccount account, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var c = await db.MetaConnections.OrderByDescending(x => x.ConnectedAt).FirstOrDefaultAsync(ct)
            ?? throw new MetaException("Facebook/Instagram ads aren't connected.");
        c.AdAccountId = account.Id;
        c.AdAccountName = account.Name;
        await db.SaveChangesAsync(ct);
        ClearCache();
    }

    /// <summary>
    /// GET /{adAccountId}/insights with the given parameters, all pages. Falls back to an asynchronous report job
    /// when Meta says the request is too big to answer directly. Kept until the next 5am.
    /// </summary>
    public async Task<List<JsonObject>> InsightsAsync(string adAccountId, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var key = "insights:" + adAccountId + "?" + string.Join("&", parameters.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        return (await CachedAsync(key, () => LoadInsightsAsync(adAccountId, parameters, ct))).Select(n => (JsonObject)n.DeepClone()).ToList();
    }

    private async Task<List<JsonObject>> LoadInsightsAsync(string adAccountId, Dictionary<string, string> parameters, CancellationToken ct)
    {
        if (Fixtures)
        {
            var name = parameters.ContainsKey("breakdowns") ? "insights-platform" : "insights-" + parameters.GetValueOrDefault("level", "account");
            return Rows(await FixtureAsync(name, ct));
        }
        var token = await TokenFromDbAsync(ct);
        var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        try
        {
            return await AllPagesAsync($"{adAccountId}/insights?{query}&limit=500", token, ct);
        }
        catch (MetaException e) when (e.TooMuchData)
        {
            logger.LogInformation("Meta insights too large for a direct answer; running an async report");
            return await AsyncReportAsync(adAccountId, parameters, token, ct);
        }
    }

    /// <summary>Meta's asynchronous insights job: start it, wait for it (up to ~2 minutes), read its rows.</summary>
    private async Task<List<JsonObject>> AsyncReportAsync(string adAccountId, Dictionary<string, string> parameters, string token, CancellationToken ct)
    {
        using var start = new HttpRequestMessage(HttpMethod.Post, $"{MetaOptions.GraphBase}{adAccountId}/insights")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(parameters) { ["access_token"] = token, ["appsecret_proof"] = Proof(token) }),
        };
        var started = await SendAsync(start, "insights (async)", ct);
        var runId = started["report_run_id"]?.GetValue<string>() ?? throw new MetaException("Meta didn't start the insights report.");
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var status = await GetJsonAsync($"{runId}?fields=async_status,async_percent_completion", token, ct);
            var s = status["async_status"]?.GetValue<string>();
            if (s == "Job Completed") return await AllPagesAsync($"{runId}/insights?limit=500", token, ct);
            if (s is "Job Failed" or "Job Skipped") throw new MetaException($"Meta's insights report failed ({s}). Try Refresh later.");
        }
        throw new MetaException("Meta's insights report is taking too long. Try Refresh in a few minutes.");
    }

    // ---- Graph plumbing ----

    private async Task<List<JsonObject>> AllPagesAsync(string pathAndQuery, string token, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        string? url = Url(pathAndQuery, token);
        for (var page = 0; url is not null && page < 50; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var json = await SendAsync(request, pathAndQuery.Split('?')[0], ct);
            rows.AddRange(Rows(json));
            // paging.next already carries the token and proof.
            url = json["paging"]?["next"]?.GetValue<string>();
        }
        return rows;
    }

    private static List<JsonObject> Rows(JsonNode json) => (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();

    private async Task<JsonNode> GetJsonAsync(string pathAndQuery, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(pathAndQuery, token));
        return await SendAsync(request, pathAndQuery.Split('?')[0], ct);
    }

    private string Url(string pathAndQuery, string token) =>
        $"{MetaOptions.GraphBase}{pathAndQuery}{(pathAndQuery.Contains('?') ? "&" : "?")}access_token={Uri.EscapeDataString(token)}&appsecret_proof={Proof(token)}";

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, string label, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("Meta {Method} {Path} {Status} in {Ms} ms", request.Method, label, (int)response.StatusCode, (DateTime.UtcNow - started).TotalMilliseconds);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if (!response.IsSuccessStatusCode || json?["error"] is not null) throw GraphError(json?["error"], (int)response.StatusCode, text);
        return json ?? throw new MetaException("Meta returned an empty response.");
    }

    private static MetaException GraphError(JsonNode? error, int status, string text)
    {
        var code = error?["code"]?.GetValue<int>() ?? 0;
        var sub = error?["error_subcode"]?.GetValue<int>() ?? 0;
        var msg = error?["message"]?.GetValue<string>() ?? Trim(text);
        var friendly = code switch
        {
            190 => "Meta no longer accepts the saved Facebook connection (expired, or the password or permissions changed). Reconnect on the Connections page.",
            4 or 17 or 32 or 613 or 80000 or 80004 => "Meta's rate limit was hit. Try again in a few minutes.",
            10 or 200 or 294 => $"Meta says the connected Facebook user doesn't have permission for this ad account ({msg}).",
            _ => $"Meta returned {status}: {msg}",
        };
        return new MetaException(friendly)
        {
            NeedsReconnect = code == 190,
            TooMuchData = code == 1 || sub == 1487534 || msg.Contains("reduce the amount of data", StringComparison.OrdinalIgnoreCase),
        };
    }

    private sealed record TokenResult(string AccessToken, int? ExpiresIn);

    private async Task<TokenResult> TokenAsync(Dictionary<string, string> query, CancellationToken ct)
    {
        var url = $"{MetaOptions.GraphBase}oauth/access_token?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        try
        {
            var json = await SendAsync(request, "oauth/access_token", ct);
            return new TokenResult(json["access_token"]!.GetValue<string>(), json["expires_in"]?.GetValue<long>() is { } s ? (int)Math.Min(s, int.MaxValue) : null);
        }
        catch (MetaException e) { throw new MetaException("Facebook sign-in failed: " + e.Message, e); }
    }

    /// <summary>When Meta didn't send expires_in, ask debug_token (app token = id|secret) when the token expires.</summary>
    private async Task<DateTime?> DebugExpiryAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{MetaOptions.GraphBase}debug_token?input_token={Uri.EscapeDataString(token)}&access_token={Uri.EscapeDataString(Options.AppId + "|" + Options.AppSecret)}");
            var json = await SendAsync(request, "debug_token", ct);
            var at = json["data"]?["expires_at"]?.GetValue<long>() ?? 0;
            return at > 0 ? DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime : null;
        }
        catch (Exception e) when (e is MetaException or HttpRequestException)
        {
            logger.LogWarning(e, "Couldn't read the Meta token expiry");
            return null;
        }
    }

    private async Task<List<MetaAdAccount>> LoadAdAccountsAsync(string token, CancellationToken ct)
    {
        return (await AllPagesAsync("me/adaccounts?fields=id,name,account_status,currency&limit=200", token, ct)).Select(ToAccount).ToList();
    }

    private async Task<List<MetaAdAccount>> AdAccountsFromFixtureAsync(CancellationToken ct) =>
        Rows(await FixtureAsync("adaccounts", ct)).Select(ToAccount).ToList();

    // account_status 1 = active; 2 disabled, 3 unsettled, 101 closed, etc.
    private static MetaAdAccount ToAccount(JsonObject a) => new(
        a["id"]!.GetValue<string>(),
        a["name"]?.GetValue<string>() ?? a["id"]!.GetValue<string>(),
        a["business"]?["name"]?.GetValue<string>(),
        a["currency"]?.GetValue<string>(),
        (a["account_status"]?.GetValue<int>() ?? 1) == 1);

    private async Task<string> TokenFromDbAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new MetaException("Facebook/Instagram ads aren't set up: the META_APP_ID and META_APP_SECRET settings are missing.");
        if (!database.Available) throw new MetaException("The dashboard database isn't available, so the Facebook connection can't be read.");
        var c = await services.GetRequiredService<DashboardDb>().MetaConnections.AsNoTracking().OrderByDescending(x => x.ConnectedAt).FirstOrDefaultAsync(ct)
            ?? throw new MetaException("Facebook/Instagram ads aren't connected yet. Connect on the Connections page.");
        if (c.ExpiresAt is { } exp && exp <= DateTime.UtcNow)
            throw new MetaException("The Facebook connection expired. Reconnect on the Connections page.") { NeedsReconnect = true };
        try { return Protector.Unprotect(c.AccessTokenProtected); }
        catch (CryptographicException e)
        {
            throw new MetaException("The saved Facebook connection can't be read anymore. Reconnect on the Connections page.", e) { NeedsReconnect = true };
        }
    }

    private async Task<JsonNode> FixtureAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(Options.FixtureDirectory!, name + ".json");
        if (!File.Exists(path)) throw new MetaException($"No Meta fixture for '{name}' at {path}.");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
    }

    /// <summary>appsecret_proof: HMAC-SHA256 of the token keyed with the app secret, so a leaked token alone can't call the API as this app.</summary>
    private string Proof(string token) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Options.AppSecret ?? ""), Encoding.UTF8.GetBytes(token)));

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
