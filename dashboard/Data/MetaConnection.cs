using System.ComponentModel.DataAnnotations;

namespace PurpleDashboard.Data;

/// <summary>
/// The shared Facebook/Instagram ads connection (one row). The long-lived Meta user token (about 60 days;
/// it can't be renewed without someone signing in again) is encrypted with ASP.NET Data Protection.
/// </summary>
public sealed class MetaConnection
{
    public int Id { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    [MaxLength(40)] public string? UserId { get; set; }
    public required string AccessTokenProtected { get; set; }
    /// <summary>When Meta says the token stops working (UTC). Null if Meta didn't say.</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>"act_1234567890".</summary>
    [MaxLength(40)] public string? AdAccountId { get; set; }
    [MaxLength(200)] public string? AdAccountName { get; set; }
    public DateTime ConnectedAt { get; set; }
    [MaxLength(200)] public string? ConnectedBy { get; set; }
}
