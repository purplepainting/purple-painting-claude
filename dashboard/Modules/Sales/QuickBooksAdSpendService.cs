using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.QuickBooks;
using PurpleDashboard.Modules.Financials;

namespace PurpleDashboard.Modules.Sales;

/// <summary>One posting to an ad account in QuickBooks (General Ledger line), with the channel it maps to.</summary>
public sealed record BookedLine(DateOnly Date, string Account, string Channel, string? Vendor, string? Memo, decimal Amount)
{
    public string Month => Date.ToString("yyyy-MM");
}

public enum BookedAdSpendState { NotSetUp, NotConnected, Ready, Error }

/// <summary>
/// Ad spend booked in QuickBooks for the last 12 months (this month and the 11 before). Lines are General Ledger postings
/// to the ad accounts (<see cref="AdAccounts"/>); Accounts = the QuickBooks accounts found for those numbers.
/// </summary>
public sealed record BookedAdSpend(BookedAdSpendState State, string FromMonth, string ToMonth, IReadOnlyList<BookedLine> Lines, IReadOnlyList<string> Accounts, string? Problem = null)
{
    public bool Ready => State == BookedAdSpendState.Ready;

    /// <summary>Totals per channel and month (only channel-months with bookings).</summary>
    public List<SpendLine> Months => Lines.GroupBy(l => (l.Month, l.Channel))
        .Select(g => new SpendLine(g.Key.Month, g.Key.Channel, Math.Round(g.Sum(l => l.Amount), 2), null, QuickBooksAdSpendService.SourceName))
        .Where(s => s.Amount != 0).OrderBy(s => s.Month).ThenBy(s => s.Channel).ToList();
}

/// <summary>
/// Ad spend as booked in QuickBooks under 6400 Marketing &amp; Sales, by channel and month (read only). One account lookup
/// and one General Ledger report for the ad accounts over the last 12 months, both kept until 5am by QuickBooksClient.
/// Account → channel and vendor → channel mapping lives here only (<see cref="ChannelOf"/>).
/// Used by the Ads tab as a spend source below the ad platform APIs and above hand-entered rows
/// (<see cref="SalesService.MergeApiSpend"/>), and for the Platform vs QuickBooks reconcile.
/// </summary>
public sealed class QuickBooksAdSpendService(QuickBooksClient quickBooks, IConfiguration config, ILogger<QuickBooksAdSpendService> logger)
{
    public const string SourceName = "QuickBooks";

    /// <summary>Default ad accounts (Purple Painting chart of accounts, Oct 2026). Setting Sales:AdAccounts (comma separated) replaces the list.</summary>
    public static readonly string[] DefaultAdAccounts = ["6410", "6420", "6430", "6440"];

    public const string GoogleAccount = "6420";
    public const string OtherAdvertisingAccount = "6410";
    public const string PrintingAccount = "6430";
    public const string SeoAccount = "6440";

    public const string OtherAdvertisingChannel = "Other advertising";
    public const string SeoChannel = "Website SEO";
    public const string PrintingChannel = "Printing";

    /// <summary>
    /// Vendor (payee or memo) → channel for postings to 6410 Other Advertising Lead Gen (and any configured account not
    /// listed in <see cref="ChannelOf"/>). First match wins; null channel = the Google Ads channel. Anything else is "Other advertising".
    /// </summary>
    private static readonly (Regex Pattern, string? Channel)[] VendorRules =
    [
        (VendorRegex(@"\b(facebook|facebk|fb\s*\*?\s*ads?|meta|instagram)\b"), MetaAdsService.Channel),
        (VendorRegex(@"\bnext\s*door\b"), SalesService.NextdoorChannel),
        (VendorRegex(@"\b(local\s*services|lsa)\b"), "Google LSA"),
        (VendorRegex(@"\b(google|adwords)\b"), null),
        (VendorRegex(@"\byelp\b"), "Yelp"),
        (VendorRegex(@"\b(angi|angie'?s(\s*list)?|homeadvisor|home\s*advisor)\b"), "Angi"),
        (VendorRegex(@"\bthumbtack\b"), "Thumbtack"),
        (VendorRegex(@"\bhouzz\b"), "Houzz"),
        (VendorRegex(@"\bporch\b"), "Porch"),
        (VendorRegex(@"\bbark(\.com)?\b"), "Bark"),
        (VendorRegex(@"\bbbb|better\s*business\b"), "BBB"),
    ];

