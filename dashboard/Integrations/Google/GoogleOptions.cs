namespace PurpleDashboard.Integrations.Google;

/// <summary>
/// The dashboard's Google Cloud OAuth client and Google Ads developer token. Azure settings:
/// GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET, GOOGLE_ADS_DEVELOPER_TOKEN and optional GOOGLE_ADS_LOGIN_CUSTOMER_ID
/// (the manager account to call through), GOOGLE_REDIRECT_URI. Setup: docs/GOOGLE.md.
/// </summary>
public sealed class GoogleOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>From the Google Ads manager account: Admin > API Center. Needs Basic access to read the real account.</summary>
    public string? AdsDeveloperToken { get; set; }

    /// <summary>
    /// Optional: the manager (MCC) account id, digits only, sent as login-customer-id on every Ads call. When empty the
    /// dashboard uses the manager it found the chosen account under (or none when the login has direct access).
    /// </summary>
    public string? AdsLoginCustomerId { get; set; }

    /// <summary>Must match an authorized redirect URI on the OAuth client exactly. Defaults to https://{this site}/connections/google/callback.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// The lead source name Google Ads spend counts under on Sales &amp; Marketing (same as JobTread's "How bid came in").
    /// Setting Google:AdsChannel; default "Google Ads".
    /// </summary>
    public string AdsChannel { get; set; } = "Google Ads";

    /// <summary>Local development only: a folder of saved Google responses (see docs/GOOGLE.md, "Fixtures").</summary>
    public string? FixtureDirectory { get; set; }

    public bool UseFixtures => !string.IsNullOrWhiteSpace(FixtureDirectory);

    public bool IsConfigured => UseFixtures || !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool HasDeveloperToken => UseFixtures || !string.IsNullOrWhiteSpace(AdsDeveloperToken);

    public const string AdsScope = "https://www.googleapis.com/auth/adwords";
    public const string BusinessScope = "https://www.googleapis.com/auth/business.manage";
    public const string Scopes = "openid email " + AdsScope + " " + BusinessScope;
}

/// <summary>A Google call failed; <see cref="Exception.Message"/> is written for the person looking at the page.</summary>
public sealed class GoogleException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Short machine-readable cause when known: "not-connected", "reconnect", "developer-token", "ads-access", "business-access", "api-disabled", "scope".</summary>
    public string? Kind { get; init; }
}
