using Microsoft.AspNetCore.Mvc;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.Meta;

namespace PurpleDashboard.ViewComponents;

/// <summary>
/// The Facebook/Instagram ads card on Connections: settings check, connect/disconnect, token expiry and the ad
/// account picker. Its buttons post to /connections/meta (Pages/Connections/Meta.cshtml.cs).
/// </summary>
public class MetaConnectionViewComponent(MetaClient meta, DatabaseStatus database, ILogger<MetaConnectionViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var model = new MetaConnectionCard
        {
            Configured = meta.IsConfigured,
            MissingSettings = [.. meta.HasAppId ? [] : new[] { "META_APP_ID" }, .. meta.HasAppSecret ? [] : new[] { "META_APP_SECRET" }],
            DatabaseAvailable = database.Available,
            Fixtures = meta.UsingFixtures,
        };
        if (!model.Configured) return View(model);

        try
        {
            model.Connection = await meta.GetConnectionAsync(ct);
            if (model.Connection is not null) model.Accounts = await meta.AdAccountsAsync(ct);
        }
        catch (Exception e) when (e is MetaException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Reading Meta ad accounts failed");
            model.Problem = e is MetaException ? e.Message : "Couldn't reach Meta to list ad accounts. Try again in a minute.";
            model.NeedsReconnect = e is MetaException { NeedsReconnect: true };
        }
        return View(model);
    }
}

public sealed class MetaConnectionCard
{
    public bool Configured { get; init; }
    public string[] MissingSettings { get; init; } = [];
    public bool DatabaseAvailable { get; init; }
    public bool Fixtures { get; init; }
    public MetaConnection? Connection { get; set; }
    public List<MetaAdAccount> Accounts { get; set; } = [];
    public string? Problem { get; set; }
    public bool NeedsReconnect { get; set; }

    public int? DaysLeft => Connection?.ExpiresAt is { } e ? (int)Math.Floor((e - DateTime.UtcNow).TotalDays) : null;
    public bool Expired => Connection?.ExpiresAt is { } e && e <= DateTime.UtcNow;
    public bool ExpiresSoon => DaysLeft is { } d && d < MetaClient.ReconnectWarningDays;
}
