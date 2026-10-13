using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
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
})
// Users & access: every page and handler is checked against Auth/AccessMap.cs (default deny).
.AddMvcOptions(o => o.Filters.Add<AccessFilter>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<UserAccessStore>();
builder.Services.AddScoped<Permissions>();
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

// Google (read only): Google Ads + Business Profile reviews through one company Google account. See docs/GOOGLE.md.
builder.Services.Configure<PurpleDashboard.Integrations.Google.GoogleOptions>(o =>
{
    o.ClientId = builder.Configuration["GOOGLE_CLIENT_ID"];
    o.ClientSecret = builder.Configuration["GOOGLE_CLIENT_SECRET"];
    o.AdsDeveloperToken = builder.Configuration["GOOGLE_ADS_DEVELOPER_TOKEN"];
    o.AdsLoginCustomerId = builder.Configuration["GOOGLE_ADS_LOGIN_CUSTOMER_ID"];
    o.RedirectUri = builder.Configuration["GOOGLE_REDIRECT_URI"];
    if (builder.Configuration["Google:AdsChannel"] is { Length: > 0 } channel) o.AdsChannel = channel;
    if (builder.Environment.IsDevelopment()) o.FixtureDirectory = builder.Configuration["Google:FixtureDirectory"];
});
builder.Services.AddHttpClient<PurpleDashboard.Integrations.Google.GoogleClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
// Facebook/Instagram ads (Meta Marketing API, read only). App keys from META_APP_ID / META_APP_SECRET in Azure; see docs/META.md.
builder.Services.Configure<PurpleDashboard.Integrations.Meta.MetaOptions>(o =>
{
    o.AppId = builder.Configuration["META_APP_ID"];
    o.AppSecret = builder.Configuration["META_APP_SECRET"];
    o.RedirectUri = builder.Configuration["META_REDIRECT_URI"];
    o.LoginConfigId = builder.Configuration["META_LOGIN_CONFIG_ID"];
    if (builder.Environment.IsDevelopment()) o.FixtureDirectory = builder.Configuration["Meta:FixtureDirectory"];
});
builder.Services.AddHttpClient<PurpleDashboard.Integrations.Meta.MetaClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.MetaAdsService>();

// Modules
builder.Services.AddScoped<ChangeLogWriter>();
builder.Services.AddScoped<PurpleDashboard.Modules.Changes.ChangeLogReader>();
builder.Services.AddScoped<JobTreadUserKeys>();
builder.Services.AddHostedService<JobTreadChangeWatcher>();
builder.Services.AddHttpClient<JobTreadOAuth>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<PurpleDashboard.Modules.People.PeopleService>();
builder.Services.AddScoped<PurpleDashboard.Modules.People.TeamDirectory>();
builder.Services.AddScoped<PurpleDashboard.Modules.People.QbEmployeeStatus>();
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
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.BonusMonths>();
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.BonusRulesStore>();
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.PayrollPayments>();
builder.Services.AddScoped<PurpleDashboard.Modules.Financials.JobReconciliationService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.SalesService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.CommissionPayroll>();
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.GoogleAdsService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.QuickBooksAdSpendService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Sales.JobTreadLeadsService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Customers.GoogleReviewsService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Equipment.EquipmentService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Hiring.HiringService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Vendors.PayablesService>();
builder.Services.AddScoped<PurpleDashboard.Modules.Customers.CustomersService>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Customers.CustomersOwingMetric>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Vendors.DueSoonMetric>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Equipment.LowStockMetric>();
builder.Services.AddScoped<ITileMetric, PurpleDashboard.Modules.Sales.CommissionsOwedMetric>();
builder.Services.AddScoped<PurpleDashboard.Modules.Search.SearchService>();
builder.Services.AddSingleton<PurpleDashboard.Infrastructure.MenuNumbers>();

var app = builder.Build();

// Fill the menu numbers (and the caches behind them) as soon as the app is up.
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<PurpleDashboard.Infrastructure.MenuNumbers>().StartRefresh());

// Sign-in is handled by Azure App Service Authentication (Entra ID) in front of
// this app, so every request that reaches here is already a signed-in team member.
// Auth/CurrentUser.cs reads who they are from the headers Easy Auth adds.
// HTTPS is enforced by the App Service "HTTPS Only" setting.

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}
app.UseStatusCodePagesWithReExecute("/error/{0}");

// Every POST handler should be in the access map; anything missing works for admins only. Say so at startup.
foreach (var missing in AccessFilter.FindUnmapped())
    app.Logger.LogWarning("Access map: {Missing} isn't in Auth/AccessMap.cs, so only admins can use it", missing);

if (app.Environment.IsDevelopment())
{
    // Local testing of roles: ?devuser=someone@purplepainting.net acts as that person (cookie), ?devuser= goes back
    // to Dev:UserEmail / the default local developer. Development only; Easy Auth headers always win.
    app.Use(async (context, next) =>
    {
        if (context.Request.Query.TryGetValue("devuser", out var devUser))
        {
            if (string.IsNullOrWhiteSpace(devUser)) context.Response.Cookies.Delete(CurrentUser.DevUserCookie);
            else context.Response.Cookies.Append(CurrentUser.DevUserCookie, devUser.ToString().Trim(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
            var query = new Microsoft.AspNetCore.Http.Extensions.QueryBuilder(context.Request.Query
                .Where(q => q.Key != "devuser")
                .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string>(q.Key, v ?? ""))));
            context.Response.Redirect(context.Request.PathBase + context.Request.Path + query.ToQueryString());
            return;
        }
        await next();
    });
}

app.MapStaticAssets();

// Simple health check for deploy verification.
app.MapGet("/health", (DatabaseStatus db) => Results.Ok(new
{
    status = "ok",
    database = db.Available ? "ok" : !db.Configured ? "not set up" : db.Problem is null ? "connecting" : "error",
    databaseProblem = db.Available ? null : db.Problem,
    jobTreadLiveUpdates = JobTreadChangeWatcher.Enabled ? "on" : "off",
    lastJobTreadChange = JobTreadChangeWatcher.LastChangeAt,
    jobTreadCache = JobTreadFreshness.Describe(),
}));

app.MapRazorPages().WithStaticAssets();

app.Run();
