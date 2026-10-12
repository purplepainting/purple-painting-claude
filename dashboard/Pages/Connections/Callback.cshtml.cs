using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Pages.Connections;

/// <summary>Intuit returns here after someone approves (or cancels) connecting QuickBooks.</summary>
public class CallbackModel(QuickBooksClient quickBooks, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<CallbackModel> logger) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? realmId, string? error, CancellationToken ct)
    {
        var expected = Request.Cookies[IndexModel.StateCookie];
        Response.Cookies.Delete(IndexModel.StateCookie, new CookieOptions { Path = "/quickbooks" });

        string message;
        if (error is not null) message = error == "access_denied" ? "QuickBooks wasn't connected (cancelled at Intuit)." : $"Intuit said: {error}.";
        else if (string.IsNullOrEmpty(expected) || expected != state) message = "That connect link expired. Click Connect QuickBooks again.";
        else if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(realmId)) message = "Intuit didn't send back a company. Try again.";
        else if (!database.Available) message = "The dashboard database isn't available, so the connection can't be saved. Try again in a minute.";
        else
        {
            try
            {
                var user = me.Email ?? me.DisplayName ?? "unknown";
                var connection = await quickBooks.CompleteConnectAsync(code, realmId, IndexModel.RedirectUri(HttpContext, quickBooks), user, ct);
                await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "connections", "connect", "QuickBooks", new { realmId, connection.CompanyName }, "Done")], ct);
                message = $"QuickBooks connected{(connection.CompanyName is { } n ? $": {n}" : "")}.";
            }
            catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException)
            {
                logger.LogError(e, "Connecting QuickBooks failed");
                message = e is QuickBooksException ? e.Message : "Couldn't reach Intuit. Try again.";
            }
        }

        TempData["Message"] = message;
        return RedirectToPage("/Connections/Index");
    }
}
