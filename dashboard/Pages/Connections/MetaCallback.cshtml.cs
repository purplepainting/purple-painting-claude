using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.Meta;

namespace PurpleDashboard.Pages.Connections;

/// <summary>Facebook returns here after someone approves (or cancels) connecting Facebook/Instagram ads.</summary>
public class MetaCallbackModel(MetaClient meta, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<MetaCallbackModel> logger) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, string? error_reason, CancellationToken ct)
    {
        var expected = Request.Cookies[MetaModel.StateCookie];
        Response.Cookies.Delete(MetaModel.StateCookie, new CookieOptions { Path = MetaModel.CookiePath });

        string message;
        if (error is not null) message = error_reason == "user_denied" || error == "access_denied" ? "Facebook/Instagram ads weren't connected (cancelled at Facebook)." : $"Facebook said: {error}.";
        else if (string.IsNullOrEmpty(expected) || expected != state) message = "That connect link expired. Click Connect Facebook again.";
        else if (string.IsNullOrEmpty(code)) message = "Facebook didn't send back an approval. Try again.";
        else if (!meta.IsConfigured) message = "The META_APP_ID and META_APP_SECRET settings are missing.";
        else if (!database.Available) message = "The dashboard database isn't available, so the connection can't be saved. Try again in a minute.";
        else
        {
            try
            {
                var user = me.Email ?? me.DisplayName ?? "unknown";
                var c = await meta.CompleteConnectAsync(code, MetaModel.RedirectUri(HttpContext, meta), user, ct);
                await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "connections", "connect", MetaModel.Target, new { facebookUser = c.UserName, adAccount = c.AdAccountName }, "Done")], ct);
                message = $"Facebook/Instagram ads connected as {c.UserName ?? "your Facebook user"}."
                    + (c.AdAccountName is { } a ? $" Reading ad account {a}." : " Now pick the ad account below.");
            }
            catch (Exception e) when (e is MetaException or HttpRequestException or TaskCanceledException)
            {
                logger.LogError(e, "Connecting Meta failed");
                message = e is MetaException ? e.Message : "Couldn't reach Facebook. Try again.";
            }
        }

        TempData["Message"] = message;
        return RedirectToPage("/Connections/Index");
    }
}
