using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PurpleDashboard.Data;

namespace PurpleDashboard.Auth;

/// <summary>
/// Reads and writes the UserAccess table. A person's row is cached for 30 seconds (cleared on save), and
/// LastSeenAt is written at most every 5 minutes, so access checks cost a database read twice a minute per
/// person at most. Bootstrap admins come from the ADMIN_EMAILS setting (comma separated).
/// </summary>
public sealed class UserAccessStore(IServiceProvider services, DatabaseStatus database, IMemoryCache cache, IConfiguration config, ILogger<UserAccessStore> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(5);

    /// <summary>Emails that are always Admin (ADMIN_EMAILS). Lowercase.</summary>
    public IReadOnlySet<string> AdminEmails => ParseEmails(config["ADMIN_EMAILS"]);

    public bool IsBootstrapAdmin(string? email) => email is not null && AdminEmails.Contains(email.Trim().ToLowerInvariant());

    /// <summary>Whether roles can be read and saved right now.</summary>
    public bool Available => database.Available;

    public static IReadOnlySet<string> ParseEmails(string? value) =>
        (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant()).ToHashSet();

    /// <summary>
    /// The person's row, adding it (View only) on their first request and updating LastSeenAt now and then.
    /// Null when the database isn't available or the read fails (callers fall back; see Permissions).
    /// </summary>
    public async Task<UserAccess?> GetAndTouchAsync(string email, string? name, CancellationToken ct)
    {
        if (!database.Available) return null;
        email = email.Trim().ToLowerInvariant();
        var key = CacheKey(email);
        if (cache.TryGetValue(key, out UserAccess? cached) && cached is not null && DateTime.UtcNow - cached.LastSeenAt < SeenEvery)
            return cached;
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DashboardDb>();
            var row = await db.UserAccess.FirstOrDefaultAsync(u => u.Email == email, ct);
            var now = DateTime.UtcNow;
            if (row is null)
            {
                row = new UserAccess { Email = email, Name = Trim(name), Role = AccessRoles.Default, FirstSeenAt = now, LastSeenAt = now };
                db.UserAccess.Add(row);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    // Two first requests at once: the other one added the row.
                    db.ChangeTracker.Clear();
                    row = await db.UserAccess.AsNoTracking().FirstAsync(u => u.Email == email, ct);
                }
                logger.LogInformation("First sign-in recorded for {Email}", email);
            }
            else if (now - row.LastSeenAt >= SeenEvery || (row.Name is null && name is not null))
            {
                row.LastSeenAt = now;
                row.Name ??= Trim(name);
                await db.SaveChangesAsync(ct);
            }
            cache.Set(key, row, CacheFor);
            return row;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Reading access for {Email} failed; using the fallback (admins from ADMIN_EMAILS, everyone else view only)", email);
            return null;
        }
    }

    public async Task<List<UserAccess>> ListAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDb>();
        return await db.UserAccess.AsNoTracking().OrderByDescending(u => u.LastSeenAt).ToListAsync(ct);
    }

    /// <summary>Saves role and overrides; returns the row as it was before (null if not found).</summary>
    public async Task<(UserAccess Before, UserAccess After)?> SaveAsync(int id, string role, IReadOnlyDictionary<string, bool> overrides, string by, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDb>();
        var row = await db.UserAccess.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (row is null) return null;
        var before = new UserAccess
        {
            Id = row.Id, Email = row.Email, Name = row.Name, Role = row.Role, OverridesJson = row.OverridesJson,
            FirstSeenAt = row.FirstSeenAt, LastSeenAt = row.LastSeenAt, UpdatedAt = row.UpdatedAt, UpdatedBy = row.UpdatedBy,
        };
        row.Role = role;
        row.OverridesJson = WriteOverrides(overrides);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = Trim(by);
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey(row.Email));
        return (before, row);
    }

    public static IReadOnlyDictionary<string, bool> ReadOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, bool>();
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? [])
                .Where(kv => Perm.All.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (JsonException) { return new Dictionary<string, bool>(); }
    }

    public static string? WriteOverrides(IReadOnlyDictionary<string, bool> overrides) =>
        overrides.Count == 0 ? null : JsonSerializer.Serialize(overrides.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value));

    private static string CacheKey(string email) => "access:" + email;
    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= 200 ? s.Trim() : s.Trim()[..200];
}
