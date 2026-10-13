using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.Google;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Pages.Connections;

/// <summary>
/// Which outside systems the dashboard is connected to, and connecting QuickBooks.
/// Connect: redirect to Intuit with a random state (also kept in a cookie); Intuit returns to
/// /quickbooks/callback (Callback page), which checks the state and saves the connection.
/// </summary>
public class IndexModel(QuickBooksClient quickBooks, GoogleClient google,IJobTreadClient jobTread, JobTreadUserKeys userKeys, JobTreadOAuth jobTreadOAuth, IDataProtectionProvider protection, DatabaseStatus database, ChangeLogWriter changeLog, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    public const string StateCookie = "qbo_state";
    public const string JobTreadCookie = "jt_signin";
    public const string GoogleStateCookie = "google_state";

    public bool JobTreadConfigured => jobTread.IsConfigured;
    public bool QuickBooksConfigured => quickBooks.IsConfigured;
    public DatabaseStatus Database => database;
    public QuickBooksConnection? Connection { get; private set; }
    public JobTreadUserKey? MyJobTread { get; private set; }
    public string? MyName => me.DisplayName;
    public string? MyEmail => me.Email;
    public string? CheckResult { get; private set; }
    public bool CheckOk { get; private set; }
    [TempData] public string? Message { get; set; }

    // Google
    public bool GoogleConfigured => google.IsConfigured;
    public bool GoogleHasDeveloperToken => google.HasDeveloperToken;
    public bool GoogleUsesFixtures => google.UsesFixtures;
    public string? GoogleLoginCustomerIdSetting => google.LoginCustomerIdSetting;
    public GoogleConnection? Google { get; private set; }
    public List<string> GoogleMissingScopes { get; private set; } = [];
    public List<AdsAccount> AdsAccounts { get; private set; } = [];
    public List<string> AdsNotes { get; private set; } = [];
    public string? AdsProblem { get; private set; }
    public List<BusinessLocation> Locations { get; private set; } = [];
    public string? LocationsProblem { get; private set; }

    public async Task OnGetAsync(bool check, bool refreshGoogle, CancellationToken ct)
    {
        ViewData["Title"] = "Connections";
        ViewData["ModuleName"] = "Connections";
        Connection = await quickBooks.GetConnectionAsync(ct);
        MyJobTread = await userKeys.FindAsync(me.ObjectId, ct);
        await LoadGoogleAsync(refreshGoogle, ct);

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

    // ---- Google (one shared connection for Google Ads + Business Profile; read only) ----

    private async Task LoadGoogleAsync(bool refresh, CancellationToken ct)
    {
        if (!google.IsConfigured) return;
        if (refresh) GoogleClient.ClearAll();
        try { Google = await google.GetConnectionAsync(ct); }
        catch (GoogleException e) { LocationsProblem = AdsProblem = e.Message; return; }
        if (Google is null) return;
        GoogleMissingScopes = GoogleClient.MissingScopes(Google);

        if (GoogleMissingScopes.Contains(GoogleOptions.AdsScope)) AdsProblem = "Google Ads wasn't allowed on Google's page. Reconnect and tick it.";
        else if (!google.HasDeveloperToken) AdsProblem = "Add the GOOGLE_ADS_DEVELOPER_TOKEN setting in Azure (docs/GOOGLE.md step 5), then come back.";
        else
        {
            try
            {
                (AdsAccounts, AdsNotes) = await google.ListAdsAccountsAsync(ct);
                if (AdsAccounts.Count(a => !a.IsManager) == 0) AdsProblem = "Google says this login can't read any Google Ads account." + (AdsNotes.Count > 0 ? " " + string.Join(" ", AdsNotes) : "");
            }
            catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Listing Google Ads accounts failed");
                AdsProblem = e is GoogleException ? e.Message : "Couldn't reach Google Ads. Try again in a minute.";
            }
        }

        if (GoogleMissingScopes.Contains(GoogleOptions.BusinessScope)) LocationsProblem = "Business Profile wasn't allowed on Google's page. Reconnect and tick it.";
        else
        {
            try
            {
                Locations = await google.ListLocationsAsync(ct);
                if (Locations.Count == 0) LocationsProblem = "Google says this login doesn't manage any Business Profile location.";
            }
            catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Listing Business Profile locations failed");
                LocationsProblem = e is GoogleException ? e.Message : "Couldn't reach Google. Try again in a minute.";
            }
        }
    }

    /// <summary>"Connect Google": send the person to Google's consent page (with fixtures, connect straight away as the sample account).</summary>
    public async Task<IActionResult> OnGetGoogleConnectAsync(CancellationToken ct)
    {
        if (!google.IsConfigured) return RedirectToPage();
        if (!database.Available) { Message = "The dashboard database isn't available, so a Google connection can't be saved right now."; return RedirectToPage(); }
        if (google.UsesFixtures)
        {
            var user = me.Email ?? me.DisplayName ?? "unknown";
            var c = await google.ConnectWithFixturesAsync(user, ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "connections", "connect", "Google", new { email = c.Email, fixtures = true }, "Done")], ct);
            Message = $"Google connected (sample data): {c.Email}.";
            return RedirectToPage();
        }
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(GoogleStateCookie, state, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(15), Path = "/connections",
        });
        return Redirect(google.AuthorizeUri(GoogleRedirectUri(HttpContext, google), state));
    }

    public async Task<IActionResult> OnPostGoogleDisconnectAsync(CancellationToken ct)
    {
        if (!database.Available) return RedirectToPage();
        await google.DisconnectAsync(ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(me.Email ?? me.DisplayName, "connections", "disconnect", "Google", null, "Done")], ct);
        Message = "Google disconnected.";
        return RedirectToPage();
    }

    /// <summary>Pick the Google Ads account (value = "customerId|loginCustomerId" from the list Google returned).</summary>
    public async Task<IActionResult> OnPostGoogleAdsAsync(string? account, CancellationToken ct)
    {
        if (!database.Available) return RedirectToPage();
        try
        {
            var (accounts, _) = await google.ListAdsAccountsAsync(ct);
            var pick = accounts.FirstOrDefault(a => a.Value == account && !a.IsManager);
            if (pick is null) { Message = "Pick one of the Google Ads accounts in the list."; return RedirectToPage(); }
            await google.SelectAdsAccountAsync(pick, ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(me.Email ?? me.DisplayName, "connections", "google-ads-account", pick.Name, new { customerId = pick.DisplayId, pick.LoginCustomerId }, "Done")], ct);
            Message = $"Google Ads account set: {pick.Name} ({pick.DisplayId}).";
        }
        catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException) { Message = e is GoogleException ? e.Message : "Couldn't reach Google. Try again in a minute."; }
        return RedirectToPage();
    }

    /// <summary>Pick the Business Profile location reviews are read from (value = "accounts/..|locations/..").</summary>
    public async Task<IActionResult> OnPostGoogleLocationAsync(string? location, CancellationToken ct)
    {
        if (!database.Available) return RedirectToPage();
        try
        {
            var pick = (await google.ListLocationsAsync(ct)).FirstOrDefault(l => l.Value == location);
            if (pick is null) { Message = "Pick one of the locations in the list."; return RedirectToPage(); }
            await google.SelectLocationAsync(pick, ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(me.Email ?? me.DisplayName, "connections", "google-location", pick.Title, new { location = pick.Location, pick.Address }, "Done")], ct);
            Message = $"Business Profile location set: {pick.Title}.";
        }
        catch (Exception e) when (e is GoogleException or HttpRequestException or TaskCanceledException) { Message = e is GoogleException ? e.Message : "Couldn't reach Google. Try again in a minute."; }
        return RedirectToPage();
    }

    /// <summary>Where Google sends people back. Must be an authorized redirect URI on the OAuth client exactly.</summary>
    public static string GoogleRedirectUri(HttpContext http, GoogleClient google) =>
        google.RedirectUriSetting ?? $"https://{http.Request.Host}/connections/google/callback";

    /// <summary>Where Intuit sends people back. Must be listed on the Intuit app exactly.</summary>
    public static string RedirectUri(HttpContext http, QuickBooksClient quickBooks) =>
        quickBooks.RedirectUriSetting ?? $"https://{http.Request.Host}/quickbooks/callback";
}
