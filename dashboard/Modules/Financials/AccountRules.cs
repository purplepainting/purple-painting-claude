namespace PurpleDashboard.Modules.Financials;

/// <summary>What a QuickBooks account counts as on the Financials pages.</summary>
public enum AccountKind { Income, FieldWages, LaborBurden, JobCost, Overhead, Ignore }

public sealed record AccountClass(AccountKind Kind, string Category);

/// <summary>
/// Sorts QuickBooks accounts by account number into income, field wages, labor burden, other job costs and
/// overhead categories. Defaults match Purple Painting's chart of accounts (Oct 2026); settings
/// (Financials:Accounts, "number prefix" → "Kind|Category") can override any prefix. Longest prefix wins.
/// </summary>
public sealed class AccountRulesOptions
{
    public Dictionary<string, string> Accounts { get; set; } = [];
    /// <summary>JobTread document ids left out of JobTread vs QuickBooks (Financials:SkipDocuments); empty = the defaults in FinancialsService.</summary>
    public List<string> SkipDocuments { get; set; } = [];
}

public sealed class AccountRules(Microsoft.Extensions.Options.IOptions<AccountRulesOptions> options)
{
    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["4"] = "Income|Income",
        // Field labor (COGS 5100, plus Painter Payroll also booked under Expenses).
        ["51"] = "FieldWages|Field payroll",
        ["5111"] = "FieldWages|Supervisor field payroll",
        ["5120"] = "FieldWages|Foreman payroll",
        ["5130"] = "FieldWages|Painter payroll",
        ["5140"] = "FieldWages|Apprentice payroll",
        ["5160"] = "FieldWages|Cleaning payroll",
        ["5190"] = "FieldWages|Overtime payroll",
        // Burden: what's paid on top of field wages.
        ["5180"] = "LaborBurden|Sick & holiday pay",
        ["5510"] = "LaborBurden|Workers' comp",
        // Other job costs.
        ["5000"] = "JobCost|Other job costs",
        ["52"] = "JobCost|Materials",
        ["53"] = "JobCost|Jobsite services",
        // Overhead.
        ["55"] = "Overhead|Insurance",
        ["61"] = "Overhead|Office & management payroll",
        ["6114"] = "Overhead|Bonuses",
        ["62"] = "Overhead|Office & facilities",
        ["621"] = "Overhead|Rent",
        ["622"] = "Overhead|Software & subscriptions",
        ["625"] = "Overhead|Team & HR",
        ["63"] = "Overhead|Vehicles & fuel",
        ["64"] = "Overhead|Marketing & sales",
        ["6490"] = "Overhead|Sales commissions",
        ["66"] = "Overhead|Professional services",
        ["67"] = "Overhead|Licenses & safety",
        ["68"] = "Overhead|Bank & merchant fees",
        ["69"] = "Overhead|Team & HR",
        ["8"] = "Overhead|Interest",
    };

    private Dictionary<string, string> Rules => options.Value.Accounts.Count == 0
        ? Defaults
        : Defaults.Concat(options.Value.Accounts).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    public AccountClass Classify(QbLine line)
    {
        var number = line.Number;
        if (number is not null)
        {
            foreach (var len in Enumerable.Range(1, number.Length).Reverse())
                if (Rules.TryGetValue(number[..len], out var rule) && rule.Split('|') is [var kind, var category] && Enum.TryParse<AccountKind>(kind, out var k))
                    return new AccountClass(k, category);
        }
        // No number: fall back on the report section.
        return line.Section switch
        {
            "Income" or "OtherIncome" => new(AccountKind.Income, "Income"),
            "COGS" => new(AccountKind.JobCost, "Other job costs"),
            _ => new(AccountKind.Overhead, "Other overhead"),
        };
    }
}
