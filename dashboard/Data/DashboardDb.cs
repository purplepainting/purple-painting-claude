using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PurpleDashboard.Data;

/// <summary>
/// The dashboard's own database (Azure SQL). Holds data that exists in neither JobTread nor
/// QuickBooks. Connection string: ConnectionStrings:Dashboard (Azure "Connection strings", name Dashboard).
/// Schema changes are EF Core migrations in Data/Migrations, applied automatically at startup.
/// </summary>
public sealed class DashboardDb(DbContextOptions<DashboardDb> options) : DbContext(options)
{
    public DbSet<EmployeeProfile> EmployeeProfiles => Set<EmployeeProfile>();
    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();
    public DbSet<QuickBooksConnection> QuickBooksConnections => Set<QuickBooksConnection>();
    public DbSet<JobTreadUserKey> JobTreadUserKeys => Set<JobTreadUserKey>();
    public DbSet<JobTreadOAuthClient> JobTreadOAuthClients => Set<JobTreadOAuthClient>();
    public DbSet<BonusPayout> BonusPayouts => Set<BonusPayout>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<EmployeeProfile>(e =>
        {
            e.HasIndex(x => x.MembershipId).IsUnique();
            e.Property(x => x.MembershipId).HasMaxLength(32);
            e.Property(x => x.Level).HasMaxLength(20);
            e.Property(x => x.Color).HasMaxLength(9);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
            e.Property(x => x.QbPayType).HasMaxLength(20);
        });
        b.Entity<QuickBooksConnection>(e =>
        {
            e.Property(x => x.RealmId).HasMaxLength(40);
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.ConnectedBy).HasMaxLength(200);
        });
        b.Entity<JobTreadUserKey>(e =>
        {
            e.HasIndex(x => x.UserObjectId).IsUnique();
            e.Property(x => x.UserObjectId).HasMaxLength(64);
            e.Property(x => x.UserEmail).HasMaxLength(200);
            e.Property(x => x.JobTreadUserId).HasMaxLength(32);
            e.Property(x => x.JobTreadUserName).HasMaxLength(200);
            e.Property(x => x.AuthKind).HasMaxLength(20);
        });
        b.Entity<JobTreadOAuthClient>(e =>
        {
            e.Property(x => x.ClientId).HasMaxLength(200);
            e.Property(x => x.RedirectUri).HasMaxLength(300);
        });
        b.Entity<BonusPayout>(e =>
        {
            e.HasIndex(x => x.JobId).IsUnique();
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.JobName).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.ApprovedBy).HasMaxLength(200);
            e.Property(x => x.PaidBy).HasMaxLength(200);
        });
        b.Entity<ChangeLogEntry>(e =>
        {
            e.HasIndex(x => x.At);
            e.Property(x => x.User).HasMaxLength(200);
            e.Property(x => x.Area).HasMaxLength(40);
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.Target).HasMaxLength(300);
            e.Property(x => x.Result).HasMaxLength(40);
        });
    }
}

/// <summary>Dashboard settings for one JobTread staff member (keyed by JobTread membership id).</summary>
public sealed class EmployeeProfile
{
    public int Id { get; set; }
    public required string MembershipId { get; set; }

    /// <summary>"foreman", "painter", "helper", "supervisor", or null to use the default.</summary>
    public string? Level { get; set; }

    /// <summary>Hex color (e.g. #0F766E), used for PMs.</summary>
    public string? Color { get; set; }

    public string? Notes { get; set; }

    /// <summary>QuickBooks base pay (hourly rate, or annual salary when QbPayType is "Salary"). Entered or imported; the QuickBooks API doesn't share pay rates.</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(10,2)")]
    public decimal? QbBasePay { get; set; }
    public string? QbPayType { get; set; }

    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// The QuickBooks company the dashboard is connected to (one row). The refresh token is encrypted
/// with ASP.NET Data Protection; Intuit replaces it on every refresh, so the newest one is saved each time.
/// </summary>
public sealed class QuickBooksConnection
{
    public int Id { get; set; }
    public required string RealmId { get; set; }
    public string? CompanyName { get; set; }
    public required string RefreshTokenProtected { get; set; }
    public DateTime RefreshTokenExpiresAt { get; set; }
    public DateTime ConnectedAt { get; set; }
    public string? ConnectedBy { get; set; }
    public string? Scopes { get; set; }
}

/// <summary>
/// A dashboard user's own JobTread grant key (encrypted), so schedule changes they push show as
/// them in JobTread. Keyed by their Entra object id. Reading pages still uses the shared key.
/// </summary>
public sealed class JobTreadUserKey
{
    public int Id { get; set; }
    public required string UserObjectId { get; set; }
    public string? UserEmail { get; set; }
    public required string GrantKeyProtected { get; set; }
    public string? JobTreadUserId { get; set; }
    public string? JobTreadUserName { get; set; }
    public DateTime SavedAt { get; set; }

    /// <summary>
    /// How they linked: null or "key" (pasted grant key, in GrantKeyProtected); "oauth-pave" (signed in;
    /// the access token works as a Pave grant key); "oauth-mcp" (signed in; queries go through JobTread's
    /// MCP server at api.jobtread.com/mcp with the access token).
    /// </summary>
    public string? AuthKind { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTime? AccessExpiresAt { get; set; }
}

/// <summary>
/// The dashboard's registration with JobTread's OAuth server (dynamic client registration, done
/// automatically the first time someone signs in). One row per redirect URI.
/// </summary>
public sealed class JobTreadOAuthClient
{
    public int Id { get; set; }
    public required string ClientId { get; set; }
    public string? ClientSecretProtected { get; set; }
    public required string RedirectUri { get; set; }
    public DateTime RegisteredAt { get; set; }
}

/// <summary>
/// A job's bonus once someone approves it: the profit and pools at that moment and each person's share
/// (LinesJson), then marked paid. Before approval, bonuses are calculated live from JobTread.
/// </summary>
public sealed class BonusPayout
{
    public int Id { get; set; }
    public required string JobId { get; set; }
    public string? JobName { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(12,2)")] public decimal Profit { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(12,2)")] public decimal SupervisorPool { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(12,2)")] public decimal FieldPool { get; set; }
    /// <summary>"approved" or "paid".</summary>
    public required string Status { get; set; }
    /// <summary>JSON array of { name, role, hours, amount }.</summary>
    public required string LinesJson { get; set; }
    public DateTime ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaidBy { get; set; }
}

/// <summary>Audit log: who changed what, and the result (schedule pushes, employee edits).</summary>
public sealed class ChangeLogEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string? User { get; set; }
    public required string Area { get; set; }      // "schedule", "employees"
    public required string Action { get; set; }    // "update", "create", "delete", "save"
    public string? Target { get; set; }            // e.g. "SBCC Cosmetology · Painting"
    public string? Details { get; set; }           // JSON of the change
    public string? Result { get; set; }            // "Done", "Conflict", "Failed"
}

/// <summary>Lets `dotnet ef migrations add` build the model without a real database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DashboardDb>
{
    public DashboardDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DashboardDb>().UseSqlServer("Server=design-time;Database=design-time").Options);
}

/// <summary>Whether the database is set up and reachable. Pages check this before saving.</summary>
public sealed class DatabaseStatus
{
    public bool Configured { get; set; }
    public volatile bool Available;
    public volatile string? Problem;
}
