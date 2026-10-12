using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Pages.Connections;

/// <summary>
/// Which outside systems the dashboard is connected to, and connecting QuickBooks.
/// Connect: redirect to Intuit with a random state (also kept in a cookie); Intuit returns to
/// /quickbooks/callback (Callback page), which checks the state and saves the connection.
/// </summary>
public class IndexModel(QuickBooksClient quickBooks, IJobTreadClient jobTread, JobTreadUserKeys userKeys, JobTreadOAuth jobTreadOAuth, IDataProtectionProvider protection, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    public const string StateCookie = "qbo_state";
    public const string JobTreadCookie = "jt_signin";

    public bool JobTreadConfigured => jobTread.IsConfigured;
    public bool QuickBooksConfigured => quickBooks.IsConfigured;
    public DatabaseStatus Database => database;
    public QuickBooksConnection? Connection { get; private set; }
    public JobTreadUserKey? MyJobTread { get; private set; }
    public string? MyName => me.DisplayName;
    public string? CheckResult { get; private set; }
    public bool CheckOk { get; private set; }
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(bool check, CancellationToken ct)
    {
        ViewData["Title"] = "Connections";
        ViewData["ModuleName"] = "Connections";
        Connection = await quickBooks.GetConnectionAsync(ct);
        MyJobTread = await userKeys.FindAsync(me.ObjectId, ct);

        if (check && Connection is not null)
        {
            try
            {
                var accounts = await quickBooks.QueryAsync("Account", "select Id from Account where Active = true", ct);
                CheckOk = true;
                CheckResult = $"Connected and reading: {accounts.Count} active accounts in QuickBooks.";
            }
            catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "QuickBooks check failed");
                CheckResult = e is QuickBooksException ? e.Message : "Couldn't reach QuickBooks. Try again in a minute.";
            }
        }
    }

    public IActionResult OnGetConnect()
    {
        if (!quickBooks.IsConfigured || !database.Available) return RedirectToPage();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15), Path = "/quickbooks",
        });
        return Redirect(quickBooks.AuthorizeUri(RedirectUri(HttpContext, quickBooks), state));
    }

    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken ct)
    {
        await quickBooks.DisconnectAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(me.Email ?? me.DisplayName, "connections", "disconnect", "QuickBooks", null, "Done")], ct);
        Message = "QuickBooks disconnected.";
        return RedirectToPage();
    }

    /// <summary>
    /// "Sign in with JobTread": send the person to JobTread's sign-in. The state and PKCE verifier are
    /// kept in a short-lived encrypted cookie and checked on the way back (/jobtread/callback).
    /// </summary>
    public async Task<IActionResult> OnGetJobTreadSignInAsync(CancellationToken ct)
    {
        if (!database.Available || me.ObjectId is null) { Message = "Can't sign in right now: the dashboard database isn't available."; return RedirectToPage(); }
        var redirectUri = JobTreadRedirectUri(HttpContext);
        try
        {
            var (clientId, _) = await jobTreadOAuth.GetClientAsync(redirectUri, ct);
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var (verifier, challenge) = JobTreadOAuth.NewPkce();
            Response.Cookies.Append(JobTreadCookie, protection.CreateProtector(JobTreadCookie).Protect($"{state}|{verifier}"), new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15), Path = "/jobtread",
            });
            return Redirect(jobTreadOAuth.AuthorizeUrl(clientId, redirectUri, state, challenge));
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Starting JobTread sign-in failed");
            Message = e is JobTreadException ? e.Message : "Couldn't reach JobTread. Try again in a minute.";
            return RedirectToPage();
        }
    }

    /// <summary>Where JobTread sends people back after signing in.</summary>
    public static string JobTreadRedirectUri(HttpContext http) => $"https://{http.Request.Host}/jobtread/callback";

    /// <summary>Link the signed-in person's own JobTread grant key (checked with JobTread first).</summary>
    public async Task<IActionResult> OnPostLinkJobTreadAsync(string? grantKey, CancellationToken ct)
    {
        if (!database.Available || me.ObjectId is null) { Message = "Can't save right now: the dashboard database isn't available."; return RedirectToPage(); }
        if (string.IsNullOrWhiteSpace(grantKey) || grantKey.Length > 200) { Message = "Paste your JobTread key first."; return RedirectToPage(); }
        try
        {
            var name = await userKeys.SaveAsync(me.ObjectId, me.Email, grantKey, ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(me.Email ?? me.DisplayName, "connections", "link-jobtread", name, null, "Done")], ct);
            Message = $"JobTread linked: your schedule changes will show as {name} in JobTread.";
        }
        catch (JobTreadException e) { Message = e.Message; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Message = "Couldn't reach JobTread. Try again in a minute."; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnlinkJobTreadAsync(CancellationToken ct)
    {
        if (me.ObjectId is not null && database.Available) await userKeys.RemoveAsync(me.ObjectId, ct);
        Message = "Your JobTread account was unlinked from the dashboard.";
        return RedirectToPage();
    }

    /// <summary>Where Intuit sends people back. Must be listed on the Intuit app exactly.</summary>
    public static string RedirectUri(HttpContext http, QuickBooksClient quickBooks) =>
        quickBooks.RedirectUriSetting ?? $"https://{http.Request.Host}/quickbooks/callback";
}
