using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Modules.Financials;

/// <summary>One paycheck line from QuickBooks: the gross amount a pay item posted to an expense account.</summary>
public sealed record PayrollLine(DateOnly Date, string Type, string? Num, string Name, string? EmployeeId, string? Memo, string Account, decimal Amount);

/// <summary>Paycheck lines for a period, or why they couldn't be read.</summary>
public sealed record PayrollView(string? Problem, IReadOnlyList<string> Accounts, bool ByMemo, IReadOnlyList<PayrollLine> Lines)
{
    public bool Ok => Problem is null;
}

/// <summary>
/// Paychecks as they land in the books, for checking dashboard-tracked pay (bonuses, commissions) against payroll.
/// The QuickBooks connection is the Accounting API only (no payroll API), but processed paychecks post to the
/// books: each pay item's wage line lands on its expense account (Bonus → 6114 Bonus Payroll, Commission → 6490
/// Sales Commissions Overhead). <see cref="GetAsync"/> reads the General Ledger for the period filtered to the given
/// accounts (numbers or names) and returns each line with the employee (Name column, with the QuickBooks employee id
/// when the report gives one), check number and gross amount. Cached daily by QuickBooksClient (Refresh on the page
/// clears it). Settings: Financials:BonusAccounts (default 6114), Sales:CommissionAccounts (default 6490).
/// </summary>
public sealed class PayrollPayments(QuickBooksClient quickBooks, IConfiguration config, ILogger<PayrollPayments> logger)
{
    public const string BonusSetting = "Financials:BonusAccounts", BonusDefault = "6114";
    public const string CommissionSetting = "Sales:CommissionAccounts", CommissionDefault = "6490";
    private static readonly string[] Columns = ["tx_date", "txn_type", "doc_num", "name", "memo", "subt_nat_amount"];

