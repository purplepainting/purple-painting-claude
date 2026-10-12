using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.JobTread;

namespace PurpleDashboard.Pages.Connections;

/// <summary>JobTread returns here after someone signs in (or cancels) for "Sign in with JobTread".</summary>
public class JobTreadCallbackModel(JobTreadOAuth oauth, JobTreadUserKeys userKeys, IDataProtectionProvider protection, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<JobTreadCallbackModel> logger) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        string? expectedState = null, verifier = null;
        try
        {
            var cookie = Request.Cookies[IndexModel.JobTreadCookie];
            if (cookie is not null && protection.CreateProtector(IndexModel.JobTreadCookie).Unprotect(cookie).Split('|') is [var s, var v]) (expectedState, verifier) = (s, v);
        }
        catch (System.Security.Cryptography.CryptographicException) { }
        Response.Cookies.Delete(IndexModel.JobTreadCookie, new CookieOptions { Path = "/jobtread" });

        string message;
        if (error is not null) message = error == "access_denied" ? "JobTread sign-in was cancelled." : $"JobTread said: {error}.";
        else if (expectedState is null || expectedState != state || verifier is null) message = "That sign-in link expired. Click Sign in with JobTread again.";
        else if (string.IsNullOrEmpty(code)) message = "JobTread didn't send back a sign-in. Try again.";
        else if (!database.Available || me.ObjectId is null) message = "The dashboard database isn't available, so the sign-in can't be saved. Try again in a minute.";
        else
        {
            try
            {
                var redirectUri = IndexModel.JobTreadRedirectUri(HttpContext);
                var (clientId, secret) = await oauth.GetClientAsync(redirectUri, ct);
                var tokens = await oauth.ExchangeCodeAsync(clientId, secret, code, redirectUri, verifier, ct);
                var name = await userKeys.SaveOAuthAsync(me.ObjectId, me.Email, tokens, ct);
                await changeLog.WriteAsync([ChangeLogWriter.Entry(me.Email ?? me.DisplayName, "connections", "jobtread-signin", name, null, "Done")], ct);
                message = $"Signed in with JobTread: your schedule changes will show as {name} in JobTread.";
            }
            catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
            {
                logger.LogError(e, "Finishing JobTread sign-in failed");
                message = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again.";
            }
        }

        TempData["Message"] = message;
        return RedirectToPage("/Connections/Index");
    }
}
