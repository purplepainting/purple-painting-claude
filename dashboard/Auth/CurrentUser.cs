using System.Text;
using System.Text.Json;

namespace PurpleDashboard.Auth;

/// <summary>
/// The signed-in user, read from the headers App Service Authentication (Easy Auth)
/// adds to every request. Easy Auth strips these headers from incoming requests and
/// sets them itself, so they can be trusted inside the app.
/// </summary>
public sealed class CurrentUser
{
    private const string NameClaim = "name";
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>Development only: cookie holding the email to act as (set with ?devuser=email, cleared with ?devuser=).</summary>
    public const string DevUserCookie = "devuser";

    public CurrentUser(IHttpContextAccessor accessor, IWebHostEnvironment env, IConfiguration config)
    {
        var headers = accessor.HttpContext?.Request.Headers;
        var claims = ReadClaims(headers?["X-MS-CLIENT-PRINCIPAL"].ToString());

        Email = NullIfEmpty(headers?["X-MS-CLIENT-PRINCIPAL-NAME"].ToString());
        ObjectId = NullIfEmpty(headers?["X-MS-CLIENT-PRINCIPAL-ID"].ToString()) ?? claims.GetValueOrDefault(ObjectIdClaim);
        DisplayName = claims.GetValueOrDefault(NameClaim) ?? Email;

        // Running locally there is no Easy Auth in front of the app. To test roles, act as someone else with
        // the Dev:UserEmail setting or ?devuser=someone@purplepainting.net (a cookie). Never in production.
        if (DisplayName is null && env.IsDevelopment())
        {
            var devEmail = NullIfEmpty(accessor.HttpContext?.Request.Cookies[DevUserCookie]) ?? NullIfEmpty(config["Dev:UserEmail"]);
            if (devEmail is null)
            {
                DisplayName = "Local Developer";
                Email = "dev@localhost";
                ObjectId = "00000000-0000-0000-0000-000000000000";
            }
            else
            {
                Email = devEmail.Trim().ToLowerInvariant();
                DisplayName = string.Join(' ', Email.Split('@')[0].Split('.', '_', '-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
                ObjectId = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(Email))).ToString();
            }
        }
    }

    /// <summary>Entra ID object id. Stable per person; use it as the key for per-user data.</summary>
    public string? ObjectId { get; }
    public string? Email { get; }
    public string? DisplayName { get; }
    public bool IsSignedIn => ObjectId is not null;

    public string FirstName => DisplayName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";

    private static Dictionary<string, string> ReadClaims(string? encodedPrincipal)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(encodedPrincipal)) return result;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encodedPrincipal)));
            if (!doc.RootElement.TryGetProperty("claims", out var claims)) return result;
            foreach (var claim in claims.EnumerateArray())
            {
                var typ = claim.GetProperty("typ").GetString();
                var val = claim.GetProperty("val").GetString();
                if (typ is not null && val is not null) result.TryAdd(typ, val);
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Malformed header: treat as no claims.
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
