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
    }

    /// <summary>
    /// Applies migrations at startup (SQLite dev databases are just created). A failure doesn't stop
    /// the app: pages that need the database say so instead.
    /// </summary>
    public static async Task PrepareDashboardDatabaseAsync(this WebApplication app)
    {
        var status = app.Services.GetRequiredService<DatabaseStatus>();
        if (!status.Configured) return;
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DashboardDb>();
            if (db.Database.IsSqlite()) await db.Database.EnsureCreatedAsync();
            else await db.Database.MigrateAsync();
            status.Available = true;
        }
        catch (Exception e)
        {
            status.Problem = e.Message;
            app.Logger.LogError(e, "Dashboard database setup failed");
        }
    }
}
