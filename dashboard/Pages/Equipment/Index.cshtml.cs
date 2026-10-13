using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PurpleDashboard.Auth;
using PurpleDashboard.Data;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Equipment;
using PurpleDashboard.Modules.Projects;

namespace PurpleDashboard.Pages.Equipment;

public class IndexModel(EquipmentService equipment, ProjectsService projects, ChangeLogWriter changeLog, CurrentUser me, ILogger<IndexModel> logger) : PageModel
{
    /// <summary>"sundries" (default), "equipment", "jobs".</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    /// <summary>Job charges period: "30", "90" (default), "ytd".</summary>
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }

    public string CurrentTab => Tab is "equipment" or "jobs" ? Tab : "sundries";
    public bool CanEdit => equipment.Available;
    [TempData] public string? Message { get; set; }

    public List<InventoryItem> Items { get; private set; } = [];
    public List<EquipmentItem> Gear { get; private set; } = [];
    public Dictionary<int, List<EquipmentEvent>> Events { get; private set; } = [];
    public List<InventoryMovement> Movements { get; private set; } = [];
    public List<(string Id, string Name)> Jobs { get; private set; } = [];
    public DateOnly From { get; private set; }

    public IEnumerable<InventoryItem> LowStock => Items.Where(EquipmentService.IsLow).OrderBy(i => i.OnHand);
    public decimal StockValue => Items.Sum(i => Math.Max(0, i.OnHand) * i.UnitCost);

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Title"] = "Equipment & Sundries";
        ViewData["ModuleName"] = "Equipment & Sundries";
        var today = LocalClock.Today;
        From = Period switch { "30" => today.AddDays(-30), "ytd" => new DateOnly(today.Year, 1, 1), _ => today.AddDays(-90) };

        Items = await equipment.ItemsAsync(ct);
        if (CurrentTab == "equipment") { Gear = await equipment.EquipmentAsync(ct); Events = await equipment.RecentEventsAsync(ct); }
        if (CurrentTab == "jobs") Movements = (await equipment.MovementsAsync(From, today, ct)).Where(m => m.Kind == "issued").ToList();
        if (CurrentTab == "sundries") Movements = (await equipment.MovementsAsync(today.AddDays(-30), today, ct)).Take(40).ToList();

        try
        {
            Jobs = (await projects.GetOpenJobsAsync(false, ct))
                .Where(j => !j.IsInternal && j.Stage is not null)
                .OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
                .Select(j => (j.Id, j.Name)).ToList();
        }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Loading jobs for equipment failed");
        }
    }

    public string ItemName(int id) => Items.FirstOrDefault(i => i.Id == id)?.Name ?? "(removed item)";
    public string ItemUnit(int id) => Items.FirstOrDefault(i => i.Id == id)?.Unit ?? "";

    private static decimal Dec(string? s) => decimal.TryParse(s?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private string User() => me.Email ?? me.DisplayName ?? "unknown";
    private IActionResult Back(string? tab = null) => RedirectToPage(new { tab = tab ?? Tab, period = Period });
    private string JobName(string? id) => Jobs.FirstOrDefault(j => j.Id == id).Name ?? id ?? "";

    private async Task LoadJobsAsync(CancellationToken ct)
    {
        Jobs = (await projects.GetOpenJobsAsync(false, ct)).Select(j => (j.Id, j.Name)).ToList();
    }

    public async Task<IActionResult> OnPostItemAsync(int id, string name, string? category, string? unit, string? unitCost, string? onHand, string? reorderAt, string? reorderQty, string? vendor, string? sku, string? notes, CancellationToken ct)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(name)) return Back();
        var item = new InventoryItem
        {
            Id = id, Name = name.Trim(), Category = category, Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
            UnitCost = Dec(unitCost), ReorderAt = Dec(reorderAt), ReorderQty = Dec(reorderQty),
            Vendor = string.IsNullOrWhiteSpace(vendor) ? null : vendor.Trim(), Sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), OnHand = id == 0 ? Dec(onHand) : 0,
        };
        await equipment.SaveItemAsync(item, ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", id == 0 ? "add-item" : "edit-item", item.Name, null, "Done")], ct);
        Message = id == 0 ? $"Added {item.Name}." : $"Saved {item.Name}.";
        return Back("sundries");
    }

    public async Task<IActionResult> OnPostReceiveAsync(int itemId, string? qty, string? unitCost, DateOnly? date, string? note, CancellationToken ct)
    {
        var q = Dec(qty);
        if (!CanEdit || q <= 0) { Message = "Enter how many came in."; return Back("sundries"); }
        await equipment.ReceiveAsync(itemId, q, Dec(unitCost), date ?? LocalClock.Today, note, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", "receive", itemId.ToString(), new { q, unitCost, note }, "Done")], ct);
        Message = "Added to stock.";
        return Back("sundries");
    }

    public async Task<IActionResult> OnPostIssueAsync(int itemId, string? qty, string? jobId, DateOnly? date, string? note, CancellationToken ct)
    {
        var q = Dec(qty);
        if (!CanEdit || q <= 0 || string.IsNullOrEmpty(jobId)) { Message = "Pick a job and how many."; return Back("sundries"); }
        await LoadJobsAsync(ct);
        var jobName = JobName(jobId);
        var cost = await equipment.IssueAsync(itemId, q, jobId, jobName, date ?? LocalClock.Today, note, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", "issue", jobName, new { itemId, q, cost }, "Done")], ct);
        Message = $"Issued to {jobName} ({Format.Money(cost)}).";
        return Back("sundries");
    }

    public async Task<IActionResult> OnPostCountAsync(int itemId, string? counted, CancellationToken ct)
    {
        if (!CanEdit || counted is null) return Back("sundries");
        await equipment.CountAsync(itemId, Dec(counted), User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", "count", itemId.ToString(), new { counted = Dec(counted) }, "Done")], ct);
        Message = "Count saved.";
        return Back("sundries");
    }

    public async Task<IActionResult> OnPostArchiveItemAsync(int id, CancellationToken ct)
    {
        if (CanEdit)
        {
            await equipment.ArchiveItemAsync(id, ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", "remove-item", id.ToString(), null, "Done")], ct);
        }
        return Back("sundries");
    }

    public async Task<IActionResult> OnPostUndoIssueAsync(long id, CancellationToken ct)
    {
        if (CanEdit) { await equipment.UndoIssueAsync(id, ct); await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", "undo-issue", id.ToString(), null, "Done")], ct); }
        return Back();
    }

    public async Task<IActionResult> OnPostEquipmentAsync(int id, string name, string? type, string? tag, string? value, string? condition, string? notes, CancellationToken ct)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(name)) return Back("equipment");
        await equipment.SaveEquipmentAsync(new EquipmentItem
        {
            Id = id, Name = name.Trim(), Type = type, Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
            Value = Dec(value) is var v && v > 0 ? v : null, Condition = EquipmentService.Conditions.Contains(condition) ? condition! : "Good",
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        }, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "equipment", id == 0 ? "add" : "edit", name.Trim(), null, "Done")], ct);
        Message = id == 0 ? $"Added {name}." : $"Saved {name}.";
        return Back("equipment");
    }

    public async Task<IActionResult> OnPostMoveAsync(int id, string? jobId, string? assignedTo, CancellationToken ct)
    {
        if (!CanEdit) return Back("equipment");
        await LoadJobsAsync(ct);
        await equipment.MoveAsync(id, jobId, JobName(jobId), assignedTo, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "equipment", "move", await GearNameAsync(id, ct),
            new { to = string.IsNullOrEmpty(jobId) ? "Shop" : JobName(jobId), with = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim() }, "Done")], ct);
        Message = "Moved.";
        return Back("equipment");
    }

    public async Task<IActionResult> OnPostConditionAsync(int id, string condition, string? note, bool serviced, CancellationToken ct)
    {
        if (!CanEdit) return Back("equipment");
        await equipment.SetConditionAsync(id, condition, note, serviced, User(), ct);
        await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "equipment", serviced ? "service" : "condition", await GearNameAsync(id, ct),
            new { condition, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }, "Done")], ct);
        Message = "Saved.";
        return Back("equipment");
    }

    public async Task<IActionResult> OnPostLoadSundriesAsync(CancellationToken ct)
    {
        if (!CanEdit) return Back("sundries");
        var n = await equipment.LoadStartingSundriesAsync(ct);
        if (n > 0) await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "sundries", "load-starting-list", null, null, $"{n} items")], ct);
        Message = n > 0 ? $"Loaded {n} sundries. Counts are 0: take a count to set what's on the shelf." : "Sundries already exist, nothing loaded.";
        return Back("sundries");
    }

    public async Task<IActionResult> OnPostLoadEquipmentAsync(CancellationToken ct)
    {
        if (!CanEdit) return Back("equipment");
        List<(string, string, string?)> jobs = [];
        try { jobs = (await projects.GetOpenJobsAsync(false, ct)).Where(j => !j.IsInternal).Select(j => (j.Id, j.Name, j.Address)).ToList(); }
        catch (Exception e) when (e is JobTreadException or HttpRequestException or TaskCanceledException) { logger.LogWarning(e, "Loading jobs for equipment matching failed"); }
        var n = await equipment.LoadStartingEquipmentAsync(jobs, User(), ct);
        if (n > 0) await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "equipment", "load-starting-list", null, null, $"{n} pieces")], ct);
        Message = n > 0 ? $"Loaded {n} pieces of equipment." : "Equipment already exists, nothing loaded.";
        return Back("equipment");
    }

    public async Task<IActionResult> OnPostRetireAsync(int id, CancellationToken ct)
    {
        if (CanEdit)
        {
            var name = await GearNameAsync(id, ct);
            await equipment.RetireAsync(id, User(), ct);
            await changeLog.WriteAsync([ChangeLogWriter.Entry(User(), "equipment", "retire", name, null, "Done")], ct);
        }
        return Back("equipment");
    }

    private async Task<string> GearNameAsync(int id, CancellationToken ct) =>
        (await equipment.EquipmentAsync(ct)).FirstOrDefault(g => g.Id == id)?.Name ?? $"equipment #{id}";

    public static string Qty(decimal q) => q.ToString("#,0.##", CultureInfo.InvariantCulture);
    public static string Cost(decimal c) => c.ToString(c < 10 ? "$0.00" : "$#,0.00", CultureInfo.InvariantCulture);
    public static string ConditionCss(string c) => c switch { "Good" => "good", "Needs service" => "warn", _ => "bad" };
}
