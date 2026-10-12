using Microsoft.EntityFrameworkCore;

namespace PurpleDashboard.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Registers the database when ConnectionStrings:Dashboard is set. Azure SQL in production;
    /// a local SQLite file ("Data Source=dashboard.db") works for development.
    /// </summary>
    public static void AddDashboardDatabase(this WebApplicationBuilder builder)
    {
        var status = new DatabaseStatus();
        builder.Services.AddSingleton(status);

        var cs = builder.Configuration.GetConnectionString("Dashboard");
        if (string.IsNullOrWhiteSpace(cs)) return;

        status.Configured = true;
        var sqlite = cs.TrimStart().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) && cs.Contains(".db", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddDbContext<DashboardDb>(o =>
        {
            if (sqlite) o.UseSqlite(cs);
            else o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure(3));
        });
        builder.Services.AddHostedService<DatabaseInitializer>();
    }
}

/// <summary>
/// Applies migrations in the background after the app has started (SQLite dev databases are just
/// created), so a slow or unreachable database never stops pages from loading. Until it succeeds,
/// pages that need the database say so; it tries again every minute, then every 5 minutes.
/// The free Azure SQL database sleeps when idle and takes about a minute to wake up.
/// </summary>
public sealed class DatabaseInitializer(IServiceProvider services, DatabaseStatus status, ILogger<DatabaseInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DashboardDb>();
                if (db.Database.IsSqlite()) await db.Database.EnsureCreatedAsync(stoppingToken);
                else await db.Database.MigrateAsync(stoppingToken);
                status.Problem = null;
                status.Available = true;
                logger.LogInformation("Dashboard database ready (attempt {Attempt})", attempt);
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // SQL error messages name the problem (login failed, firewall, server not found) but never the password.
                status.Problem = Short(e.GetBaseException().Message);
                logger.LogError(e, "Dashboard database setup failed (attempt {Attempt})", attempt);
            }
            try { await Task.Delay(attempt < 10 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
