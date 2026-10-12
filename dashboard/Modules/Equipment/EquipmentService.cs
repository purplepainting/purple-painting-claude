using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PurpleDashboard.Data;

namespace PurpleDashboard.Modules.Equipment;

/// <summary>
/// Shop sundries (stock, bulk orders in, issued to jobs with cost, reorder alerts) and equipment
/// (location, condition, history). All dashboard-owned data. Sundry costs issued to a job are tracked
/// here; pushing them into JobTread as job costs would be a JobTread write (needs Kareem's approval).
/// </summary>
public sealed class EquipmentService(IServiceProvider services, DatabaseStatus database)
{
    public static readonly string[] Categories = ["Tape", "Plastic & masking", "Caulk & patch", "Sandpaper & abrasives", "Rollers & covers", "Brushes", "Drop cloths", "Cleaning", "Safety & PPE", "Other"];
    public static readonly string[] EquipmentTypes = ["Sprayer", "Ladder", "Scaffold", "Pressure washer", "Vehicle", "Lift", "Sander", "Other"];
    public static readonly string[] Conditions = ["Good", "Needs service", "Out of service"];

    public bool Available => database.Available;
    private DashboardDb Db => services.GetRequiredService<DashboardDb>();

    public static bool IsLow(InventoryItem i) => i.ReorderAt > 0 && i.OnHand <= i.ReorderAt;

    // ---- Sundries ----

    public async Task<List<InventoryItem>> ItemsAsync(CancellationToken ct) =>
        database.Available ? await Db.InventoryItems.AsNoTracking().Where(i => i.Active).OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync(ct) : [];

    public async Task SaveItemAsync(InventoryItem input, CancellationToken ct)
    {
        var db = Db;
        var item = input.Id == 0 ? null : await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == input.Id, ct);
        if (item is null) { db.InventoryItems.Add(input); }
        else
        {
            item.Name = input.Name; item.Category = input.Category; item.Unit = input.Unit;
            item.ReorderAt = input.ReorderAt; item.ReorderQty = input.ReorderQty;
            item.Vendor = input.Vendor; item.Sku = input.Sku; item.Notes = input.Notes;
            if (input.UnitCost > 0) item.UnitCost = input.UnitCost;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task ArchiveItemAsync(int id, CancellationToken ct)
    {
        var db = Db;
        if (await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id, ct) is { } item) { item.Active = false; await db.SaveChangesAsync(ct); }
    }

