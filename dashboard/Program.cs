var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Sign-in is handled by Azure App Service Authentication (Entra ID) in front of
// this app, so every request that reaches here is already a signed-in team member.

app.UseDefaultFiles();   // serves wwwroot/index.html at "/"
app.UseStaticFiles();

// Simple health check for deploy verification.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
