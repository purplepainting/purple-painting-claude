using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.Meta;

namespace PurpleDashboard.Pages.Connections;

/// <summary>
/// Facebook/Instagram ads connection actions. Connect: redirect to Facebook Login with a random state (also kept in a
/// cookie); Facebook returns to /connections/meta/callback (MetaCallback page), which checks it and saves the token.
/// The card is the MetaConnection view component on /connections.
/// </summary>
public class MetaModel(MetaClient meta, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<MetaModel> logger) : PageModel
{
    public const string StateCookie = "meta_state";
    public const string CookiePath = "/connections/meta";
    public const string Target = "Facebook/Instagram ads";

    [TempData] public string? Message { get; set; }

    public IActionResult OnGet() => RedirectToPage("/Connections/Index");

    public IActionResult OnGetConnect()
    {
        if (!meta.IsConfigured || meta.UsingFixtures || !database.Available)
        {
            Message = meta.UsingFixtures ? "Using sample Meta data (Meta:FixtureDirectory), so there's nothing to connect." : null;
            return RedirectToPage("/Connections/Index");
        }
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15), Path = CookiePath,
        });
        return Redirect(meta.AuthorizeUri(RedirectUri(HttpContext, meta), state));
    }

    public async Task<IActionResult> OnPostAccountAsync(string? adAccountId, CancellationToken ct)
    {
        if (!database.Available || string.IsNullOrWhiteSpace(adAccountId)) return RedirectToPage("/Connections/Index");
        try
        {
            var account = (await meta.AdAccountsAsync(ct)).FirstOrDefault(a => a.Id == adAccountId);
            if (account is null) Message = "That ad account isn't available to the connected Facebook user.";
            else
            {
                await meta.SelectAdAccountAsync(account, ct);
                await changeLog.WriteAsync([ChangeLogWriter.Entry(UserName(), "connections", "meta-ad-account", account.Name, new { adAccountId = account.Id }, "Done")], ct);
                Message = $"Facebook/Instagram ads will be read from {account.Name}.";
            }
        }
        catch (Exception e) when (e is MetaException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Picking the Meta ad account failed");
            Message = e is MetaException ? e.Message : "Couldn't reach Meta. Try again in a minute.";
        }
        return RedirectToPage("/Connections/Index");
    }

    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken ct)
    {
        if (database.Available)
        {
            await meta.DisconnectAsync(ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(UserName(), "connections", "disconnect", Target, null, "Done")], ct);
            Message = "Facebook/Instagram ads disconnected.";
        }
        return RedirectToPage("/Connections/Index");
    }

    private string UserName() => me.Email ?? me.DisplayName ?? "unknown";

    /// <summary>Where Facebook sends people back. Must be a Valid OAuth Redirect URI on the Meta app, exactly.</summary>
    public static string RedirectUri(HttpContext http, MetaClient meta) =>
        meta.RedirectUriSetting ?? $"https://{http.Request.Host}/connections/meta/callback";
}
