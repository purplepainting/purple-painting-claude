using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.QuickBooks;

namespace PurpleDashboard.Modules.Financials;

/// <summary>Financials tile: QuickBooks net profit year to date (blank until QuickBooks is connected).</summary>
public sealed class NetProfitMetric(FinancialsService financials, QuickBooksClient quickBooks) : ITileMetric
{
    public string ModuleKey => "financials";

    public async Task<string?> GetValueAsync(CancellationToken ct)
    {
        if (await quickBooks.GetConnectionAsync(ct) is null) return null;
        var today = LocalClock.Today;
        var report = await financials.GetAsync(new DateOnly(today.Year, 1, 1), today, "Accrual", false, ct);
        var net = report.Total.QbNet;
        return (net < 0 ? "-" : "") + "$" + (Math.Abs(net) >= 1000 ? Math.Round(Math.Abs(net) / 1000m).ToString("#,0") + "k" : Math.Round(Math.Abs(net)).ToString("#,0"));
    }
}
