using PurpleDashboard.Auth;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules;
using PurpleDashboard.Modules.Projects;
using PurpleDashboard.Modules.Scheduling;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    // Home page = the Projects list (also at /projects).
    options.Conventions.AddPageRoute("/Projects/Index", "");
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<CurrentUser>();

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

// Modules
builder.Services.Configure<MarginBands>(builder.Configuration.GetSection("Projects:Margin"));
builder.Services.AddScoped<ProjectsService>();
builder.Services.AddScoped<ITileMetric, ActiveProjectsMetric>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<ITileMetric, JobsThisWeekMetric>();

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
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapRazorPages().WithStaticAssets();

app.Run();