    private static Regex VendorRegex(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The one place a QuickBooks ad posting is turned into a spend channel: 6420 → the Google Ads channel (Google:AdsChannel);
    /// 6440 → "Website SEO"; 6430 → "Printing"; 6410 (and other configured accounts) by vendor: Facebook/Meta/Instagram →
    /// "Facebook/Instagram", Nextdoor → "Nextdoor", Yelp/Angi/Thumbtack/… → that name, else "Other advertising".
    /// </summary>
    public static string ChannelOf(string account, string? vendor, string? memo, string googleChannel)
    {
        switch (account)
        {
            case GoogleAccount: return googleChannel;
            case SeoAccount: return SeoChannel;
            case PrintingAccount: return PrintingChannel;
        }
        foreach (var text in new[] { vendor, memo })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var (pattern, channel) in VendorRules)
                if (pattern.IsMatch(text)) return channel ?? googleChannel;
        }
        return OtherAdvertisingChannel;
    }

    /// <summary>Account numbers to read: Sales:AdAccounts ("6410,6420,6430,6440"), or the defaults.</summary>
    public IReadOnlyList<string> AdAccounts
    {
        get
        {
            var section = config.GetSection("Sales:AdAccounts");
            var list = section.Value is { Length: > 0 } v
                ? v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : section.GetChildren().Select(c => c.Value?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).ToArray();
            return list.Length > 0 ? list : DefaultAdAccounts;
        }
    }

    /// <summary>Default channels that are marketing but not ad spend. Setting Sales:NonAdChannels (comma separated) replaces the list.</summary>
    public static readonly string[] DefaultNonAdChannels = [SeoChannel, PrintingChannel];

    /// <summary>
    /// Channels left out of the Ad spend total and the lead-source table (shown in the reconcile as "not ad spend"):
    /// Sales:NonAdChannels ("Website SEO,Printing" by default; set it to "-" to count everything). Case-insensitive.
    /// </summary>
    public IReadOnlySet<string> NonAdChannels
    {
        get
        {
            var section = config.GetSection("Sales:NonAdChannels");
            string[] list = section.Value is { } v
                ? v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => c != "-").ToArray()
                : section.GetChildren().Any() ? section.GetChildren().Select(c => c.Value?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).ToArray()
                : DefaultNonAdChannels;
            return list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<BookedAdSpend> GetAsync(string googleChannel, CancellationToken ct)
    {
        var today = LocalClock.Today;
        var since = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        string fromMonth = since.ToString("yyyy-MM"), toMonth = today.ToString("yyyy-MM");
        BookedAdSpend Empty(BookedAdSpendState s, string? problem = null) => new(s, fromMonth, toMonth, [], [], problem);

        if (!quickBooks.IsConfigured) return Empty(BookedAdSpendState.NotSetUp, "QuickBooks isn't set up (QBO_CLIENT_ID / QBO_CLIENT_SECRET).");
        try
        {
            if (await quickBooks.GetConnectionAsync(ct) is null) return Empty(BookedAdSpendState.NotConnected, "QuickBooks isn't connected. Connect it on the Connections page to use booked ad spend.");

            // Account ids for the configured numbers (and their sub-accounts): the report filters by id.
            var wanted = AdAccounts;
            var accounts = (await quickBooks.QueryAsync("Account", "select * from Account", ct)).OfType<JsonObject>()
                .Select(a => (Id: Str(a, "Id"), Name: Str(a, "Name") ?? "", Number: Str(a, "AcctNum") is { Length: > 0 } n ? n : QbReport.NumberOf(Str(a, "Name") ?? ""), Parent: Str(a["ParentRef"] as JsonObject, "value")))
                .Where(a => a.Id is not null).ToList();
            var matched = accounts.Where(a => a.Number is not null && wanted.Any(w => a.Number.StartsWith(w, StringComparison.Ordinal)))
                .ToDictionary(a => a.Id!, a => wanted.Where(w => a.Number!.StartsWith(w, StringComparison.Ordinal)).OrderByDescending(w => w.Length).First());
            for (var added = true; added;)
            {
                added = false;
                foreach (var a in accounts.Where(a => !matched.ContainsKey(a.Id!) && a.Parent is not null && matched.ContainsKey(a.Parent)))
                {
                    matched[a.Id!] = matched[a.Parent!];
                    added = true;
                }
            }
            if (matched.Count == 0)
                return Empty(BookedAdSpendState.Ready, $"No QuickBooks accounts numbered {string.Join(", ", wanted)} were found (setting Sales:AdAccounts).");
            var names = accounts.Where(a => matched.ContainsKey(a.Id!)).Select(a => a.Number is not null && !a.Name.StartsWith(a.Number) ? $"{a.Number} {a.Name}" : a.Name).OrderBy(n => n).ToList();
            var numberByName = accounts.Where(a => matched.ContainsKey(a.Id!)).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => matched[g.First().Id!], StringComparer.OrdinalIgnoreCase);

            var report = await quickBooks.ReportAsync("GeneralLedger", new()
            {
                ["start_date"] = since.ToString("yyyy-MM-dd"),
                ["end_date"] = today.ToString("yyyy-MM-dd"),
                ["account"] = string.Join(",", matched.Keys.OrderBy(k => k)),
                ["accounting_method"] = "Accrual",
                ["columns"] = "tx_date,txn_type,doc_num,name,vend_name,memo,account_name,subt_nat_amount",
            }, ct);
            var lines = Parse(report, wanted, numberByName, googleChannel)
                .Where(l => string.CompareOrdinal(l.Month, fromMonth) >= 0 && string.CompareOrdinal(l.Month, toMonth) <= 0).ToList();
            return new BookedAdSpend(BookedAdSpendState.Ready, fromMonth, toMonth, lines, names);
        }
        catch (Exception e) when (e is QuickBooksException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Loading QuickBooks ad spend failed");
            return Empty(BookedAdSpendState.Error, e is QuickBooksException ? e.Message : "Couldn't reach QuickBooks. Try again in a minute.");
        }
    }

    /// <summary>
    /// Postings from a GeneralLedger (or TransactionList) report. Columns are found by their key (tx_date, name, vend_name,
    /// memo, account_name, subt_nat_amount). The account is the row's account_name when given, else the innermost section
    /// header ("6420 Google Advertising Lead Gen"); rows not in a wanted account, and Beginning Balance / total rows, are skipped.
    /// </summary>
    public static List<BookedLine> Parse(JsonNode report, IReadOnlyList<string> wanted, IReadOnlyDictionary<string, string>? numberByName, string googleChannel)
    {
        var columns = (report["Columns"]?["Column"] as JsonArray ?? []).Select(c =>
        {
            var key = (c?["MetaData"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(m => Str(m, "Name") == "ColKey") is { } meta ? Str(meta, "Value") : null;
            return key ?? (Str(c as JsonObject, "ColTitle") ?? "").ToLowerInvariant() switch
            {
                "date" => "tx_date",
                "name" => "name",
                "vendor" => "vend_name",
                "memo/description" or "memo" => "memo",
                "account" => "account_name",
                "amount" => "subt_nat_amount",
                var t => t,
            };
        }).ToList();
        int Col(string key) => columns.IndexOf(key);
        int date = Col("tx_date"), name = Col("name"), vendor = Col("vend_name"), memo = Col("memo"), account = Col("account_name"), amount = Col("subt_nat_amount");
        if (date < 0 || amount < 0) return [];

        string? AccountOf(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var number = QbReport.NumberOf(text);
            if (number is null && numberByName is not null)
            {
                var leaf = text.Split(':').Last().Trim();
                if (numberByName.TryGetValue(leaf, out var n) || numberByName.TryGetValue(text.Trim(), out n)) return n;
            }
            return number is null ? null : wanted.Where(w => number.StartsWith(w, StringComparison.Ordinal)).OrderByDescending(w => w.Length).FirstOrDefault();
        }

        var lines = new List<BookedLine>();
        void Walk(JsonArray? rows, string? section)
        {
            foreach (var row in rows ?? [])
            {
                if (row is null) continue;
                if (row["Rows"]?["Row"] is JsonArray children)
                {
                    var header = Str(row["Header"]?["ColData"]?[0] as JsonObject, "value");
                    Walk(children, AccountOf(header) ?? section);
                    continue;
                }
                if (row["ColData"] is not JsonArray cols) continue;
                string? Cell(int i) => i >= 0 && i < cols.Count ? Str(cols[i] as JsonObject, "value") is { Length: > 0 } v ? v.Trim() : null : null;
                if (!DateOnly.TryParseExact(Cell(date), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                var acct = (account >= 0 ? AccountOf(Cell(account)) : null) ?? section;
                if (acct is null) continue;
                if (!decimal.TryParse(Cell(amount), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) || amt == 0) continue;
                var who = Cell(vendor) ?? Cell(name);
                lines.Add(new BookedLine(d, acct, ChannelOf(acct, who, Cell(memo), googleChannel), who, Cell(memo), amt));
            }
        }
        Walk(report["Rows"]?["Row"] as JsonArray, null);
        return lines;
    }

    /// <summary>
    /// QuickBooks as spend sources for <see cref="SalesService.MergeApiSpend"/>, one per channel, listed after the API
    /// sources: each covers only the months with bookings for its channel (a month with nothing booked leaves hand-entered
    /// rows counted, i.e. spend not in QuickBooks).
    /// </summary>
    public static List<ApiSpendSource> SpendSources(BookedAdSpend booked, string googleChannel)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        return booked.Months.GroupBy(m => m.Channel, cmp).Select(g =>
        {
            var channel = g.Key;
            var months = g.Select(m => m.Month).ToHashSet();
            return new ApiSpendSource(SourceName, channel, c => cmp.Equals(SalesService.ChannelFor(c, googleChannel), channel), months.Contains, g.ToList());
        }).ToList();
    }

    private static string? Str(JsonObject? o, string key) => o?[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
}
