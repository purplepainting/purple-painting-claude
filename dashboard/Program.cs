using PurpleDashboard.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();

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
