using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;

namespace PurpleDashboard.Pages.Settings;

/// <summary>
/// Settings → Users &amp; access (admins only; Auth/AccessMap.cs). Everyone who has signed in, their role,
/// last seen, and per-person overrides on top of the role. Every change goes to the change log (area "access").
/// </summary>
public class AccessModel(UserAccessStore store, Permissions permissions, ChangeLogWriter changeLog, DatabaseStatus database, ILogger<AccessModel> logger) : PageModel
{
    public sealed record Person(UserAccess Row, bool Bootstrap, IReadOnlySet<string> Defaults, IReadOnlySet<string> Effective, IReadOnlyDictionary<string, bool> Overrides);

    public List<Person> People { get; private set; } = [];
    /// <summary>ADMIN_EMAILS entries that haven't signed in yet.</summary>
    public List<string> AdminsNotSeen { get; private set; } = [];
    public bool DatabaseAvailable => database.Available;
    public bool CanEdit => database.Available && permissions.Can(Perm.AccessEdit);
    public string? MyEmail => permissions.Email;
    [TempData] public string? Message { get; set; }
    [BindProperty(SupportsGet = true)] public int? Open { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Users & access";
        ViewData["ModuleName"] = "Users & access";
        if (!database.Available) return;
        try
        {
            var rows = await store.ListAsync(ct);
            People = rows.Select(ToPerson)
                .OrderBy(p => p.Bootstrap ? 0 : 1)
                .ThenByDescending(p => p.Row.LastSeenAt)
                .ToList();
            AdminsNotSeen = store.AdminEmails.Where(e => rows.All(r => r.Email != e)).Order().ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Loading users failed");
            Message = "Couldn't load the list of users. Try again in a minute.";
        }
    }

    /// <summary>
    /// Saves one person's role and overrides. Each permission is a checkbox showing what the person gets;
    /// <paramref name="fromRole"/> is the role those boxes were drawn for, so a box that differs from that
    /// role's default is an override. Overrides are kept when the role changes.
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(int id, string role, string? fromRole, List<string>? perms, CancellationToken ct)
    {
        if (!CanEdit) return RedirectToPage();
        if (!AccessRoles.IsValid(role)) { Message = "Pick a role."; return RedirectToPage(new { open = id }); }

        var list = await store.ListAsync(ct);
        if (list.FirstOrDefault(r => r.Id == id) is not { } current) return NotFound();
        if (store.IsBootstrapAdmin(current.Email))
        {
            Message = $"{current.Email} is in ADMIN_EMAILS, so they're always Admin. Change the ADMIN_EMAILS setting to change that.";
            return RedirectToPage(new { open = id });
        }

        var shown = AccessRoles.Defaults(AccessRoles.IsValid(fromRole) ? fromRole : current.Role);
        var ticked = (perms ?? []).Where(Perm.All.Contains).ToHashSet();
        var overrides = Perm.All
            .Where(k => ticked.Contains(k) != shown.Contains(k))
            .ToDictionary(k => k, ticked.Contains);
        // Don't store overrides that match the new role anyway.
        var newDefaults = AccessRoles.Defaults(role);
        foreach (var k in overrides.Keys.ToList()) if (overrides[k] == newDefaults.Contains(k)) overrides.Remove(k);

        if (current.Email == permissions.Email && !AccessRoles.Effective(role, overrides).Contains(Perm.AccessEdit))
        {
            Message = "You can't take away your own access to this page. Ask another admin to do it.";
            return RedirectToPage(new { open = id });
        }

        var saved = await store.SaveAsync(id, role, overrides, permissions.Email ?? "unknown", ct);
        if (saved is not { } s) return NotFound();

        var beforeOverrides = UserAccessStore.ReadOverrides(s.Before.OverridesJson);
        if (s.Before.Role != role || !SameOverrides(beforeOverrides, overrides))
        {
            await changeLog.WriteAsync([ChangeLogWriter.Entry(permissions.Email, "access", s.Before.Role != role ? "role" : "overrides", current.Name ?? current.Email, new
            {
                email = current.Email,
                before = new { role = s.Before.Role, overrides = beforeOverrides },
                after = new { role, overrides },
            }, "Done")], ct);
            Message = $"Saved {current.Name ?? current.Email}: {AccessRoles.Label(role)}" + (overrides.Count > 0 ? $" with {overrides.Count} change{(overrides.Count == 1 ? "" : "s")} to the role" : "") + ".";
        }
        else Message = "Nothing changed.";
        return RedirectToPage(new { open = id });
    }

    private Person ToPerson(UserAccess row)
    {
        var bootstrap = store.IsBootstrapAdmin(row.Email);
        var role = bootstrap ? AccessRoles.Admin : AccessRoles.IsValid(row.Role) ? row.Role : AccessRoles.ViewOnly;
        var overrides = bootstrap ? new Dictionary<string, bool>() : UserAccessStore.ReadOverrides(row.OverridesJson);
        return new Person(row, bootstrap, AccessRoles.Defaults(role), AccessRoles.Effective(role, overrides), overrides);
    }

    private static bool SameOverrides(IReadOnlyDictionary<string, bool> a, IReadOnlyDictionary<string, bool> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Each role's defaults as JSON for the page script (redraws the boxes when the role changes).</summary>
    public static string RoleDefaultsJson() => System.Text.Json.JsonSerializer.Serialize(
        AccessRoles.All.ToDictionary(r => r, r => AccessRoles.Defaults(r).Order().ToArray()));
}
