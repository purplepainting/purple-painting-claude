using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace PurpleDashboard.Data;

/// <summary>
/// One row per person who has signed in to the dashboard: their role and per-person permission overrides
/// (Settings → Users &amp; access). Rows are added on a person's first request (role View only). Keyed by
/// email (lowercase) because that's what Easy Auth gives on every request and what admins recognise.
/// Bootstrap admins (setting ADMIN_EMAILS) are Admin whatever the row says.
/// </summary>
[Index(nameof(Email), IsUnique = true)]
public sealed class UserAccess
{
    public int Id { get; set; }
    [MaxLength(200)] public required string Email { get; set; }
    [MaxLength(200)] public string? Name { get; set; }
    /// <summary>"admin", "office", "supervisor" or "viewonly" (Auth/AccessRoles.cs).</summary>
    [MaxLength(20)] public required string Role { get; set; }
    /// <summary>{"permission.key": true|false}: grant or deny on top of the role. Null when there are none.</summary>
    [MaxLength(4000)] public string? OverridesJson { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}
