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
