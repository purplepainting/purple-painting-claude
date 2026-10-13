using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Modules.Vendors;

/// <summary>An unpaid QuickBooks bill.</summary>
public sealed record OpenBill(string Id, string Vendor, string? DocNumber, DateOnly? Date, DateOnly? Due, decimal Total, decimal Balance, string? Memo)
{
    public int? DaysUntilDue(DateOnly today) => Due is { } d ? d.DayNumber - today.DayNumber : null;
}

/// <summary>Bills owed to vendors, from QuickBooks (read only), cached 10 minutes.</summary>
public sealed class PayablesService(QuickBooksClient quickBooks, IMemoryCache cache)
{
    public Task<List<OpenBill>> OpenBillsAsync(bool refresh, CancellationToken ct)
    {
        const string key = "payables:open";
        if (refresh) cache.Remove(key);
        return cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var rows = await quickBooks.QueryAsync("Bill", "select * from Bill where Balance > '0'", ct);
            return rows.Select(ToBill).OrderBy(b => b.Due ?? DateOnly.MaxValue).ToList();
        })!;
    }

    /// <summary>Open bills if they're already cached, else null. Never calls QuickBooks (used by search).</summary>
    public List<OpenBill>? CachedOpenBills() => cache.TryGetValue("payables:open", out List<OpenBill>? hit) ? hit : null;

    private static OpenBill ToBill(JsonNode b) => new(
        b["Id"]?.GetValue<string>() ?? "",
        b["VendorRef"]?["name"]?.GetValue<string>() ?? "Unknown vendor",
        b["DocNumber"]?.GetValue<string>(),
        DateOf(b["TxnDate"]), DateOf(b["DueDate"]),
        Dec(b["TotalAmt"]), Dec(b["Balance"]),
        b["PrivateNote"]?.GetValue<string>());

    private static DateOnly? DateOf(JsonNode? n) => DateOnly.TryParse(n?.GetValue<string>(), CultureInfo.InvariantCulture, out var d) ? d : null;
    private static decimal Dec(JsonNode? n) => n is null ? 0 : n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.GetValue<decimal>() : decimal.TryParse(n.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>Vendors tile: owed on bills due in the next 14 days (including overdue).</summary>
public sealed class DueSoonMetric(PayablesService payables, QuickBooksClient quickBooks) : ITileMetric
{
    public string ModuleKey => "vendors";

    public async Task<string?> GetValueAsync(CancellationToken ct)
    {
        if (await quickBooks.GetConnectionAsync(ct) is null) return null;
        var today = LocalClock.Today;
        var due = (await payables.OpenBillsAsync(false, ct)).Where(b => b.DaysUntilDue(today) <= 14).Sum(b => b.Balance);
        return due >= 1000 ? "$" + Math.Round(due / 1000m).ToString("#,0") + "k" : "$" + Math.Round(due).ToString("#,0");
    }
}
