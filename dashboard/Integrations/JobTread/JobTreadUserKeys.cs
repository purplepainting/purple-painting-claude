using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PurpleDashboard.Data;

namespace PurpleDashboard.Integrations.JobTread;

/// <summary>
/// Each dashboard user's own JobTread grant key, so changes they push to JobTread are recorded as
/// them. Saved encrypted (ASP.NET Data Protection) per Entra user. A key is checked with JobTread
/// before it's saved.
/// </summary>
public sealed class JobTreadUserKeys(IJobTreadClient jobTread, IServiceProvider services, DatabaseStatus database, IDataProtectionProvider protection, IOptions<JobTreadOptions> options)
{
    private IDataProtector Protector => protection.CreateProtector("JobTread.UserGrantKey");

    public sealed record Linked(string GrantKey, string? JobTreadUserName);

    /// <summary>The user's key and JobTread name, or null if they haven't linked (or it can't be read anymore).</summary>
    public async Task<Linked?> GetAsync(string? userObjectId, CancellationToken ct)
    {
        var row = await FindAsync(userObjectId, ct);
        if (row is null) return null;
        try { return new Linked(Protector.Unprotect(row.GrantKeyProtected), row.JobTreadUserName); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task<JobTreadUserKey?> FindAsync(string? userObjectId, CancellationToken ct)
    {
        if (!database.Available || string.IsNullOrEmpty(userObjectId)) return null;
        var db = services.GetRequiredService<DashboardDb>();
        return await db.JobTreadUserKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserObjectId == userObjectId, ct);
    }

    /// <summary>Checks the key with JobTread (must belong to Purple Painting) and saves it. Returns the JobTread user's name.</summary>
    public async Task<string> SaveAsync(string userObjectId, string? email, string grantKey, CancellationToken ct)
    {
        grantKey = grantKey.Trim();
        JsonNode result;
        try
        {
            result = await jobTread.QueryAsAsync("grant-check", new JsonObject
            {
                ["currentGrant"] = JsonNode.Parse("""{ "user": { "id": {}, "name": {} }, "organization": { "id": {} } }"""),
            }, grantKey, ct);
        }
        catch (JobTreadException e)
        {
            throw new JobTreadException("JobTread didn't accept that key. Check you copied all of it and that it hasn't expired.", e);
        }
        var grant = result["currentGrant"];
        var orgId = grant?["organization"]?["id"]?.GetValue<string>();
        if (orgId is not null && orgId != options.Value.OrganizationId)
            throw new JobTreadException("That key is for a different JobTread organization than Purple Painting.");

        var db = services.GetRequiredService<DashboardDb>();
        var row = await db.JobTreadUserKeys.FirstOrDefaultAsync(k => k.UserObjectId == userObjectId, ct);
        if (row is null) db.JobTreadUserKeys.Add(row = new JobTreadUserKey { UserObjectId = userObjectId, GrantKeyProtected = "" });
        row.GrantKeyProtected = Protector.Protect(grantKey);
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
