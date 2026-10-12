using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PurpleDashboard.Modules.Financials;

/// <summary>One account line from a QuickBooks report, with an amount per column.</summary>
/// <param name="Number">Account number from the name ("5120 Foreman Payroll" → "5120"), or the parent's number for amounts posted to a parent.</param>
/// <param name="Section">The report's top-level group: Income, COGS, Expenses, OtherIncome, OtherExpenses.</param>
/// <param name="Path">Parent accounts, top first (e.g. "6000 OPERATING EXPENSES", "6200 Office & Facilities").</param>
public sealed record QbLine(string? Number, string Name, string Section, IReadOnlyList<string> Path, decimal[] Amounts)
{
    public decimal Total => Amounts.Length == 0 ? 0 : Amounts[^1];
}

/// <summary>
/// A QuickBooks report (ProfitAndLoss etc.) flattened to account lines. Only leaf "Data" rows are kept,
/// so nothing is counted twice; money posted straight to a parent account shows up as a Data row too.
/// With summarize_column_by=Month, Columns has one per month and the last is the total.
/// </summary>
public sealed partial class QbReport
{
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<QbLine> Lines { get; }

    private QbReport(IReadOnlyList<string> columns, IReadOnlyList<QbLine> lines) { Columns = columns; Lines = lines; }

    public static QbReport Parse(JsonNode report)
    {
        var columns = (report["Columns"]?["Column"] as JsonArray ?? []).Skip(1)
            .Select(c => c?["ColTitle"]?.GetValue<string>() ?? "").ToList();
        var lines = new List<QbLine>();
        Walk(report["Rows"]?["Row"] as JsonArray, null, [], lines, columns.Count);
        return new QbReport(columns, lines);
    }

    private static void Walk(JsonArray? rows, string? section, List<string> path, List<QbLine> lines, int columnCount)
    {
        foreach (var row in rows ?? [])
        {
            if (row is null) continue;
            var group = row["group"]?.GetValue<string>();
            if (row["Rows"]?["Row"] is JsonArray children)
            {
                var title = row["Header"]?["ColData"]?[0]?["value"]?.GetValue<string>() ?? "";
                var sub = section is null ? path : [.. path, title];
                Walk(children, section ?? group ?? title, sub, lines, columnCount);
            }
            else if (row["ColData"] is JsonArray cols && cols.Count > 1)
            {
                var name = cols[0]?["value"]?.GetValue<string>() ?? "";
                var amounts = cols.Skip(1).Select(c => decimal.TryParse(c?["value"]?.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m).ToArray();
                if (amounts.Length < columnCount) amounts = [.. amounts, .. new decimal[columnCount - amounts.Length]];
                var number = NumberOf(name) ?? path.Select(NumberOf).LastOrDefault(n => n is not null);
                lines.Add(new QbLine(number, name, section ?? "", path, amounts));
            }
        }
    }

    public static string? NumberOf(string name) => AccountNumber().Match(name) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^\s*(\d{4})\b")]
    private static partial Regex AccountNumber();
}
