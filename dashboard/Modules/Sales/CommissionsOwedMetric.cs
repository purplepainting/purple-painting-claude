namespace PurpleDashboard.Modules.Sales;

/// <summary>Sales tile: commissions earned but not paid yet.</summary>
public sealed class CommissionsOwedMetric(SalesService sales) : ITileMetric
{
    public string ModuleKey => "sales";

    public async Task<string?> GetValueAsync(CancellationToken ct)
    {
        var owed = (await sales.CommissionsAsync(false, ct)).Sum(c => c.Owed);
        return owed >= 1000 ? "$" + Math.Round(owed / 1000m).ToString("#,0") + "k" : "$" + Math.Round(owed).ToString("#,0");
    }
}
