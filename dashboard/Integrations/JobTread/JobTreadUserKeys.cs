using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PurpleDashboard.Data;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// Each dashboard user's own JobTread access, so changes they push to JobTread are recorded as them.
/// Either "Sign in with JobTread" (OAuth, see JobTreadOAuth) or a pasted grant key. Saved encrypted
/// (ASP.NET Data Protection) per Entra user, and checked with JobTread before it's saved.
/// </summary>
public sealed class JobTreadUserKeys(
    IJobTreadClient jobTread,
    JobTreadOAuth oauth,
    IServiceProvider services,
    DatabaseStatus database,
    IDataProtectionProvider protection,
    IOptions<JobTreadOptions> options,
    ILogger<JobTreadUserKeys> logger)
{
    private IDataProtector Protector => protection.CreateProtector("JobTread.UserGrantKey");

    private static readonly JsonObject GrantCheck = new()
    {
        ["currentGrant"] = JsonNode.Parse("""{ "user": { "id": {}, "name": {} }, "organization": { "id": {} } }"""),
    };

    /// <summary>Runs a Pave query as one person.</summary>
    public delegate Task<JsonNode> Runner(string name, JsonObject query, CancellationToken ct);

    public async Task<JobTreadUserKey?> FindAsync(string? userObjectId, CancellationToken ct)
    {
        if (!database.Available || string.IsNullOrEmpty(userObjectId)) return null;
        var db = services.GetRequiredService<DashboardDb>();
        return await db.JobTreadUserKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserObjectId == userObjectId, ct);
    }

    /// <summary>
    /// Something that runs queries as this person (refreshing their sign-in if it expired), or null if
    /// they haven't linked JobTread.
    /// </summary>
    public async Task<Runner?> GetRunnerAsync(string? userObjectId, CancellationToken ct)
    {
        if (!database.Available || string.IsNullOrEmpty(userObjectId)) return null;
        var db = services.GetRequiredService<DashboardDb>();
        var row = await db.JobTreadUserKeys.FirstOrDefaultAsync(k => k.UserObjectId == userObjectId, ct);
        if (row is null) return null;

        string secret;
        try { secret = Protector.Unprotect(row.GrantKeyProtected); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }

        if (row.AuthKind is "oauth-pave" or "oauth-mcp" && row.AccessExpiresAt is { } expires && expires < DateTime.UtcNow)
        {
            if (row.RefreshTokenProtected is null) throw new JobTreadException("Your JobTread sign-in expired. Sign in with JobTread again on Connections.");
            var client = await db.JobTreadOAuthClients.AsNoTracking().OrderByDescending(c => c.RegisteredAt).FirstOrDefaultAsync(ct)
                ?? throw new JobTreadException("Sign in with JobTread again on Connections.");
            var tokens = await oauth.RefreshAsync(client.ClientId, client.ClientSecretProtected is null ? null : oauth.Unprotect(client.ClientSecretProtected),
                Protector.Unprotect(row.RefreshTokenProtected), ct);
            secret = tokens.AccessToken;
            row.GrantKeyProtected = Protector.Protect(tokens.AccessToken);
            if (tokens.RefreshToken is not null) row.RefreshTokenProtected = Protector.Protect(tokens.RefreshToken);
            row.AccessExpiresAt = tokens.ExpiresAt;
            await db.SaveChangesAsync(ct);
        }

        return row.AuthKind == "oauth-mcp"
            ? (name, query, c) => oauth.McpQueryAsync(secret, name, query, c)
            : (name, query, c) => jobTread.QueryAsAsync(name, query, secret, c);
    }

    /// <summary>Checks a pasted grant key with JobTread (must belong to Purple Painting) and saves it. Returns the JobTread user's name.</summary>
    public async Task<string> SaveAsync(string userObjectId, string? email, string grantKey, CancellationToken ct)
    {
        grantKey = grantKey.Trim();
        JsonNode result;
        try { result = await jobTread.QueryAsAsync("grant-check", GrantCheck, grantKey, ct); }
        catch (JobTreadException e)
        {
            throw new JobTreadException("JobTread didn't accept that key. Check you copied all of it and that it hasn't expired.", e);
        }
        return await StoreAsync(userObjectId, email, result, "key", grantKey, null, null, ct);
    }

    /// <summary>
    /// Saves a "Sign in with JobTread" result. Tries the token straight against Pave first; if JobTread
    /// doesn't take it there, uses JobTread's MCP server. Returns the JobTread user's name.
    /// </summary>
    public async Task<string> SaveOAuthAsync(string userObjectId, string? email, JobTreadOAuth.Tokens tokens, CancellationToken ct)
    {
        JsonNode result;
        string kind;
        try
        {
            result = await jobTread.QueryAsAsync("grant-check", GrantCheck, tokens.AccessToken, ct);
            kind = "oauth-pave";
        }
        catch (JobTreadException pave)
        {
            logger.LogInformation("JobTread sign-in token isn't a Pave grant key ({Message}); using the MCP server", pave.Message);
            result = await oauth.McpQueryAsync(tokens.AccessToken, "grant-check", GrantCheck, ct);
            kind = "oauth-mcp";
        }
        return await StoreAsync(userObjectId, email, result, kind, tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt, ct);
    }

    private async Task<string> StoreAsync(string userObjectId, string? email, JsonNode result, string kind, string secret, string? refresh, DateTime? expires, CancellationToken ct)
    {
        var grant = result["currentGrant"];
        var orgId = grant?["organization"]?["id"]?.GetValue<string>();
        if (orgId is not null && orgId != options.Value.OrganizationId)
            throw new JobTreadException("That JobTread account is for a different organization than Purple Painting.");

        var db = services.GetRequiredService<DashboardDb>();
        var row = await db.JobTreadUserKeys.FirstOrDefaultAsync(k => k.UserObjectId == userObjectId, ct);
        if (row is null) db.JobTreadUserKeys.Add(row = new JobTreadUserKey { UserObjectId = userObjectId, GrantKeyProtected = "" });
        row.GrantKeyProtected = Protector.Protect(secret);
        row.RefreshTokenProtected = refresh is null ? null : Protector.Protect(refresh);
        row.AccessExpiresAt = expires;
        row.AuthKind = kind;
        row.UserEmail = email;
        row.JobTreadUserId = grant?["user"]?["id"]?.GetValue<string>();
        row.JobTreadUserName = grant?["user"]?["name"]?.GetValue<string>();
        row.SavedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return row.JobTreadUserName ?? "your JobTread user";
    }

    public async Task RemoveAsync(string userObjectId, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        await db.JobTreadUserKeys.Where(k => k.UserObjectId == userObjectId).ExecuteDeleteAsync(ct);
    }
}
