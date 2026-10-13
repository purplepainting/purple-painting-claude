using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PurpleDashboard.Auth;

/// <summary>
/// Runs before every Razor Pages handler (registered globally in Program.cs) and enforces
/// <see cref="AccessMap"/>: the page's view permission first, then, for anything but GET/HEAD, the
/// handler's permission. Unmapped pages and unmapped POST handlers need Admin and log a warning.
/// A denied request gets 403: JSON {error} for fetch calls, otherwise the error page with
/// "You don't have access to X — ask an admin".
/// </summary>
public sealed class AccessFilter(Permissions permissions, ILogger<AccessFilter> logger) : IAsyncPageFilter
{
    public const string DeniedItem = "AccessDenied";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        await permissions.LoadAsync(http.RequestAborted);

        var page = context.ActionDescriptor.ViewEnginePath;
        var handler = context.HandlerMethod?.Name ?? "";
        var safe = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

        var pageRule = AccessMap.PageRule(page);
        if (pageRule is null)
        {
            logger.LogWarning("Page {Page} isn't in AccessMap.Pages; only admins can open it", page);
            pageRule = AccessRule.AdminOnly("this page");
        }
        if (!pageRule.Allows(permissions))
        {
            // The home page is Projects; someone without it starts at the first module they can open.
            if (http.Request.Path == "/" && safe)
            {
                var first = Modules.ModuleRegistry.All.FirstOrDefault(m => AccessMap.CanSeeModule(permissions, m.Key));
                context.Result = new RedirectResult(first?.Route ?? "/settings");
                return;
            }
            Deny(context, pageRule.Area, edit: false, page, handler);
            return;
        }

        var handlerRule = AccessMap.HandlerRule(page, handler);
        if (handlerRule is null && !safe)
        {
            logger.LogWarning("{Method} {Page} handler '{Handler}' isn't in AccessMap.Handlers; only admins can use it", http.Request.Method, page, handler);
            handlerRule = AccessRule.AdminOnly(pageRule.Area);
        }
        if (handlerRule is not null && !handlerRule.Allows(permissions))
        {
            Deny(context, handlerRule.Area, edit: !safe || handlerRule.AnyOf.Any(k => k.EndsWith(".edit")), page, handler);
            return;
        }

        await next();
    }

    private void Deny(PageHandlerExecutingContext context, string area, bool edit, string page, string handler)
    {
        var http = context.HttpContext;
        var message = edit
            ? $"You can't make changes in {area}. Ask an admin if you need to."
            : $"You don't have access to {area}. Ask an admin if you need it.";
        logger.LogInformation("Access denied: {Email} ({Role}) {Method} {Page} handler '{Handler}' ({Area})",
            permissions.Email, permissions.Role, http.Request.Method, page, handler, area);

        if (WantsJson(http.Request))
        {
            context.Result = new JsonResult(new { error = message }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        // Empty 403: UseStatusCodePagesWithReExecute shows /error/403, which reads the message from Items.
        http.Items[DeniedItem] = message;
        context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    private static bool WantsJson(HttpRequest r) =>
        r.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
        || (r.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false)
        || r.Headers.ContainsKey("RequestVerificationToken")   // fetch posts (site.js, hiring.js, schedule editor)
        || HttpMethods.IsGet(r.Method) && string.Equals(r.Query["handler"], "suggest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Startup check (Program.cs): logs every page model POST/PUT/DELETE handler that isn't in the access map.
    /// Page paths come from the namespace convention (PurpleDashboard.Pages.Employees.IndexModel → /Employees/Index).
    /// </summary>
    public static IReadOnlyList<string> FindUnmapped()
    {
        var missing = new List<string>();
        var models = typeof(AccessFilter).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(PageModel)) && t.Namespace?.StartsWith("PurpleDashboard.Pages", StringComparison.Ordinal) == true && t.Name.EndsWith("Model", StringComparison.Ordinal));
        foreach (var t in models)
        {
            var path = t.Namespace!["PurpleDashboard.Pages".Length..].Replace('.', '/') + "/" + t.Name[..^"Model".Length];
            if (AccessMap.PageRule(path) is null) missing.Add($"page {path}");
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var verb = new[] { "OnPost", "OnPut", "OnDelete", "OnPatch" }.FirstOrDefault(v => m.Name.StartsWith(v, StringComparison.Ordinal));
                if (verb is null) continue;
                var name = m.Name[verb.Length..];
                if (name.EndsWith("Async", StringComparison.Ordinal)) name = name[..^"Async".Length];
                if (AccessMap.HandlerRule(path, name) is null) missing.Add($"handler {path}#{name}");
            }
        }
        return missing;
    }
}
