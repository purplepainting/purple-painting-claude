using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.Google;

namespace PurpleDashboard.Pages.Connections;

/// <summary>Google returns here after someone approves (or cancels) "Connect Google".</summary>
public class GoogleCallbackModel(GoogleClient google, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<GoogleCallbackModel> logger) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var expected = Request.Cookies[IndexModel.GoogleStateCookie];
        Response.Cookies.Delete(IndexModel.GoogleStateCookie, new CookieOptions { Path = "/connections" });

        string message;
        if (error is not null) message = error == "access_denied" ? "Google wasn't connected (cancelled on Google's page)." : $"Google said: {error}.";
        else if (string.IsNullOrEmpty(expected) || expected != state) message = "That connect link expired. Click Connect Google again.";
        else if (string.IsNullOrEmpty(code)) message = "Google didn't send back an approval. Try again.";
        else if (!database.Available) message = "The dashboard database isn't available, so the connection can't be saved. Try again in a minute.";
        else
        {
            try
            {
                var user = me.Email ?? me.DisplayName ?? "unknown";
                var connection = await google.CompleteConnectAsync(code, IndexModel.GoogleRedirectUri(HttpContext, google), user, ct);
                var missing = GoogleClient.MissingScopes(connection);
                await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "connections", "connect", "Google", new { email = connection.Email, scopes = connection.Scopes }, "Done")], ct);
                message = $"Google connected: {connection.Email}."
                    + (missing.Count > 0 ? " Some access wasn't allowed on Google's page (see below); reconnect and tick every box to fix it." : " Now pick the Ads account and the Business Profile location below.");
            }
            catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException)
            {
                logger.LogError(e, "Connecting Google failed");
                message = e is GoogleException ? e.Message : "Couldn't reach Google. Try again.";
            }
        }

        TempData["Message"] = message;
        return RedirectToPage("/Connections/Index");
    }
}
