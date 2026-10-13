using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;

namespace PurpleDashboard.Pages.Settings;

/// <summary>Settings: connections, change log, users &amp; access, sign out. Tiles show by permission.</summary>
public class IndexModel(CurrentUser me, Permissions permissions, UserAccessStore access) : PageModel
{
    public string? Name => me.DisplayName;
    public string? Email => me.Email;
    public Permissions Access => permissions;
    /// <summary>The database is set up but roles couldn't be read: everyone but ADMIN_EMAILS is View only.</summary>
    public bool Fallback => permissions.Fallback;
    public bool AccessDatabaseAvailable => access.Available;

    public void OnGet()
    {
        ViewData["Title"] = "Settings";
        ViewData["ModuleName"] = "Settings";
    }
}
