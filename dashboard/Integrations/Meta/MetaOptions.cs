namespace PurpleDashboard.Integrations.Meta;

/// <summary>
/// The dashboard's Meta (Facebook) developer app. Azure settings META_APP_ID and META_APP_SECRET
/// (developers.facebook.com > the app > App settings > Basic). Setup: docs/META.md.
/// </summary>
public sealed class MetaOptions
{
    /// <summary>
    /// Graph / Marketing API version used for every call, in one place. v26.0 was released 2026-07-29.
    /// Meta retires Marketing API versions a few months to a year after the next one ships; bump this when
    /// the changelog (developers.facebook.com/docs/graph-api/changelog) lists a newer version.
    /// </summary>
    public const string GraphVersion = "v26.0";

    public const string GraphBase = "https://graph.facebook.com/" + GraphVersion + "/";
    public const string DialogUrl = "https://www.facebook.com/" + GraphVersion + "/dialog/oauth";

    /// <summary>Read-only ads permission. Listing ad accounts (/me/adaccounts) works with it too.</summary>
    public const string Scopes = "ads_read";

    public string? AppId { get; set; }
    public string? AppSecret { get; set; }

    /// <summary>Must match a Valid OAuth Redirect URI on the Meta app exactly. Defaults to https://{this site}/connections/meta/callback.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Facebook Login for Business configuration id (META_LOGIN_CONFIG_ID, optional). When set, the login dialog
    /// is sent config_id instead of scope (Business-type apps using a Login for Business configuration).
    /// </summary>
    public string? LoginConfigId { get; set; }

    /// <summary>Local development only: a folder of saved Graph responses (me.json, adaccounts.json, insights-campaign.json, insights-platform.json).</summary>
    public string? FixtureDirectory { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(FixtureDirectory) || !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

public sealed class MetaException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The saved token was rejected (expired, revoked, password changed): reconnect.</summary>
    public bool NeedsReconnect { get; init; }

    /// <summary>Meta asked for a smaller request (insights): retried as an async report.</summary>
    internal bool TooMuchData { get; init; }
}
