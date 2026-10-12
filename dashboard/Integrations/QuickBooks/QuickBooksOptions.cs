namespace PurpleDashboard.Integrations.QuickBooks;

/// <summary>
/// The dashboard's Intuit developer app. Azure settings QBO_CLIENT_ID and QBO_CLIENT_SECRET
/// (from developer.intuit.com > the app > Keys & credentials, Production). Setup: docs/QUICKBOOKS.md.
/// </summary>
public sealed class QuickBooksOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Must match a redirect URI on the Intuit app exactly. Defaults to https://{this site}/quickbooks/callback.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>Extra OAuth scopes, space separated (e.g. payroll scopes once Intuit approves them). Setting QBO_EXTRA_SCOPES.</summary>
    public string? ExtraScopes { get; set; }

    /// <summary>"production" (default) or "sandbox" for an Intuit test company.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>Local development only: a folder of saved QuickBooks responses ({report}.json, query-{Entity}.json).</summary>
    public string? FixtureDirectory { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(FixtureDirectory) || !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public string ApiBase => Environment == "sandbox"
        ? "https://sandbox-quickbooks.api.intuit.com/"
        : "https://quickbooks.api.intuit.com/";

    public string Scopes => ("com.intuit.quickbooks.accounting " + ExtraScopes).Trim();
}

public sealed class QuickBooksException(string message, Exception? inner = null) : Exception(message, inner);
