using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Sales;

namespace PurpleDashboard.Modules.Changes;

public sealed record DetailFact(string Label, string Value);
public sealed record DetailDiff(string Label, string Before, string After)
{
    public bool Changed => Before != After;
}

/// <summary>A change log entry ready to show: a one-line summary plus readable details.</summary>
public sealed record ChangeView(
    ChangeLogEntry Entry,
    string Summary,
    string? Note,
    string ResultLabel,
    string ResultClass,
    IReadOnlyList<DetailDiff> Diffs,
    IReadOnlyList<DetailFact> Facts,
    string? Link)
{
    public string AreaLabel => ChangeFormatter.AreaLabel(Entry.Area);
}

/// <summary>
/// Plain-English summaries of change log entries, one formatter per area. Each reads the Details JSON its
/// writer stores (see the ChangeLogWriter.Entry calls); anything unknown falls back to "action target"
/// and a flat key/value list, so a new area shows up readably before it gets its own formatter.
/// </summary>
public sealed class ChangeFormatter(IReadOnlyDictionary<string, string> crewNames, IReadOnlyDictionary<int, string> itemNames)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string AreaLabel(string area) => area switch
    {
        "schedule" => "Schedule",
        "employees" => "Employees",
        "bonuses" => "Bonuses",
        "commissions" => "Commissions",
        "sundries" => "Sundries",
        "equipment" => "Equipment",
        "reviews" => "Reviews",
        "connections" => "Connections",
        _ => Humanize(area),
    };

    /// <summary>Page where the area's data lives.</summary>
    public static string? AreaLink(string area) => area switch
    {
        "schedule" => "/scheduling",
        "employees" => "/employees",
        "bonuses" => "/financials/bonuses",
        "commissions" => "/sales",
        "sundries" => "/equipment",
        "equipment" => "/equipment?tab=equipment",
        "reviews" => "/customers",
        "connections" => "/connections",
        _ => null,
    };

    public ChangeView Format(ChangeLogEntry e)
    {
        JsonObject? d = null;
        if (!string.IsNullOrEmpty(e.Details))
        {
            try { d = JsonNode.Parse(e.Details) as JsonObject; }
            catch (JsonException) { }
        }

        var (label, cls, extra) = e.Result switch
        {
            null or "" or "Done" => ("Done", "good", (string?)null),
            "Conflict" => ("Conflict", "warn", null),
            "Failed" => ("Failed", "bad", null),
            var other => ("Done", "good", other),   // e.g. "31 items" from loading a starting list
        };

        var diffs = new List<DetailDiff>();
        var facts = new List<DetailFact>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? note = null;
        var target = string.IsNullOrWhiteSpace(e.Target) ? null : e.Target;

        var summary = e.Area switch
        {
            "schedule" => Schedule(e, d, target, diffs, facts, used, out note),
            "employees" when e.Action == "save" => Employee(d, target, diffs, used),
            "bonuses" => Bonus(e.Action, d, target, facts, used),
            "commissions" => Commission(e.Action, d, target, used),
            "sundries" => Sundry(e.Action, d, target, extra, used),
            "equipment" => Equipment(e.Action, d, target, extra, used),
            "reviews" => Review(e.Action, d, target, used),
            "connections" => Connection(e.Action, d, target, used),
            _ => null,
        } ?? Fallback(e, target);

        if (d is not null) Flatten(d, null, facts, used);
        if (extra is not null && !summary.Contains(extra)) facts.Insert(0, new("Result", extra));
        return new ChangeView(e, summary, note, label, cls, diffs, facts, AreaLink(e.Area));
    }

    private static string Fallback(ChangeLogEntry e, string? target) =>
        target is null ? Humanize(e.Action) : $"{Humanize(e.Action)} {target}";

    // ---- Schedule pushes: { TaskId, JobId, Original, Updated, Message } ----

    private string Schedule(ChangeLogEntry e, JsonObject? d, string? target, List<DetailDiff> diffs, List<DetailFact> facts, HashSet<string> used, out string? note)
    {
        used.UnionWith(["TaskId", "JobId", "Original", "Updated", "Message"]);
        var o = Get(d, "Original") as JsonObject;
        var u = Get(d, "Updated") as JsonObject;
        var message = Str(Get(d, "Message"));
        note = message is null or "Updated." or "Added." or "Deleted." ? null : message;
        if (Str(Get(d, "TaskId")) is { } taskId) facts.Add(new("JobTread task", taskId));
        if (Str(Get(d, "JobId")) is { } jobId) facts.Add(new("JobTread job", jobId));
        var name = target ?? Str(Get(u, "Name")) ?? Str(Get(o, "Name")) ?? "a task";

        switch (e.Action)
        {
            case "update" when o is not null && u is not null:
            {
                var rows = new[]
                {
                    new DetailDiff("Name", Str(Get(o, "Name")) ?? "—", Str(Get(u, "Name")) ?? "—"),
                    new DetailDiff("Days", Dates(o), Dates(u)),
                    new DetailDiff("Time", Times(o), Times(u)),
                    new DetailDiff("Crew", CrewText(Crew(o)), CrewText(Crew(u))),
                };
                diffs.AddRange(rows);
                var parts = new List<string>();
                if (rows[1].Changed) parts.Add($"moved {rows[1].Before} → {rows[1].After}");
                if (rows[2].Changed) parts.Add($"time {rows[2].Before} → {rows[2].After}");
                var before = Crew(o); var after = Crew(u);
                var added = after.Except(before).ToList(); var removed = before.Except(after).ToList();
                if (added.Count > 0) parts.Add("added " + string.Join(", ", added));
                if (removed.Count > 0) parts.Add("took off " + string.Join(", ", removed));
                if (rows[0].Changed) parts.Add($"renamed to “{rows[0].After}”");
                return parts.Count == 0 ? $"Updated {name} (no changes)" : $"Changed {name}: {string.Join("; ", parts)}";
            }
            case "create" when u is not null:
            {
                var crew = Crew(u);
                facts.Add(new("Days", Dates(u)));
                facts.Add(new("Time", Times(u)));
                facts.Add(new("Crew", CrewText(crew)));
                var who = crew.Count == 0 ? "no crew yet" : crew.Count <= 3 ? string.Join(", ", crew) : $"{crew.Count} crew";
                return $"Added {name} on {Dates(u)} ({who})";
            }
            case "delete":
            {
                if (o is not null)
                {
                    facts.Add(new("Days", Dates(o)));
                    facts.Add(new("Time", Times(o)));
                    facts.Add(new("Crew", CrewText(Crew(o))));
                }
                return o is null ? $"Deleted {name}" : $"Deleted {name} ({Dates(o)})";
            }
            default:
                return $"{Humanize(e.Action)} {name}";
        }
    }

    private List<string> Crew(JsonObject? task) =>
        (Get(task, "Crew") as JsonArray ?? []).Select(n => Str(n)).OfType<string>()
            .Select(id => crewNames.GetValueOrDefault(id) ?? $"Unknown ({id})").ToList();

    private static string CrewText(List<string> crew) => crew.Count == 0 ? "Nobody" : string.Join(", ", crew);

    private static string Dates(JsonObject? task)
    {
        var start = ParseDate(Str(Get(task, "StartDate")));
        var end = ParseDate(Str(Get(task, "EndDate")));
        return (start, end) switch
        {
            (null, null) => "Not scheduled",
            ({ } s, null) => Infrastructure.Format.Day(s),
            ({ } s, { } x) when s == x => Infrastructure.Format.Day(s),
            ({ } s, { } x) => $"{Infrastructure.Format.Day(s)} – {Infrastructure.Format.Day(x)}",
            (null, { } x) => Infrastructure.Format.Day(x),
        };
    }

    private static string Times(JsonObject? task)
    {
        var start = ParseTime(Str(Get(task, "StartTime")));
        var end = ParseTime(Str(Get(task, "EndTime")));
        if (start is null && end is null) return "No time set";
        return $"{start?.ToString("h:mm tt", Us) ?? "?"}–{end?.ToString("h:mm tt", Us) ?? "?"}";
    }

    private static DateOnly? ParseDate(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", Us, DateTimeStyles.None, out var d) ? d : null;
    private static TimeOnly? ParseTime(string? s) => s is { Length: >= 5 } && TimeOnly.TryParseExact(s[..5], "HH:mm", Us, DateTimeStyles.None, out var t) ? t : null;

    // ---- Employees: { before: { Level, Color, Notes, QbBasePay, QbPayType }, after: { level, color, notes, qbBasePay, qbPayType } } ----

    private static string Employee(JsonObject? d, string? target, List<DetailDiff> diffs, HashSet<string> used)
    {
        used.UnionWith(["before", "after"]);
        var b = Get(d, "before") as JsonObject;
        var a = Get(d, "after") as JsonObject;
        var rows = new[]
        {
            new DetailDiff("Level", LevelText(Str(Get(b, "Level"))), LevelText(Str(Get(a, "Level")))),
            new DetailDiff("PM color", Str(Get(b, "Color")) ?? "Default", Str(Get(a, "Color")) ?? "Default"),
            new DetailDiff("QB pay", PayText(Get(b, "QbBasePay"), Str(Get(b, "QbPayType"))), PayText(Get(a, "QbBasePay"), Str(Get(a, "QbPayType")))),
            new DetailDiff("Notes", Str(Get(b, "Notes")) ?? "—", Str(Get(a, "Notes")) ?? "—"),
        };
        diffs.AddRange(rows);
        var parts = rows.Where(r => r.Changed).Select(r => r.Label == "Notes" ? "notes edited" : $"{Lower(r.Label)} {r.Before} → {r.After}").ToList();
        var name = target ?? "an employee";
        return parts.Count == 0 ? $"Saved {name} (no changes)" : $"Updated {name}: {string.Join(", ", parts)}";
    }

    private static string LevelText(string? level) => string.IsNullOrEmpty(level) ? "Default" : Levels.Label(level);

    private static string PayText(JsonNode? amount, string? type) => Dec(amount) is not { } a ? "Not set"
        : type == "Salary" ? a.ToString("$#,0", Us) + "/yr" : a.ToString("$#,0.00", Us) + "/hr";

    private static string Lower(string label) => label.StartsWith("QB") || label.StartsWith("PM") ? label : label.ToLowerInvariant();

    // ---- Bonuses: approve { Profit, SupervisorPool, FieldPool, Lines }; paid / unpaid / unapprove (no details) ----

    private static string? Bonus(string action, JsonObject? d, string? target, List<DetailFact> facts, HashSet<string> used)
    {
        var job = target ?? "a customer";
        switch (action)
        {
            case "approve":
                used.UnionWith(["Lines", "Profit", "SupervisorPool", "FieldPool"]);
                var sup = Dec(Get(d, "SupervisorPool")); var field = Dec(Get(d, "FieldPool"));
                facts.Add(new("Job profit", Money(Get(d, "Profit"))));
                facts.Add(new("Supervisor pool", Money(sup)));
                facts.Add(new("Field pool", Money(field)));
                foreach (var line in (Get(d, "Lines") as JsonArray ?? []).OfType<JsonObject>())
                    facts.Add(new(Str(Get(line, "Name")) ?? "Someone",
                        $"{Money(Get(line, "Amount"))} · {Infrastructure.Format.Number(Dec(Get(line, "Hours")))} hrs · {Humanize(Str(Get(line, "Role")) ?? "")}".TrimEnd(' ', '·')));
                return sup is null && field is null
                    ? $"Approved the bonus for {job}"
                    : $"Approved the bonus for {job}: {Money(sup)} supervisors + {Money(field)} field";
            case "paid": return $"Marked the bonus for {job} paid";
            case "unpaid": return $"Marked the bonus for {job} not paid";
            case "unapprove": return $"Took back the bonus approval for {job}";
            default: return null;
        }
    }

    // ---- Commissions: rule { percent, basis, notes }; pay { jobId, amount, paidOn, note }; delete-payment ----

    private static string? Commission(string action, JsonObject? d, string? target, HashSet<string> used)
    {
        switch (action)
        {
            case "rule":
                used.UnionWith(["basis", "percent"]);
                var pct = Dec(Get(d, "percent"));
                return $"Set {Possessive(target ?? "someone")} commission to {(pct is null ? "?" : pct.Value.ToString("0.##", Us))}% {SalesService.BasisLabel(Str(Get(d, "basis")) ?? "collected")}";
            case "pay":
                var parts = (target ?? "").Split(" · ", 2);
                var on = ParseDate(Str(Get(d, "paidOn")));
                return $"Recorded {Money(Get(d, "amount"))} commission paid to {parts[0]}{(parts.Length > 1 ? " for " + parts[1] : "")}{(on is { } o ? " on " + Infrastructure.Format.Date(o) : "")}";
            case "delete-payment":
                return $"Deleted a commission payment (#{target})";
            default: return null;
        }
    }

    // ---- Sundries: add-item / edit-item (name); receive (item id) { q, unitCost, note }; issue (job) { itemId, q, cost }; undo-issue; load-starting-list ----

    private string? Sundry(string action, JsonObject? d, string? target, string? extra, HashSet<string> used)
    {
        switch (action)
        {
            case "add-item": return $"Added sundry {target}";
            case "edit-item": return $"Edited sundry {target}";
            case "receive":
            {
                used.Add("q");
                var item = ItemName(target);
                var cost = Str(Get(d, "unitCost")) is { Length: > 0 } c ? $" at {(c.StartsWith('$') ? c : "$" + c)} each" : "";
                return $"Received {Infrastructure.Format.Number(Dec(Get(d, "q")))} {item}{cost}";
            }
            case "issue":
            {
                used.UnionWith(["q", "itemId", "cost"]);
                var item = ItemName(Str(Get(d, "itemId")));
                return $"Issued {Infrastructure.Format.Number(Dec(Get(d, "q")))} {item} to {target ?? "a job"} ({Money(Get(d, "cost"))})";
            }
            case "undo-issue": return $"Undid a sundry issued to a job (#{target})";
            case "count":
                used.Add("counted");
                return $"Counted {ItemName(target)}: {Infrastructure.Format.Number(Dec(Get(d, "counted")))} on hand";
            case "remove-item": return $"Removed {ItemName(target)} from the sundries list";
            case "load-starting-list": return $"Loaded the starting sundries list{Paren(extra)}";
            default: return null;
        }
    }

    // ---- Equipment: target is the piece's name ----

    private static string? Equipment(string action, JsonObject? d, string? target, string? extra, HashSet<string> used)
    {
        var gear = target ?? "a piece of equipment";
        switch (action)
        {
            case "load-starting-list": return $"Loaded the starting equipment list{Paren(extra)}";
            case "add": return $"Added equipment {gear}";
            case "edit": return $"Edited equipment {gear}";
            case "move":
                used.UnionWith(["to", "with"]);
                var with = Str(Get(d, "with")) is { Length: > 0 } w ? $", with {w}" : "";
                return $"Moved {gear} to {Str(Get(d, "to")) ?? "the shop"}{with}";
            case "condition":
            case "service":
                used.UnionWith(["condition", "note"]);
                var note = Str(Get(d, "note")) is { Length: > 0 } n ? $" ({n})" : "";
                return action == "service" ? $"Serviced {gear}, now {Str(Get(d, "condition"))}{note}" : $"Marked {gear} {Str(Get(d, "condition"))}{note}";
            case "retire": return $"Retired {gear}";
            default: return null;
        }
    }

    private string ItemName(string? id) =>
        int.TryParse(id, out var n) && itemNames.TryGetValue(n, out var name) ? name : id is null ? "an item" : $"item #{id}";

    // ---- Reviews: action is the status (requested / received / later / skip) { platform, rating, notes } ----

    private static string? Review(string action, JsonObject? d, string? target, HashSet<string> used)
    {
        var job = target ?? "a job";
        switch (action)
        {
            case "requested": return $"Review requested for {job}";
            case "received":
                used.UnionWith(["platform", "rating"]);
                var rating = Dec(Get(d, "rating")) is { } r ? $" {r:0}★" : "";
                var platform = Str(Get(d, "platform")) is { Length: > 0 } p ? $" on {p}" : "";
                return $"Review received for {job}:{rating}{platform}".TrimEnd(':');
            case "later": return $"Review: ask {job} later";
            case "skip": return $"Not asking for a review on {job}";
            default: return null;
        }
    }

    // ---- Connections ----

    private static string? Connection(string action, JsonObject? d, string? target, HashSet<string> used) => action switch
    {
        "connect" when target == "Google" => Str(Get(d, "email")) is { } email ? Done(used, "email", $"Connected Google ({email})") : "Connected Google",
        "google-ads-account" => $"Picked Google Ads account {target}",
        "google-location" => $"Picked Business Profile location {target}",
        "connect" => Str(Get(d, "CompanyName")) is { } company ? Done(used, "CompanyName", $"Connected QuickBooks ({company})") : "Connected QuickBooks",
        "disconnect" => $"Disconnected {target ?? "QuickBooks"}",
        "link-jobtread" => target is null ? "Linked a JobTread key" : $"Linked a JobTread key (as {target})",
        "jobtread-signin" => target is null ? "Signed in with JobTread" : $"Signed in with JobTread (as {target})",
        _ => null,
    };

    private static string Done(HashSet<string> used, string key, string summary) { used.Add(key); return summary; }

    // ---- Generic details: every field not already shown, as a readable label and value ----

    private static void Flatten(JsonObject obj, string? prefix, List<DetailFact> facts, HashSet<string> used)
    {
        foreach (var (key, value) in obj)
        {
            if (prefix is null && used.Contains(key)) continue;
            var label = prefix is null ? Humanize(key) : $"{prefix} · {Humanize(key)}";
            switch (value)
            {
                case null: continue;
                case JsonObject child: Flatten(child, label, facts, used); break;
                case JsonArray arr when arr.All(x => x is JsonValue):
                    if (arr.Count > 0) facts.Add(new(label, string.Join(", ", arr.Select(Str))));
                    break;
                case JsonArray arr:
                    var i = 0;
                    foreach (var item in arr)
                    {
                        i++;
                        if (item is JsonObject o) facts.Add(new($"{label} {i}", string.Join(" · ", o.Where(p => p.Value is JsonValue).Select(p => $"{Humanize(p.Key)}: {Value(p.Key, p.Value)}"))));
                    }
                    break;
                default:
                    var text = Value(key, value);
                    if (!string.IsNullOrWhiteSpace(text)) facts.Add(new(label, text));
                    break;
            }
        }
    }

    private static readonly string[] MoneyWords = ["amount", "cost", "profit", "pool", "pay", "price"];

    private static string Value(string key, JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<decimal>(out var n) && MoneyWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return Money(n);
        if (node is JsonValue b && b.TryGetValue<bool>(out var flag)) return flag ? "Yes" : "No";
        var s = Str(node) ?? "";
        return ParseDate(s) is { } date ? date.ToString("MMM d, yyyy", Us) : s;
    }

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["q"] = "Quantity", ["qbBasePay"] = "QB base pay", ["qbPayType"] = "QB pay type", ["realmId"] = "QuickBooks company id",
        ["jobId"] = "JobTread job", ["taskId"] = "JobTread task", ["itemId"] = "Item", ["paidOn"] = "Paid on", ["companyName"] = "Company",
    };

    /// <summary>"SupervisorPool" → "Supervisor pool", "load-starting-list" → "Load starting list".</summary>
    public static string Humanize(string key)
    {
        if (Words.TryGetValue(key, out var w)) return w;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c is '-' or '_') { sb.Append(' '); continue; }
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(key[i - 1])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? key : char.ToUpperInvariant(s[0]) + s[1..];
    }

    // ---- JSON helpers (property names vary in case between writers) ----

    private static JsonNode? Get(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        if (obj.TryGetPropertyValue(key, out var v)) return v;
        foreach (var (k, value) in obj) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v
        ? (v.TryGetValue<string>(out var s) ? (string.IsNullOrEmpty(s) ? null : s) : v.ToJsonString())
        : null;

    private static decimal? Dec(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue<decimal>(out var d) || (v.TryGetValue<string>(out var s) && decimal.TryParse(s?.Replace("$", "").Replace(",", ""), NumberStyles.Number, Us, out d))) ? d : null;

    private static string Money(JsonNode? n) => Money(Dec(n));
    private static string Money(decimal? d) => d is { } x && x != Math.Round(x) ? x.ToString("$#,##0.00;-$#,##0.00", Us) : Infrastructure.Format.Money(d);
    private static string Paren(string? s) => s is null ? "" : $" ({s})";
    private static string Possessive(string name) => name.EndsWith('s') ? name + "’" : name + "’s";
}
