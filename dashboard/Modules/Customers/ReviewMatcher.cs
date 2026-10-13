using System.Globalization;
using System.Text;
using PurpleDashboard.Integrations.Google;

namespace PurpleDashboard.Modules.Customers;

/// <summary>A Google review matched to a customer. How = "exact" (same name) or "close" (same words in another order, middle name left out, or one typo).</summary>
public sealed record ReviewMatch(GoogleReview Review, CustomerView Customer, string How);

/// <summary>A Google review that couldn't be matched to exactly one customer, and why.</summary>
public sealed record UnmatchedReview(GoogleReview Review, string Why);

/// <summary>
/// Matches Google reviewers to customers by name. Names are normalized (case, accents, punctuation, words like
/// "the", "family", "Mr.", "LLC"); couples ("John &amp; Mary Smith") match either person. Only exact or close matches
/// count, and only when exactly one customer fits; anything else stays unmatched so a person can check it.
/// </summary>
public static class ReviewMatcher
{
    private static readonly HashSet<string> Stopwords = ["the", "family", "residence", "household", "mr", "mrs", "ms", "miss", "dr", "jr", "sr", "ii", "iii", "llc", "inc", "co"];

    public static (List<ReviewMatch> Matches, List<UnmatchedReview> Unmatched) Match(IEnumerable<GoogleReview> reviews, IEnumerable<CustomerView> customers)
    {
        var people = customers.Select(c => (Customer: c, Variants: Variants(c.Name))).Where(x => x.Variants.Count > 0).ToList();
        var matches = new List<ReviewMatch>();
        var unmatched = new List<UnmatchedReview>();

        foreach (var review in reviews)
        {
            var tokens = Tokens(review.Reviewer);
            if (review.Anonymous || tokens.Count == 0 || string.Join(" ", tokens) == "a google user")
            {
                unmatched.Add(new(review, "Anonymous reviewer"));
                continue;
            }
            var full = string.Join(" ", tokens);
            if (tokens.Count < 2) { unmatched.Add(new(review, "Only one name on Google")); continue; }

            var exact = people.Where(p => p.Variants.Any(v => string.Join(" ", v) == full)).Select(p => p.Customer).Distinct().ToList();
            if (exact.Count == 1) { matches.Add(new(review, exact[0], "exact")); continue; }
            if (exact.Count > 1) { unmatched.Add(new(review, $"Same name as {exact.Count} customers")); continue; }

            var close = people.Where(p => p.Variants.Any(v => IsClose(tokens, v))).Select(p => p.Customer).Distinct().ToList();
            if (close.Count == 1) matches.Add(new(review, close[0], "close"));
            else unmatched.Add(new(review, close.Count > 1 ? $"Close to {close.Count} customers" : "No customer with that name"));
        }
        return (matches, unmatched);
    }

    /// <summary>Same words in any order, same first and last name (middle names left out), or one letter off on a full name of 8+ letters.</summary>
    private static bool IsClose(List<string> a, List<string> b)
    {
        if (b.Count < 2) return false;
        if (a.OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(b.OrderBy(t => t, StringComparer.Ordinal))) return true;
        if (a[0] == b[0] && a[^1] == b[^1]) return true;
        string fa = string.Join(" ", a), fb = string.Join(" ", b);
        return fa.Length >= 8 && fb.Length >= 8 && Levenshtein(fa, fb) <= 1;
    }

    /// <summary>
    /// The names a customer could review under: the whole name, and for couples each person with the shared last name
    /// ("john and mary smith" → "john smith", "mary smith").
    /// </summary>
    public static List<List<string>> Variants(string name)
    {
        var tokens = Tokens(name);
        if (tokens.Count == 0) return [];
        var result = new List<List<string>> { tokens.Where(t => t != "and").ToList() };
        if (tokens.Contains("and"))
        {
            var groups = new List<List<string>> { new() };
            foreach (var t in tokens) { if (t == "and") groups.Add([]); else groups[^1].Add(t); }
            groups = groups.Where(g => g.Count > 0).ToList();
            var last = groups[^1][^1];
            foreach (var g in groups) result.Add(g.Count == 1 ? [g[0], last] : g);
        }
        return result.Where(v => v.Count > 0).ToList();
    }

    /// <summary>Lowercase words without accents, punctuation or filler words; "&amp;" and "+" become "and".</summary>
    public static List<string> Tokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var plain = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(ch is '&' or '+' or '/' ? " and " : char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ch == '\'' ? "" : " ");
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Stopwords.Contains(t)).ToList();
    }

    private static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }
}
