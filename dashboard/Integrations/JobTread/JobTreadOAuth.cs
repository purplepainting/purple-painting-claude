using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// "Sign in with JobTread": the same OAuth sign-in JobTread's Claude connector uses
/// (discovery: https://api.jobtread.com/.well-known/oauth-authorization-server). The dashboard
/// registers itself automatically (dynamic client registration), sends people to JobTread to approve
/// (authorization code + PKCE), and swaps the code for tokens. Queries made with a signed-in person's
/// token go either straight to Pave (if JobTread accepts the token as a grant key) or through
/// JobTread's MCP server's "query" tool at api.jobtread.com/mcp.
/// </summary>
public sealed class JobTreadOAuth(HttpClient http, IServiceProvider services, IDataProtectionProvider protection, IConfiguration config, ILogger<JobTreadOAuth> logger)
{
    // From JobTread's discovery document. Overridable (JobTread:OAuth:*) only for local testing against a stand-in.
    private string AuthorizeEndpoint => config["JobTread:OAuth:Authorize"] ?? "https://app.jobtread.com/oauth/authorize";
    private string TokenEndpoint => config["JobTread:OAuth:Token"] ?? "https://api.jobtread.com/oauth/token";
    private string RegistrationEndpoint => config["JobTread:OAuth:Register"] ?? "https://api.jobtread.com/oauth/register";
    private string McpResource => config["JobTread:OAuth:Mcp"] ?? "https://api.jobtread.com/mcp";
    private const string McpProtocolVersion = "2025-06-18";

    private IDataProtector Protector => protection.CreateProtector("JobTread.OAuth");

    public string Protect(string s) => Protector.Protect(s);
    public string Unprotect(string s) => Protector.Unprotect(s);

    /// <summary>PKCE: a random verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>The dashboard's client registration for this redirect URI, registering with JobTread the first time.</summary>
    public async Task<(string ClientId, string? ClientSecret)> GetClientAsync(string redirectUri, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var existing = await db.JobTreadOAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.RedirectUri == redirectUri, ct);
        if (existing is not null)
            return (existing.ClientId, existing.ClientSecretProtected is null ? null : Unprotect(existing.ClientSecretProtected));

        var body = new JsonObject
        {
            ["client_name"] = "Purple Painting Dashboard",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        using var response = await http.PostAsync(RegistrationEndpoint, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new JobTreadException($"JobTread didn't accept the dashboard's sign-in registration ({(int)response.StatusCode}): {Trim(text)}");

        var json = JsonNode.Parse(text)!;
        var clientId = json["client_id"]?.GetValue<string>() ?? throw new JobTreadException("JobTread's registration didn't return a client id.");
        var secret = json["client_secret"]?.GetValue<string>();
        db.JobTreadOAuthClients.Add(new JobTreadOAuthClient
        {
            ClientId = clientId,
            ClientSecretProtected = secret is null ? null : Protect(secret),
            RedirectUri = redirectUri,
            RegisteredAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Registered the dashboard with JobTread OAuth for {RedirectUri}", redirectUri);
        return (clientId, secret);
    }

    public string AuthorizeUrl(string clientId, string redirectUri, string state, string challenge) =>
        $"{AuthorizeEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256"
        + $"&resource={Uri.EscapeDataString(McpResource)}";

    public sealed record Tokens(string AccessToken, string? RefreshToken, DateTime? ExpiresAt);

    public Task<Tokens> ExchangeCodeAsync(string clientId, string? secret, string code, string redirectUri, string verifier, CancellationToken ct) =>
        TokenRequestAsync(clientId, secret, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["resource"] = McpResource,
        }, ct);

    public Task<Tokens> RefreshAsync(string clientId, string? secret, string refreshToken, CancellationToken ct) =>
        TokenRequestAsync(clientId, secret, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["resource"] = McpResource,
        }, ct);

    private async Task<Tokens> TokenRequestAsync(string clientId, string? secret, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = clientId;
        if (secret is not null) form["client_secret"] = secret;
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new JobTreadException(text.Contains("invalid_grant")
                ? "Your JobTread sign-in expired. Sign in with JobTread again on Connections."
                : $"JobTread sign-in failed ({(int)response.StatusCode}): {Trim(text)}");

        var json = JsonNode.Parse(text)!;
        var expiresIn = json["expires_in"]?.GetValue<int?>();
        return new Tokens(
            json["access_token"]?.GetValue<string>() ?? throw new JobTreadException("JobTread didn't return an access token."),
            json["refresh_token"]?.GetValue<string>(),
            expiresIn is { } s ? DateTime.UtcNow.AddSeconds(s - 60) : null);
    }

    /// <summary>
    /// Runs a Pave query through JobTread's MCP server ("query" tool) as the signed-in person.
    /// Speaks MCP's Streamable HTTP: initialize, initialized, then tools/call.
    /// </summary>
    public async Task<JsonNode> McpQueryAsync(string accessToken, string name, JsonObject query, CancellationToken ct)
    {
        string? session = null;
        await McpPostAsync(accessToken, ref_session: s => session = s, session, new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = McpProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "purple-dashboard", ["version"] = "1.0" },
            },
        }, ct);
        await McpPostAsync(accessToken, _ => { }, session, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);

        var started = DateTime.UtcNow;
        var reply = await McpPostAsync(accessToken, _ => { }, session, new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "query", ["arguments"] = new JsonObject { ["query"] = query.DeepClone() } },
        }, ct);
        logger.LogInformation("JobTread MCP {Query} in {Ms} ms", name, (DateTime.UtcNow - started).TotalMilliseconds);

        if (reply?["error"] is { } error) throw new JobTreadException($"JobTread error: {Trim(error["message"]?.ToString() ?? error.ToJsonString())}");
        var result = reply?["result"];
        var textOut = string.Concat((result?["content"] as JsonArray ?? []).Select(c => c?["text"]?.GetValue<string>() ?? ""));
        if (result?["isError"]?.GetValue<bool>() == true) throw new JobTreadException($"JobTread error: {Trim(textOut)}");
        if (result?["structuredContent"] is JsonNode structured) return structured.DeepClone();
        try { return JsonNode.Parse(textOut) ?? throw new JobTreadException("JobTread returned an empty response."); }
        catch (System.Text.Json.JsonException e) { throw new JobTreadException($"JobTread error: {Trim(textOut)}", e); }
    }

    /// <summary>One MCP JSON-RPC message. Returns the reply (JSON or the matching server-sent event), or null for notifications.</summary>
    private async Task<JsonNode?> McpPostAsync(string accessToken, Action<string> ref_session, string? session, JsonObject message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpResource)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", McpProtocolVersion);
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);

        using var response = await http.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) ref_session(ids.First());
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new JobTreadException("Your JobTread sign-in expired. Sign in with JobTread again on Connections.");
        if (!response.IsSuccessStatusCode)
            throw new JobTreadException($"JobTread's connector returned {(int)response.StatusCode}: {Trim(text)}");
        if (message["id"] is null || string.IsNullOrWhiteSpace(text)) return null;

        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            // Server-sent events: the reply is the "data:" line whose JSON has our id.
            var id = message["id"]!.ToJsonString();
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("data:")) continue;
                var node = JsonNode.Parse(line[5..].Trim());
                if (node?["id"]?.ToJsonString() == id) return node;
            }
            throw new JobTreadException("JobTread's connector didn't answer.");
        }
        return JsonNode.Parse(text);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
