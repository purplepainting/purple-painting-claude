using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PurpleDashboard.Pages;

[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : PageModel
{
    public string Heading { get; private set; } = "Something went wrong";
    public string Message { get; private set; } = "Try again in a minute. If it keeps happening, let Kareem know.";

    public void OnGet(int? code) => Set(code);
    public void OnPost(int? code) => Set(code);

    private void Set(int? code)
    {
        ViewData["Title"] = "Error";
        if (code == 404)
        {
            Heading = "Page not found";
            Message = "That page doesn't exist. It may have moved.";
        }
        if (code is not null) Response.StatusCode = code.Value;
    }
}
