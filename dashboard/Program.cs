using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules;
using PurpleDashboard.Modules.Projects;
using PurpleDashboard.Modules.Scheduling;

var builder = WebApplication.CreateBuilder(args);

// Fetch/JSON posts send the antiforgery token in this header.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddRazorPages(options =>
{
    // Home page = the Projects list (also at /projects).
    options.Conventions.AddPageRoute("/Projects/Index", "");
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<CurrentUser>();
builder.AddDashboardDatabase();

// JobTread. The grant key comes from the JOBTREAD_API_KEY setting in Azure (same name as the function app).
builder.Services.Configure<JobTreadOptions>(o =>
{
    o.GrantKey = builder.Configuration["JOBTREAD_API_KEY"];
    o.FixtureDirectory = builder.Configuration["JobTread:FixtureDirectory"];
});
if (builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["JobTread:FixtureDirectory"]))
{
    builder.Services.AddSingleton<IJobTreadClient, FixtureJobTreadClient>();
}
else
{
    builder.Services.AddHttpClient<IJobTreadClient, JobTreadClient>(c =>
    {
        c.BaseAddress = new Uri("https://api.jobtread.com/");
        c.Timeout = TimeSpan.FromSeconds(30);
    });
}

// QuickBooks (read only). Intuit app keys come from QBO_CLIENT_ID / QBO_CLIENT_SECRET in Azure; see docs/QUICKBOOKS.md.
builder.Services.Configure<PurpleDashboard.Integrations.QuickBooks.QuickBooksOptions>(o =>
{
    o.ClientId = builder.Configuration["QBO_CLIENT_ID"];
    o.ClientSecret = builder.Configuration["QBO_CLIENT_SECRET"];
    o.RedirectUri = builder.Configuration["QBO_REDIRECT_URI"];
    o.ExtraScopes = builder.Configuration["QBO_EXTRA_SCOPES"];
    o.Environment = builder.Configuration["QBO_ENVIRONMENT"] ?? "production";
    if (builder.Environment.IsDevelopment()) o.FixtureDirectory = builder.Configuration["QuickBooks:FixtureDirectory"];
});
builder.Services.AddHttpClient<PurpleDashboard.Integrations.QuickBooks.QuickBooksClient>(c => c.Timeout = TimeSpan.FromSeconds(30));

// Modules
builder.Services.AddScoped<ChangeLogWriter>();
builder.Services.AddScoped<JobTreadUserKeys>();
builder.Services.AddHttpClient<JobTreadOAuth>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<PurpleDashboard.Modules.People.PeopleService>();
builder.Services.AddScoped<PurpleDashboard.Modules.People.TeamDirectory>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.People.TeamMembersMetric>();
builder.Services.AddScoped<ProjectsService>();
builder.Services.AddScoped<ITileMetric, ActiveProjectsMetric>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<SchedulePushService>();
builder.Services.AddScoped<LaborCostService>();
builder.Services.Configure<CrewLevelOptions>(builder.Configuration.GetSection("Crew"));
builder.Services.Configure<PurpleDashboard.Modules.People.PmColorOptions>(builder.Configuration.GetSection("ProjectManagers"));
builder.Services.AddScoped<ITileMetric, JobsThisWeekMetric>();
builder.Services.Configure<PurpleDashboard.Modules.Financials.AccountRulesOptions>(builder.Configuration.GetSection("Financials"));
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.AccountRules>();
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.FinancialsService>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Financials.NetProfitMetric>();
builder.Services.Configure<PurpleDashboard.Modules.Financials.BonusOptions>(builder.Configuration.GetSection("Bonuses"));
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.BonusService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.SalesService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Equipment.EquipmentService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Vendors.PayablesService>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Vendors.DueSoonMetric>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Equipment.LowStockMetric>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Sales.CommissionsOwedMetric>();

var app = builder.Build();

// Sign-in is handled by Azure App Service Authentication (Entra ID) in front of
// this app, so every request that reaches here is already a signed-in team member.
// Auth/CurrentUser.cs reads who they are from the headers Easy Auth adds.
// HTTPS is enforced by the App Service "HTTPS Only" setting.

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.MapStaticAssets();

// Simple health check for deploy verification.
app.MapGet("/health", (DatabaseStatus db) => Results.Ok(new
{
    status = "ok",
    database = db.Available ? "ok" : !db.Configured ? "not set up" : db.Problem is null ? "connecting" : "error",
    databaseProblem = db.Available ? null : db.Problem,
}));

app.MapRazorPages().WithStaticAssets();

app.Run();
