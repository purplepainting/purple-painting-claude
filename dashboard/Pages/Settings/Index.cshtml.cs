using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;

namespace PurpleDashboard.Pages.Settings;

/// <summary>Settings: the places that used to be small links under the signed-in name (connections, change log, sign out).</summary>
public class IndexModel(CurrentUser me) : PageModel
{
    public string? Name => me.DisplayName;
    public string? Email => me.Email;

    public void OnGet()
    {
        ViewData["Title"] = "Settings";
        ViewData["ModuleName"] = "Settings";
    }
}
