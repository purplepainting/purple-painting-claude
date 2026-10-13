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
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    public DbSet<CommissionPayment> CommissionPayments => Set<CommissionPayment>();
    public DbSet<SalesActivity> SalesActivities => Set<SalesActivity>();
    public DbSet<AdSpend> AdSpends => Set<AdSpend>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<EquipmentItem> Equipment => Set<EquipmentItem>();
    public DbSet<EquipmentEvent> EquipmentEvents => Set<EquipmentEvent>();
    /// <summary>No longer used (reviews are per customer now, see CustomerReviews). Kept so the table and its rows stay.</summary>
    public DbSet<JobReview> JobReviews => Set<JobReview>();
    public DbSet<CustomerReview> CustomerReviews => Set<CustomerReview>();
    /// <summary>Facebook/Instagram ads (Meta Marketing API) connection. Entity in Data/MetaConnection.cs.</summary>
    public DbSet<MetaConnection> MetaConnections => Set<MetaConnection>();

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
        b.Entity<CommissionRule>(e =>
        {
            e.HasIndex(x => x.Person).IsUnique();
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Basis).HasMaxLength(20);
            e.Property(x => x.Percent).HasColumnType("decimal(6,3)");
        });
        b.Entity<CommissionPayment>(e =>
        {
            e.HasIndex(x => x.JobId);
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
        });
        b.Entity<SalesActivity>(e =>
        {
            e.HasIndex(x => x.Date);
            e.Property(x => x.Rep).HasMaxLength(200);
            e.Property(x => x.SoldAmount).HasColumnType("decimal(12,2)");
        });
        b.Entity<AdSpend>(e =>
        {
            e.HasIndex(x => new { x.Month, x.Channel }).IsUnique();
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.Channel).HasMaxLength(100);
            e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
        });
        b.Entity<InventoryItem>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Unit).HasMaxLength(30);
            e.Property(x => x.Vendor).HasMaxLength(120);
            e.Property(x => x.Sku).HasMaxLength(80);
            e.Property(x => x.OnHand).HasColumnType("decimal(12,2)");
            e.Property(x => x.UnitCost).HasColumnType("decimal(12,4)");
            e.Property(x => x.ReorderAt).HasColumnType("decimal(12,2)");
            e.Property(x => x.ReorderQty).HasColumnType("decimal(12,2)");
        });
        b.Entity<InventoryMovement>(e =>
        {
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.JobId);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.JobName).HasMaxLength(300);
            e.Property(x => x.Quantity).HasColumnType("decimal(12,2)");
            e.Property(x => x.UnitCost).HasColumnType("decimal(12,4)");
        });
        b.Entity<EquipmentItem>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Type).HasMaxLength(60);
            e.Property(x => x.Tag).HasMaxLength(80);
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.JobName).HasMaxLength(300);
            e.Property(x => x.AssignedTo).HasMaxLength(200);
            e.Property(x => x.Condition).HasMaxLength(30);
            e.Property(x => x.Value).HasColumnType("decimal(12,2)");
        });
        b.Entity<EquipmentEvent>(e =>
        {
            e.HasIndex(x => x.EquipmentId);
            e.Property(x => x.Kind).HasMaxLength(20);
        });
        b.Entity<JobReview>(e =>
        {
            e.HasIndex(x => x.JobId).IsUnique();
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Platform).HasMaxLength(40);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
        });
        b.Entity<CustomerReview>(e =>
        {
            e.HasIndex(x => x.AccountId).IsUnique();
            e.Property(x => x.AccountId).HasMaxLength(32);
            e.Property(x => x.CustomerName).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Platform).HasMaxLength(40);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
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

/// <summary>A salesperson's commission rule. Person = the JobTread "Salesperson" value on jobs.</summary>
public sealed class CommissionRule
{
    public int Id { get; set; }
    public required string Person { get; set; }
    public decimal Percent { get; set; }
    /// <summary>"collected" (paid as the customer pays), "contract", or "profit".</summary>
    public required string Basis { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>A commission paid to a salesperson for a job.</summary>
public sealed class CommissionPayment
{
    public int Id { get; set; }
    public required string JobId { get; set; }
    public string? JobName { get; set; }
    public required string Person { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>A door-to-door / canvassing log entry.</summary>
public sealed class SalesActivity
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public required string Rep { get; set; }
    public int Doors { get; set; }
    public int Leads { get; set; }
    public int Sold { get; set; }
    public decimal SoldAmount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>Monthly ad spend per channel (entered by hand until ad platforms are connected).</summary>
public sealed class AdSpend
{
    public int Id { get; set; }
    /// <summary>yyyy-MM</summary>
    public required string Month { get; set; }
    public required string Channel { get; set; }
    public decimal Amount { get; set; }
    public int? Leads { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>A sundry kept in stock at the shop (tape, plastic, caulk, sandpaper, rollers...).</summary>
public sealed class InventoryItem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Category { get; set; }
    /// <summary>roll, box, tube, each...</summary>
    public string? Unit { get; set; }
    public decimal OnHand { get; set; }
    /// <summary>Average cost per unit, updated on each received order.</summary>
    public decimal UnitCost { get; set; }
    /// <summary>Reorder when on hand drops to this.</summary>
    public decimal ReorderAt { get; set; }
    public decimal ReorderQty { get; set; }
    public string? Vendor { get; set; }
    public string? Sku { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>Stock in (bulk order received), out (issued to a job) or a count correction.</summary>
public sealed class InventoryMovement
{
    public long Id { get; set; }
    public int ItemId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>"received", "issued", "count".</summary>
    public required string Kind { get; set; }
    /// <summary>Positive in, negative out.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public string? JobId { get; set; }
    public string? JobName { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>A piece of equipment: where it is (shop or a job), its condition, who has it.</summary>
public sealed class EquipmentItem
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Type { get; set; }
    /// <summary>Serial number or asset tag.</summary>
    public string? Tag { get; set; }
    /// <summary>Null when it's at the shop.</summary>
    public string? JobId { get; set; }
    public string? JobName { get; set; }
    public string? AssignedTo { get; set; }
    /// <summary>"Good", "Needs service", "Out of service".</summary>
    public required string Condition { get; set; }
    public decimal? Value { get; set; }
    public DateOnly? LastServiced { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>History for a piece of equipment: moved, condition change, service, note.</summary>
public sealed class EquipmentEvent
{
    public long Id { get; set; }
    public int EquipmentId { get; set; }
    public DateTime At { get; set; }
    public required string Kind { get; set; }
    public string? Detail { get; set; }
    public string? By { get; set; }
}

/// <summary>
/// Review status for a finished job. No longer used: reviews are tracked per customer (CustomerReview).
/// The table is kept so earlier rows aren't lost (they only have a job id, so they can't be moved to a customer in SQL).
/// </summary>
public sealed class JobReview
{
    public int Id { get; set; }
    public required string JobId { get; set; }
    public string? JobName { get; set; }
    /// <summary>"requested", "received", "later", "skip".</summary>
    public required string Status { get; set; }
    /// <summary>Google, Yelp, Facebook, Houzz, Nextdoor, Other.</summary>
    public string? Platform { get; set; }
    public int? Rating { get; set; }
    public DateOnly? RequestedOn { get; set; }
    public DateOnly? ReceivedOn { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Review status for a customer (dashboard-owned). One review per customer, even with several jobs.
/// Keyed by the JobTread customer account id. Not written back to JobTread's "Left Review" field.
/// </summary>
public sealed class CustomerReview
{
    public int Id { get; set; }
    public required string AccountId { get; set; }
    public string? CustomerName { get; set; }
    /// <summary>"requested", "received", "later", "skip".</summary>
    public required string Status { get; set; }
    /// <summary>Google, Yelp, Facebook, Houzz, Nextdoor, Other.</summary>
    public string? Platform { get; set; }
    public int? Rating { get; set; }
    public string? Notes { get; set; }
    public DateOnly? RequestedOn { get; set; }
    public DateOnly? ReceivedOn { get; set; }
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
