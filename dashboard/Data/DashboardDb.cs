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
    public DbSet<GoogleConnection> GoogleConnections => Set<GoogleConnection>();
    public DbSet<JobTreadUserKey> JobTreadUserKeys => Set<JobTreadUserKey>();
    public DbSet<JobTreadOAuthClient> JobTreadOAuthClients => Set<JobTreadOAuthClient>();
    public DbSet<BonusPayout> BonusPayouts => Set<BonusPayout>();
    public DbSet<BonusPayment> BonusPayments => Set<BonusPayment>();
    public DbSet<BonusClawback> BonusClawbacks => Set<BonusClawback>();
    public DbSet<EffortRating> EffortRatings => Set<EffortRating>();
    public DbSet<BonusExclusion> BonusExclusions => Set<BonusExclusion>();
    public DbSet<BonusJobExclusion> BonusJobExclusions => Set<BonusJobExclusion>();
    public DbSet<BonusRetentionRelease> BonusRetentionReleases => Set<BonusRetentionRelease>();
    public DbSet<DashboardSetting> DashboardSettings => Set<DashboardSetting>();
    public DbSet<BonusAdjustment> BonusAdjustments => Set<BonusAdjustment>();
    public DbSet<PayLock> PayLocks => Set<PayLock>();
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
    public DbSet<HiringCandidate> HiringCandidates => Set<HiringCandidate>();
    public DbSet<HiringEvent> HiringEvents => Set<HiringEvent>();
    public DbSet<InterviewDay> InterviewDays => Set<InterviewDay>();
    public DbSet<InterviewBooking> InterviewBookings => Set<InterviewBooking>();
    public DbSet<CandidateDocument> CandidateDocuments => Set<CandidateDocument>();
    public DbSet<CustomerReview> CustomerReviews => Set<CustomerReview>();
    /// <summary>Facebook/Instagram ads (Meta Marketing API) connection. Entity in Data/MetaConnection.cs.</summary>
    public DbSet<MetaConnection> MetaConnections => Set<MetaConnection>();
    /// <summary>Who has signed in, their role and permission overrides. Entity in Data/UserAccess.cs.</summary>
    public DbSet<UserAccess> UserAccess => Set<UserAccess>();

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
        b.Entity<GoogleConnection>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Scopes).HasMaxLength(1000);
            e.Property(x => x.ConnectedBy).HasMaxLength(200);
            e.Property(x => x.AdsCustomerId).HasMaxLength(20);
            e.Property(x => x.AdsLoginCustomerId).HasMaxLength(20);
            e.Property(x => x.AdsCustomerName).HasMaxLength(200);
            e.Property(x => x.BusinessAccount).HasMaxLength(100);
            e.Property(x => x.BusinessLocation).HasMaxLength(100);
            e.Property(x => x.BusinessLocationTitle).HasMaxLength(200);
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
        b.Entity<BonusPayment>(e =>
        {
            e.HasIndex(x => x.PaidOn);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
            e.Property(x => x.Covers).HasMaxLength(100);
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.JobName).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<BonusExclusion>(e =>
        {
            e.HasIndex(x => new { x.Month, x.Person }).IsUnique();
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<BonusJobExclusion>(e =>
        {
            e.HasIndex(x => x.JobId).IsUnique();
            e.Property(x => x.JobId).HasMaxLength(50);
            e.Property(x => x.JobName).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            // Kareem, Oct 2026: both were paid out in the last spreadsheet bonus round, and QuickBooks costs and old
            // billing were loaded onto them in August to catch JobTread up, so they'd count as huge August swings.
            e.HasData(
                new BonusJobExclusion { Id = 1, JobId = "22PX9MaHxFt8", JobName = "Goleta Valley Cottage Hospital", Reason = "Paid out in the spreadsheet bonus round; QuickBooks catch-up costs and old billing were added in August", CreatedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), CreatedBy = "kareem@purplepainting.net" },
                new BonusJobExclusion { Id = 2, JobId = "22PXjg7k8Eu3", JobName = "1509 Shoreline Dr", Reason = "Paid out in the spreadsheet bonus round; QuickBooks catch-up costs were added in August", CreatedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), CreatedBy = "kareem@purplepainting.net" });
        });
        b.Entity<BonusRetentionRelease>(e =>
        {
            e.HasIndex(x => x.Month);
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<PayLock>(e =>
        {
            e.HasIndex(x => new { x.Kind, x.Person, x.Month }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.Net).HasColumnType("decimal(12,2)");
            e.Property(x => x.Owed).HasColumnType("decimal(12,2)");
            e.Property(x => x.Gross).HasColumnType("decimal(12,2)");
            e.Property(x => x.CheckNum).HasMaxLength(100);
            e.Property(x => x.LockedBy).HasMaxLength(200);
        });
        b.Entity<BonusAdjustment>(e =>
        {
            e.HasIndex(x => x.Month);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<EffortRating>(e =>
        {
            e.HasIndex(x => new { x.Month, x.MembershipId, x.RatedBy }).IsUnique();
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.MembershipId).HasMaxLength(32);
            e.Property(x => x.Person).HasMaxLength(200);
            e.Property(x => x.Score).HasColumnType("decimal(5,2)");
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.RatedBy).HasMaxLength(200);
        });
        b.Entity<BonusClawback>(e =>
        {
            e.HasIndex(x => x.JobId);
            e.Property(x => x.JobId).HasMaxLength(32);
            e.Property(x => x.JobName).HasMaxLength(300);
            e.Property(x => x.Month).HasMaxLength(7);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Pool).HasMaxLength(20);
            e.Property(x => x.Amount).HasColumnType("decimal(12,2)");
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
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
        b.Entity<HiringCandidate>(e =>
        {
            e.HasIndex(x => x.Stage);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Position).HasMaxLength(20);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.Source).HasMaxLength(40);
            e.Property(x => x.Experience).HasMaxLength(500);
            e.Property(x => x.Stage).HasMaxLength(20);
            e.Property(x => x.Interviewer).HasMaxLength(200);
            e.Property(x => x.TrialSupervisor).HasMaxLength(200);
            e.Property(x => x.TrialHours).HasColumnType("decimal(6,2)");
            e.Property(x => x.Onboarding).HasMaxLength(500);
            e.Property(x => x.NotHiredReason).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<HiringEvent>(e =>
        {
            e.HasIndex(x => x.CandidateId);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.FromStage).HasMaxLength(20);
            e.Property(x => x.ToStage).HasMaxLength(20);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.By).HasMaxLength(200);
        });
        b.Entity<InterviewDay>(e =>
        {
            e.HasIndex(x => x.Date);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<InterviewBooking>(e =>
        {
            e.HasIndex(x => new { x.DayId, x.Time }).IsUnique();
            e.HasIndex(x => new { x.DayId, x.CandidateId }).IsUnique();
            e.HasIndex(x => x.CandidateId);
            e.Property(x => x.ReminderSentBy).HasMaxLength(200);
            e.Property(x => x.ConfirmedBy).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
        });
        b.Entity<CandidateDocument>(e =>
        {
            e.HasIndex(x => x.CandidateId);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.UploadedBy).HasMaxLength(200);
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
/// The company Google account the dashboard is connected to (one row): it owns the Google Ads account and the
/// Google Business Profile. The refresh token is encrypted with ASP.NET Data Protection. Read only: the dashboard
/// never changes ads or the profile.
/// </summary>
public sealed class GoogleConnection
{
    public int Id { get; set; }
    /// <summary>The Google account that approved the connection.</summary>
    public required string Email { get; set; }
    public required string RefreshTokenProtected { get; set; }
    /// <summary>Scopes Google says were granted (space separated); the person can untick some on Google's page.</summary>
    public string? Scopes { get; set; }
    public DateTime ConnectedAt { get; set; }
    public string? ConnectedBy { get; set; }

    /// <summary>The Google Ads account read for Sales &amp; Marketing (10 digits, no dashes).</summary>
    public string? AdsCustomerId { get; set; }
    /// <summary>The manager account the Ads calls go through (login-customer-id), or null for direct access.</summary>
    public string? AdsLoginCustomerId { get; set; }
    public string? AdsCustomerName { get; set; }

    /// <summary>Business Profile account ("accounts/123") and location ("locations/456") reviews are read from.</summary>
    public string? BusinessAccount { get; set; }
    public string? BusinessLocation { get; set; }
    public string? BusinessLocationTitle { get; set; }
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
/// Before monthly bonuses (Oct 2026): a job's bonus once someone approved it, with each person's share
/// (LinesJson), then marked paid. Kept as history. Paid ones were copied into <see cref="BonusPayment"/>
/// (one per person, Source "migrated") the first time the monthly page loaded; MigratedAt marks that.
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
    /// <summary>When this approval was copied into BonusPayments (paid ones) or noted as history (approved, never paid).</summary>
    public DateTime? MigratedAt { get; set; }
}

/// <summary>
/// A bonus paid to someone (usually a Bonus Only payroll check). Counts in the calendar year of <see cref="PaidOn"/>; one dated
/// in month M+1 settles month M on the reconcile. Not tied to a job; JobId/Covers only note what it was for.
/// </summary>
public sealed class BonusPayment
{
    public int Id { get; set; }
    /// <summary>Name as in JobTread (the bonus lines use JobTread names).</summary>
    public required string Person { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }
    /// <summary>What it covers, free text (e.g. "Sep 2026 bonus").</summary>
    public string? Covers { get; set; }
    public string? JobId { get; set; }
    public string? JobName { get; set; }
    public string? Note { get; set; }
    /// <summary>"manual", "payroll" (Record from payroll) or "migrated" (from an old per-job approval).</summary>
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A rater's effort score for a field employee for one month (one per month, person and rater; saving again
/// replaces it). The plain average of everyone's scores feeds the effort part of the field pool split.
/// </summary>
public sealed class EffortRating
{
    public int Id { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public required string Month { get; set; }
    /// <summary>JobTread membership id of the person rated.</summary>
    public required string MembershipId { get; set; }
    /// <summary>Their JobTread name when rated.</summary>
    public required string Person { get; set; }
    /// <summary>Whole number on the Bonuses:EffortScaleMin–Max scale (default 1–10).</summary>
    public decimal Score { get; set; }
    public string? Notes { get; set; }
    /// <summary>The rater (signed-in email). Each rater has their own row; the person's score is the plain average.</summary>
    public required string RatedBy { get; set; }
    public DateTime RatedAt { get; set; }
}

/// <summary>
/// "Paid &amp; locked": a person's month frozen once it's paid (matched to their payroll check, or recorded and
/// confirmed). LinesJson is the snapshot of their lines at lock time; later changes to that month's data don't change
/// it, the difference flows into the next open month as an automatic adjustment. Kind "bonus" (commissions can reuse it).
/// </summary>
public sealed class PayLock
{
    public int Id { get; set; }
    public string Kind { get; set; } = "bonus";
    public required string Person { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public required string Month { get; set; }
    /// <summary>JSON array of { JobId, JobName, Role, Hours, Amount, Clawback } at lock time.</summary>
    public required string LinesJson { get; set; }
    /// <summary>Their net for the month at lock time (sum of the lines).</summary>
    public decimal Net { get; set; }
    public decimal Owed { get; set; }
    /// <summary>The payroll check it was matched to, if any.</summary>
    public string? CheckNum { get; set; }
    public DateOnly? CheckDate { get; set; }
    public decimal? Gross { get; set; }
    public DateTime LockedAt { get; set; }
    public string? LockedBy { get; set; }
}

/// <summary>
/// A manual line on someone's bonus year (waive a negative balance, correct something), with the reason.
/// Positive adds to their year, negative takes away.
/// </summary>
public sealed class BonusAdjustment
{
    public int Id { get; set; }
    public required string Person { get; set; }
    /// <summary>"yyyy-MM" it counts in.</summary>
    public required string Month { get; set; }
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A group of dashboard settings edited on the Settings pages, stored as JSON by key (e.g. "bonus-rules").
/// App settings / appsettings.json are only the defaults until something is saved here.
/// </summary>
public sealed class DashboardSetting
{
    [System.ComponentModel.DataAnnotations.Key, System.ComponentModel.DataAnnotations.MaxLength(100)]
    public required string Key { get; set; }
    public required string Json { get; set; }
    public DateTime UpdatedAt { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Bonus retention released early by hand in a month, for one person (or everyone when Person is null): whatever
/// is held for them at that point is paid out. Otherwise retention is released at year end (or when a job closes,
/// depending on the Bonuses:RetentionRelease rule).
/// </summary>
public sealed class BonusRetentionRelease
{
    public int Id { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public required string Month { get; set; }
    public string? Person { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>Someone left out of bonuses for one month by hand (write-up, more than 2 weeks off the schedule, ...).</summary>
public sealed class BonusExclusion
{
    public int Id { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public required string Month { get; set; }
    /// <summary>JobTread name (bonus lines are by name).</summary>
    public required string Person { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>A job left out of bonuses entirely (every month, both pools, its clawbacks), e.g. one already paid out before monthly bonuses.</summary>
public sealed class BonusJobExclusion
{
    public int Id { get; set; }
    public required string JobId { get; set; }
    public string? JobName { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// Bonus taken back on a job in a month (a callback repaint, costs not in JobTread, a job that lost money).
/// Kind "bonus": Amount is bonus dollars, from Pool ("both", "supervisor", "field"). Kind "cost": Amount is a
/// cost or loss not in JobTread, and the bonus percentages of it come back from each pool.
/// </summary>
public sealed class BonusClawback
{
    public int Id { get; set; }
    public required string JobId { get; set; }
    public string? JobName { get; set; }
    /// <summary>"yyyy-MM".</summary>
    public required string Month { get; set; }
    public string Kind { get; set; } = "bonus";
    public string Pool { get; set; } = "both";
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
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
/// Someone applying to work for us (Employees → Hiring). Moves through stages from the first text to hired.
/// The interview sheet answers are kept as JSON (Modules/Hiring/InterviewSheet.cs) so the questions can change
/// without a migration; rating and interview date are columns so lists can show them.
/// </summary>
public sealed class HiringCandidate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Phone { get; set; }
    /// <summary>"helper", "painter", "foreman", "sales".</summary>
    public required string Position { get; set; }
    /// <summary>"en", "es", "both".</summary>
    public required string Language { get; set; }
    /// <summary>Craigslist, Indeed, Referral, Other.</summary>
    public string? Source { get; set; }
    public string? Experience { get; set; }
    /// <summary>A key from HiringStages (texted, booked, confirmed, interviewed, trial, hired, not_hired, no_show, withdrew).</summary>
    public required string Stage { get; set; }
    public DateTime StageAt { get; set; }
    /// <summary>1 to 5 stars, from the interview sheet.</summary>
    public int? Rating { get; set; }
    public string? SheetJson { get; set; }
    public DateOnly? InterviewedOn { get; set; }
    public string? Interviewer { get; set; }
    public DateOnly? TrialDate { get; set; }
    public string? TrialSupervisor { get; set; }
    public string? TrialNotes { get; set; }
    public decimal? TrialHours { get; set; }
    public DateOnly? HiredOn { get; set; }
    /// <summary>Onboarding checklist keys done, comma separated (HiringLists.Onboarding).</summary>
    public string? Onboarding { get; set; }
    public string? NotHiredReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A candidate's history: stage changes, bookings, notes (who and when).</summary>
public sealed class HiringEvent
{
    public long Id { get; set; }
    public int CandidateId { get; set; }
    public DateTime At { get; set; }
    /// <summary>"stage", "booking", "note", "sheet", "trial", "hire", "edit".</summary>
    public required string Kind { get; set; }
    public string? FromStage { get; set; }
    public string? ToStage { get; set; }
    public string? Detail { get; set; }
    public string? By { get; set; }
}

/// <summary>A group interview day (usually a Wednesday at Topa Topa) split into slots.</summary>
public sealed class InterviewDay
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public required string Location { get; set; }
    public TimeOnly Start { get; set; }
    /// <summary>End of the window: the last slot starts one slot length before this.</summary>
    public TimeOnly End { get; set; }
    public int SlotMinutes { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>A candidate booked into a slot on an interview day, with the reminder and day-of confirmation.</summary>
public sealed class InterviewBooking
{
    public int Id { get; set; }
    public int DayId { get; set; }
    public int CandidateId { get; set; }
    public TimeOnly Time { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public string? ReminderSentBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A file kept with a candidate (scanned interview sheet, resume, trial day timesheet photo). Hiring replaces the
/// interview spreadsheets and paper sheets, so the file itself lives in the database (max 10 MB). List queries
/// must project without Data; only the download handler reads it.
/// </summary>
public sealed class CandidateDocument
{
    public int Id { get; set; }
    public int CandidateId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required byte[] Data { get; set; }
    /// <summary>"Interview sheet", "Resume", "Trial timesheet", "Other".</summary>
    public required string Kind { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
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