    /// <summary>Account numbers or names from a setting (comma separated), or the default.</summary>
    public IReadOnlyList<string> AccountsFrom(string setting, string fallback) =>
        (string.IsNullOrWhiteSpace(config[setting]) ? fallback : config[setting]!).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Paycheck lines posted to <paramref name="accounts"/> from <paramref name="from"/> to <paramref name="to"/>.
    /// When none of the accounts exist or they have no lines, and <paramref name="memoFallback"/> is set, Paycheck
    /// lines whose memo/description contains that word are used instead (ByMemo).
    /// </summary>
    public async Task<PayrollView> GetAsync(IReadOnlyList<string> accounts, DateOnly from, DateOnly to, string? memoFallback, string setting, CancellationToken ct)
    {
        try
        {
            if (await quickBooks.GetConnectionAsync(ct) is null)
                return new("QuickBooks isn't connected, so paychecks can't be checked. Connect it on Connections.", [], false, []);

            var chart = await quickBooks.QueryAsync("Account", "select Id, Name, AcctNum, FullyQualifiedName, Classification, AccountType, Active from Account where Active in (true, false)", ct);
            var matched = chart.Where(a => accounts.Any(t => Matches(a, t))).ToList();
            var names = matched.Select(a => Str(a, "Name")).OfType<string>().ToList();
            var parameters = new Dictionary<string, string>
            {
                ["start_date"] = from.ToString("yyyy-MM-dd"),
                ["end_date"] = to.ToString("yyyy-MM-dd"),
                ["columns"] = string.Join(",", Columns),
            };

            var lines = new List<PayrollLine>();
            if (matched.Count > 0)
            {
                parameters["account"] = string.Join(",", matched.Select(a => Str(a, "Id")));
                lines = Parse(await quickBooks.ReportAsync("GeneralLedger", parameters, ct), from, to)
                    .Where(l => names.Any(n => l.Account.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            if (lines.Count == 0 && memoFallback is not null)
            {
                parameters.Remove("account");
                parameters["source_account_type"] = "Expense";
                var byMemo = Parse(await quickBooks.ReportAsync("GeneralLedger", parameters, ct), from, to)
                    .Where(l => IsPaycheck(l.Type) && (l.Memo ?? "").Contains(memoFallback, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byMemo.Count > 0) return new(null, [$"Paychecks with \"{memoFallback}\" in the memo"], true, Sort(byMemo));
            }
            if (matched.Count == 0)
                return new($"No QuickBooks account matches {string.Join(", ", accounts)} (setting {setting}, account numbers or names, comma separated).", [], false, []);
            return new(null, matched.Select(a => Str(a, "FullyQualifiedName") ?? Str(a, "Name") ?? "").ToList(), false, Sort(lines));
        }
        catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(e, "Reading paychecks from QuickBooks failed");
            return new($"Couldn't read QuickBooks: {e.Message}", [], false, []);
        }
    }

    private static List<PayrollLine> Sort(IEnumerable<PayrollLine> lines) => lines.OrderBy(l => l.Date).ThenBy(l => l.Num, StringComparer.Ordinal).ThenBy(l => l.Name).ToList();

    private static bool IsPaycheck(string type) => type.Contains("Paycheck", StringComparison.OrdinalIgnoreCase) || type.Contains("Payroll", StringComparison.OrdinalIgnoreCase);

    /// <summary>A number matches AcctNum or a name starting with it ("6114 Bonus Payroll"); text matches the name or full name.</summary>
    private static bool Matches(JsonNode a, string token)
    {
        var name = Str(a, "Name") ?? "";
        var full = Str(a, "FullyQualifiedName") ?? "";
        if (token.All(char.IsDigit)) return Str(a, "AcctNum") == token || name.StartsWith(token + " ", StringComparison.Ordinal);
        return name.Equals(token, StringComparison.OrdinalIgnoreCase) || full.Equals(token, StringComparison.OrdinalIgnoreCase)
            || full.EndsWith(":" + token, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v ? v.ToString() : null;

    /// <summary>Data rows of a GeneralLedger report in [from, to], each with the account section it sits under.</summary>
    public static List<PayrollLine> Parse(JsonNode report, DateOnly from, DateOnly to)
    {
        var cols = (report["Columns"]?["Column"] as JsonArray ?? []).Select((c, i) =>
        {
            var key = (c?["MetaData"] as JsonArray ?? []).FirstOrDefault(m => m?["Name"]?.ToString() == "ColKey")?["Value"]?.ToString();
            key ??= (c?["ColTitle"]?.ToString() ?? "") switch
            {
                "Date" => "tx_date", "Transaction Type" => "txn_type", "Num" => "doc_num", "Name" => "name",
                "Memo/Description" => "memo", "Amount" => "subt_nat_amount", var t => t,
            };
            return (key, i);
        }).GroupBy(x => x.key).ToDictionary(g => g.Key, g => g.First().i);
        var result = new List<PayrollLine>();
        void Walk(JsonArray? rows, string account)
        {
            foreach (var row in rows ?? [])
            {
                if (row is null) continue;
                if (row["Rows"]?["Row"] is JsonArray children)
                {
                    var title = row["Header"]?["ColData"]?[0]?["value"]?.ToString();
                    Walk(children, string.IsNullOrEmpty(title) ? account : title);
                    continue;
                }
                if (row["ColData"] is not JsonArray data) continue;
                JsonNode? Col(string key) => cols.TryGetValue(key, out var i) && i < data.Count ? data[i] : null;
                if (!DateOnly.TryParse(Col("tx_date")?["value"]?.ToString(), CultureInfo.InvariantCulture, out var date) || date < from || date > to) continue;
                if (!decimal.TryParse(Col("subt_nat_amount")?["value"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount == 0) continue;
                var name = Col("name")?["value"]?.ToString();
                result.Add(new PayrollLine(date, Col("txn_type")?["value"]?.ToString() ?? "", Empty(Col("doc_num")?["value"]?.ToString()),
                    string.IsNullOrWhiteSpace(name) ? "(no name)" : name, Empty(Col("name")?["id"]?.ToString()), Empty(Col("memo")?["value"]?.ToString()), account, amount));
            }
        }
        Walk(report["Rows"]?["Row"] as JsonArray, "");
        return result;
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Maps a QuickBooks employee to a dashboard (JobTread) name: by QuickBooks employee id (qboEmployeeId on the
    /// JobTread membership), then by normalized name ("Last, First" turned around, accents and punctuation dropped),
    /// then by first + last word when only one person fits. Null when nobody fits.
    /// </summary>
    public static string? MatchPerson(PayrollLine line, IReadOnlyList<StaffMember> staff, IEnumerable<string> names)
    {
        if (line.EmployeeId is { } id && staff.FirstOrDefault(s => s.QboEmployeeId == id) is { } byId) return byId.Name;
        var all = staff.Select(s => s.Name).Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var key = Normalize(line.Name);
        if (all.FirstOrDefault(n => Normalize(n) == key) is { } exact) return exact;
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;
        var loose = all.Where(n => Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } w && w[0] == words[0] && w[^1] == words[^1]).ToList();
        return loose.Count == 1 ? loose[0] : null;
    }

    public static string Normalize(string name)
    {
        var n = name.Trim();
        if (n.Contains(',')) { var parts = n.Split(',', 2); n = parts[1].Trim() + " " + parts[0].Trim(); }
        var sb = new StringBuilder();
        foreach (var ch in n.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetter(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