    /// <summary>A bulk order arrived: add to stock and update the average cost.</summary>
    public async Task ReceiveAsync(int itemId, decimal qty, decimal unitCost, DateOnly date, string? note, string user, CancellationToken ct)
    {
        var db = Db;
        var item = await db.InventoryItems.FirstAsync(i => i.Id == itemId, ct);
        var onHand = Math.Max(0, item.OnHand);
        if (unitCost > 0 && onHand + qty > 0) item.UnitCost = Math.Round((onHand * item.UnitCost + qty * unitCost) / (onHand + qty), 4);
        item.OnHand += qty;
        db.InventoryMovements.Add(new InventoryMovement { ItemId = itemId, Date = date, Kind = "received", Quantity = qty, UnitCost = unitCost > 0 ? unitCost : item.UnitCost, Note = note, CreatedAt = DateTime.UtcNow, CreatedBy = user });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Take sundries out of the shop for a job, at the current average cost.</summary>
    public async Task<decimal> IssueAsync(int itemId, decimal qty, string jobId, string jobName, DateOnly date, string? note, string user, CancellationToken ct)
    {
        var db = Db;
        var item = await db.InventoryItems.FirstAsync(i => i.Id == itemId, ct);
        item.OnHand -= qty;
        db.InventoryMovements.Add(new InventoryMovement { ItemId = itemId, Date = date, Kind = "issued", Quantity = -qty, UnitCost = item.UnitCost, JobId = jobId, JobName = jobName, Note = note, CreatedAt = DateTime.UtcNow, CreatedBy = user });
        await db.SaveChangesAsync(ct);
        return Math.Round(qty * item.UnitCost, 2);
    }

    /// <summary>Physical count: set on hand to what's actually on the shelf.</summary>
    public async Task CountAsync(int itemId, decimal counted, string user, CancellationToken ct)
    {
        var db = Db;
        var item = await db.InventoryItems.FirstAsync(i => i.Id == itemId, ct);
        var diff = counted - item.OnHand;
        if (diff == 0) return;
        item.OnHand = counted;
        db.InventoryMovements.Add(new InventoryMovement { ItemId = itemId, Date = PurpleDashboard.Infrastructure.LocalClock.Today, Kind = "count", Quantity = diff, UnitCost = item.UnitCost, Note = "Count correction", CreatedAt = DateTime.UtcNow, CreatedBy = user });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<InventoryMovement>> MovementsAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        database.Available ? await Db.InventoryMovements.AsNoTracking().Where(m => m.Date >= from && m.Date <= to).OrderByDescending(m => m.Date).ThenByDescending(m => m.Id).ToListAsync(ct) : [];

    /// <summary>Undo an issue to a job (puts the stock back).</summary>
    public async Task UndoIssueAsync(long movementId, CancellationToken ct)
    {
        var db = Db;
        var m = await db.InventoryMovements.FirstOrDefaultAsync(x => x.Id == movementId && x.Kind == "issued", ct);
        if (m is null) return;
        if (await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == m.ItemId, ct) is { } item) item.OnHand -= m.Quantity;
        db.InventoryMovements.Remove(m);
        await db.SaveChangesAsync(ct);
    }

    // ---- Equipment ----

    public async Task<List<EquipmentItem>> EquipmentAsync(CancellationToken ct) =>
        database.Available ? await Db.Equipment.AsNoTracking().Where(e => e.Active).OrderBy(e => e.JobName ?? "").ThenBy(e => e.Type).ThenBy(e => e.Name).ToListAsync(ct) : [];

    public async Task<List<EquipmentEvent>> EventsAsync(int equipmentId, CancellationToken ct) =>
        await Db.EquipmentEvents.AsNoTracking().Where(e => e.EquipmentId == equipmentId).OrderByDescending(e => e.At).Take(30).ToListAsync(ct);

    public async Task<Dictionary<int, List<EquipmentEvent>>> RecentEventsAsync(CancellationToken ct) =>
        database.Available
            ? (await Db.EquipmentEvents.AsNoTracking().OrderByDescending(e => e.At).Take(500).ToListAsync(ct)).GroupBy(e => e.EquipmentId).ToDictionary(g => g.Key, g => g.Take(10).ToList())
            : [];

    public async Task SaveEquipmentAsync(EquipmentItem input, string user, CancellationToken ct)
    {
        var db = Db;
        var e = input.Id == 0 ? null : await db.Equipment.FirstOrDefaultAsync(x => x.Id == input.Id, ct);
        if (e is null)
        {
            input.UpdatedAt = DateTime.UtcNow;
            db.Equipment.Add(input);
            await db.SaveChangesAsync(ct);
            await LogAsync(db, input.Id, "added", $"Added ({input.Condition})", user, ct);
            return;
        }
        e.Name = input.Name; e.Type = input.Type; e.Tag = input.Tag; e.Value = input.Value; e.Notes = input.Notes; e.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Move to the shop (jobId null) or a job, optionally with who has it.</summary>
    public async Task MoveAsync(int id, string? jobId, string? jobName, string? assignedTo, string user, CancellationToken ct)
    {
        var db = Db;
        var e = await db.Equipment.FirstAsync(x => x.Id == id, ct);
        e.JobId = string.IsNullOrEmpty(jobId) ? null : jobId;
        e.JobName = e.JobId is null ? null : jobName;
        e.AssignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim();
        e.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await LogAsync(db, id, "moved", (e.JobName ?? "Shop") + (e.AssignedTo is null ? "" : $" · with {e.AssignedTo}"), user, ct);
    }

    public async Task SetConditionAsync(int id, string condition, string? note, bool serviced, string user, CancellationToken ct)
    {
        var db = Db;
        var e = await db.Equipment.FirstAsync(x => x.Id == id, ct);
        var changed = e.Condition != condition;
        e.Condition = Conditions.Contains(condition) ? condition : e.Condition;
        if (serviced) e.LastServiced = PurpleDashboard.Infrastructure.LocalClock.Today;
        e.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var detail = string.Join(" · ", new[] { changed ? e.Condition : null, serviced ? "Serviced" : null, string.IsNullOrWhiteSpace(note) ? null : note.Trim() }.OfType<string>());
        if (detail.Length > 0) await LogAsync(db, id, serviced ? "service" : changed ? "condition" : "note", detail, user, ct);
    }

    public async Task RetireAsync(int id, string user, CancellationToken ct)
    {
        var db = Db;
        if (await db.Equipment.FirstOrDefaultAsync(x => x.Id == id, ct) is { } e) { e.Active = false; await db.SaveChangesAsync(ct); await LogAsync(db, id, "retired", "Retired", user, ct); }
    }

    // ---- Starting list (from the SharePoint equipment list and the April 2025 bulk sundry order) ----

    private sealed record SeedFile(List<SeedGear> Equipment, List<SeedSundry> Sundries);
    private sealed record SeedGear(string Name, string? Type, string? Tag, string? Vehicle, string? Site, string Condition, string? Notes);
    private sealed record SeedSundry(string Name, string? Category, string? Unit, decimal UnitCost, string? Vendor, string? Sku);

    private static SeedFile LoadSeed()
    {
        using var stream = typeof(EquipmentService).Assembly.GetManifestResourceStream("PurpleDashboard.Equipment.starting-list.json")
            ?? throw new InvalidOperationException("starting-list.json is not embedded");
        return JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>
    /// Loads the starting sundries list, only when there are no sundries yet. Counts start at 0 (the source
    /// was an order sheet, not a count), so take a count after loading.
    /// </summary>
    public async Task<int> LoadStartingSundriesAsync(CancellationToken ct)
    {
        var db = Db;
        if (await db.InventoryItems.AnyAsync(ct)) return 0;
        var seed = LoadSeed().Sundries;
        db.InventoryItems.AddRange(seed.Select(s => new InventoryItem { Name = s.Name, Category = s.Category, Unit = s.Unit, UnitCost = s.UnitCost, Vendor = s.Vendor, Sku = s.Sku }));
        await db.SaveChangesAsync(ct);
        return seed.Count;
    }

    /// <summary>
    /// Loads the starting equipment list, only when there's no equipment yet. A job site from the sheet is
    /// matched to an open JobTread job by name or address; trucks and trailers go in "with"; anything else
    /// stays at the shop with the sheet's location in the notes.
    /// </summary>
    public async Task<int> LoadStartingEquipmentAsync(IReadOnlyList<(string Id, string Name, string? Address)> openJobs, string user, CancellationToken ct)
    {
        var db = Db;
        if (await db.Equipment.AnyAsync(ct)) return 0;
        var seed = LoadSeed().Equipment;
        var items = new List<EquipmentItem>();
        foreach (var g in seed)
        {
            var notes = g.Notes;
            (string Id, string Name, string? Address)? job = null;
            if (g.Site is not null)
            {
                var key = Key(g.Site);
                job = openJobs.Where(j => Key(j.Name).Contains(key) || (j.Address is not null && Key(j.Address).Contains(key))).Select(j => ((string, string, string?)?)j).FirstOrDefault();
                if (job is null) notes = string.Join("; ", new[] { $"Sheet location: {g.Site}", notes }.OfType<string>());
            }
            items.Add(new EquipmentItem
            {
                Name = g.Name, Type = g.Type, Tag = g.Tag, Condition = Conditions.Contains(g.Condition) ? g.Condition : "Good", Notes = notes,
                JobId = job?.Id, JobName = job?.Name, AssignedTo = g.Vehicle, UpdatedAt = DateTime.UtcNow,
            });
        }
        db.Equipment.AddRange(items);
        await db.SaveChangesAsync(ct);
        db.EquipmentEvents.AddRange(items.Select(e => new EquipmentEvent { EquipmentId = e.Id, At = DateTime.UtcNow, Kind = "added", Detail = "Added from the SharePoint equipment list", By = user }));
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    private static string Key(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static async Task LogAsync(DashboardDb db, int id, string kind, string detail, string user, CancellationToken ct)
    {
        db.EquipmentEvents.Add(new EquipmentEvent { EquipmentId = id, At = DateTime.UtcNow, Kind = kind, Detail = detail, By = user });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Equipment tile: sundries at or below their reorder point.</summary>
public sealed class LowStockMetric(EquipmentService equipment) : ITileMetric
{
    public string ModuleKey => "equipment";

    public async Task<string?> GetValueAsync(CancellationToken ct) =>
        equipment.Available ? (await equipment.ItemsAsync(ct)).Count(EquipmentService.IsLow).ToString() : null;
}
