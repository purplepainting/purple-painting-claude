namespace PurpleDashboard.Auth;

/// <summary>
/// What the signed-in person may do on this request. Inject it anywhere (scoped) and call
/// <see cref="Can"/> with a key from <see cref="Perm"/>. Loaded once per request by the access middleware
/// (Program.cs) before any page runs.
///
/// Rules: bootstrap admins (ADMIN_EMAILS) are always Admin. Everyone else gets their role from the
/// UserAccess table plus per-person overrides; new people are View only. When the database isn't
/// available, everyone who isn't a bootstrap admin is View only (<see cref="Fallback"/> is true).
/// </summary>
public sealed class Permissions(CurrentUser me, UserAccessStore store, ILogger<Permissions> logger)
{
    private IReadOnlySet<string> granted = new HashSet<string>();
    private bool loaded;

    public string? Email => me.Email?.Trim().ToLowerInvariant();
    public string Role { get; private set; } = AccessRoles.ViewOnly;
    public bool IsAdmin => Role == AccessRoles.Admin;
    /// <summary>In ADMIN_EMAILS: always Admin, can't be changed or locked out on the access page.</summary>
    public bool IsBootstrapAdmin { get; private set; }
    /// <summary>The database couldn't be read: roles fell back to ADMIN_EMAILS = Admin, everyone else View only.</summary>
    public bool Fallback { get; private set; }
    public IReadOnlyDictionary<string, bool> Overrides { get; private set; } = new Dictionary<string, bool>();
    public IReadOnlySet<string> Granted => granted;

    public async Task LoadAsync(CancellationToken ct)
    {
        if (loaded) return;
        loaded = true;
        var email = Email;
        if (email is null)
        {
            // Not signed in (never happens behind Easy Auth): nothing but public pages.
            Role = "none";
            granted = new HashSet<string>();
            return;
        }

        IsBootstrapAdmin = store.IsBootstrapAdmin(email);
        var row = await store.GetAndTouchAsync(email, me.DisplayName, ct);
        Fallback = row is null;
        if (IsBootstrapAdmin)
        {
            Role = AccessRoles.Admin;
            granted = AccessRoles.Effective(Role, null);
            return;
        }
        if (row is null)
        {
            Role = AccessRoles.ViewOnly;
            granted = AccessRoles.Effective(Role, null);
            return;
        }
        Role = AccessRoles.IsValid(row.Role) ? row.Role : AccessRoles.ViewOnly;
        Overrides = UserAccessStore.ReadOverrides(row.OverridesJson);
        granted = AccessRoles.Effective(Role, Overrides);
    }

    /// <summary>True when the person has this permission key (see <see cref="Perm"/>).</summary>
    public bool Can(string permission)
    {
        if (!loaded) logger.LogWarning("Permissions checked before they were loaded ({Permission}); treated as denied", permission);
        if (!Perm.All.Contains(permission)) logger.LogWarning("Unknown permission key {Permission}", permission);
        return granted.Contains(permission);
    }

    public bool CanAny(params string[] permissions) => permissions.Any(Can);
}
