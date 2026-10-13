using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Modules.Search;

namespace PurpleDashboard.Pages.Search;

/// <summary>
/// Global search. GET /search?q= is the full results page (Enter in the top bar, or no JavaScript);
/// GET /search?handler=suggest&amp;q= is the JSON behind the top bar's dropdown (wwwroot/js/search.js).
/// </summary>
public class IndexModel(SearchService search) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public SearchResults Results { get; private set; } = SearchResults.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = string.IsNullOrWhiteSpace(Q) ? "Search" : $"Search: {Q.Trim()}";
        ViewData["ModuleName"] = "Search";
        ViewData["SearchQuery"] = Q;
        // The page can wait a little longer than the dropdown for a source that's still loading.
        Results = await search.SearchAsync(Q, perGroup: 25, timeLimit: TimeSpan.FromSeconds(8), ct);
    }

    /// <summary>Dropdown results: up to 6 per group, and never waits more than about 2 seconds on a source.</summary>
    public async Task<IActionResult> OnGetSuggestAsync(CancellationToken ct)
    {
        var r = await search.SearchAsync(Q, perGroup: 6, timeLimit: TimeSpan.FromSeconds(2), ct);
        return new JsonResult(new
        {
            q = r.Query,
            groups = r.Groups.Select(g => new
            {
                key = g.Key, label = g.Label, total = g.Total, more = g.MoreUrl,
                hits = g.Hits.Select(h => new { title = h.Title, sub = h.Sub, url = h.Url, ext = h.ExternalUrl }),
            }),
            skipped = r.Skipped,
        });
    }
}
