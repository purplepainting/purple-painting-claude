using PurpleDashboard.Auth;
using PurpleDashboard.Infrastructure;
using PurpleDashboard.Integrations.JobTread;
using PurpleDashboard.Modules.Customers;
using PurpleDashboard.Modules.Equipment;
using PurpleDashboard.Modules.People;
using PurpleDashboard.Modules.Projects;
using PurpleDashboard.Modules.Vendors;

namespace PurpleDashboard.Modules.Search;

/// <summary>One search hit. Url opens it in the dashboard; ExternalUrl (jobs only) opens it in JobTread.</summary>
public sealed record SearchHit(string Title, string? Sub, string Url, string? ExternalUrl = null);

/// <summary>Hits from one source (Jobs, Customers...). Total counts every match; Hits is the first few. MoreUrl lists them all.</summary>
public sealed record SearchGroup(string Key, string Label, IReadOnlyList<SearchHit> Hits, int Total, string? MoreUrl);

public sealed record SearchResults(string Query, IReadOnlyList<SearchGroup> Groups, IReadOnlyList<string> Skipped)
{
    public static readonly SearchResults Empty = new("", [], []);
    public bool Any => Groups.Any(g => g.Hits.Count > 0);
}

/// <summary>
/// Global search (top bar). Reads only what the module services already cache: jobs (open + closed in the
/// last 12 months), customers, staff, equipment and sundries (dashboard database), and QuickBooks bills
/// only when they're already in memory. Sources run in parallel, each with a time limit; a source that
/// throws, isn't connected or is too slow is skipped (listed in Skipped) so search never waits on it.
/// </summary>
public sealed class SearchService(
    ProjectsService projects, PeopleService people, EquipmentService equipment,
    CustomersService customers, PayablesService payables, Permissions permissions, ILogger<SearchService> logger)
{
    public const int MinLength = 2;

    public async Task<SearchResults> SearchAsync(string? query, int perGroup, TimeSpan timeLimit, CancellationToken ct)
    {
        var q = (query ?? "").Trim();
        if (q.Length < MinLength) return SearchResults.Empty with { Query = q };
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Only the groups this person may open (Auth/AccessMap.cs); the others aren't searched at all.
        var canJobs = permissions.CanAny(Perm.SchedulingView, Perm.ProjectsView);
        var canCustomers = permissions.Can(Perm.CustomersView);
        // Jobs feed both the Jobs and Customers groups, so load them once.
        var jobs = canJobs || canCustomers ? JobsAsync(ct) : Task.FromResult(new List<ProjectSummary>());
        var sources = new List<Task<Outcome>>();
        if (canJobs) sources.Add(Run("Jobs", Then(jobs, list => FindJobs(list, terms, q, perGroup, permissions.Can(Perm.SchedulingView))), timeLimit));
        if (canCustomers) sources.Add(Run("Customers", Then(jobs, list => FindCustomers(list, terms, q, perGroup)), timeLimit));
        if (permissions.Can(Perm.EmployeesView)) sources.Add(Run("People", FindPeopleAsync(terms, q, perGroup, ct), timeLimit));
        if (permissions.Can(Perm.EquipmentView)) sources.Add(Run("Equipment", FindEquipmentAsync(terms, q, perGroup, ct), timeLimit));
        if (permissions.Can(Perm.VendorsView)) sources.Add(Run("Vendors", Task.Run(() => FindVendors(terms, q, perGroup), ct), timeLimit));
        var results = await Task.WhenAll(sources);
        return new SearchResults(q,
            results.Where(r => r.Value is { Total: > 0 }).Select(r => r.Value!).ToList(),
            results.Where(r => r.Failed).Select(r => r.Name).Distinct().ToList());
    }

    // ---- Sources ----

    private async Task<List<ProjectSummary>> JobsAsync(CancellationToken ct)
    {
        var open = await projects.GetOpenJobsAsync(false, ct);
        var closed = await projects.GetClosedJobsAsync(LocalClock.Today.AddDays(-365), false, ct);
        return open.Concat(closed).GroupBy(j => j.Id).Select(g => g.First()).ToList();
    }

    private static SearchGroup FindJobs(List<ProjectSummary> jobs, string[] terms, string q, int take, bool timeline)
    {
        var hits = jobs
            .Select(j => (Job: j, Score: Score(terms, q, [j.Number, j.Name], [j.Customer, j.Address])))
            .Where(x => x.Score > 0)
            // Best match first, then jobs still going, then newest.
            .OrderByDescending(x => x.Score).ThenBy(x => x.Job.Stage is null or "Completed" ? 1 : 0)
            .ThenByDescending(x => int.TryParse(x.Job.Number, out var n) ? n : 0)
            .ToList();
        return new SearchGroup("jobs", "Jobs", hits.Take(take).Select(x => new SearchHit(
            $"#{x.Job.Number} {x.Job.Name}",
            Join(x.Job.Customer, x.Job.Stage ?? x.Job.BidStage ?? "Closed", x.Job.Address),
            // The job timeline lives under Scheduling; without it, the Projects list filtered to the job.
            timeline ? $"/scheduling/job/{Uri.EscapeDataString(x.Job.Id)}" : "/projects?q=" + Uri.EscapeDataString(x.Job.Number ?? x.Job.Name),
            JobTreadIds.JobUrl(x.Job.Id))).ToList(), hits.Count, null);
    }

    /// <summary>Customers from the job list, plus QuickBooks customers when they're already cached (no QuickBooks call).</summary>
    private SearchGroup FindCustomers(List<ProjectSummary> jobs, string[] terms, string q, int take)
    {
        var byName = jobs.Where(j => !string.IsNullOrWhiteSpace(j.Customer))
            .GroupBy(j => j.Customer!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Jobs: g.Count(), Owes: g.Sum(j => j.Unpaid), Address: g.Select(j => j.Address).FirstOrDefault(a => a is not null)), StringComparer.OrdinalIgnoreCase);
        foreach (var c in customers.CachedQbCustomers() ?? [])
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            byName[c.Name] = byName.TryGetValue(c.Name, out var have) ? have with { Owes = c.Balance } : (0, c.Balance, c.Address);
        }

        var hits = byName
            .Select(c => (Name: c.Key, c.Value, Score: Score(terms, q, [c.Key], [c.Value.Address])))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Value.Jobs).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SearchGroup("customers", "Customers", hits.Take(take).Select(x => new SearchHit(
            x.Name,
            Join(x.Value.Jobs switch { 0 => "No recent jobs", 1 => "1 job", var n => $"{n} jobs" }, x.Value.Owes >= 1 ? "owes " + Format.Money(x.Value.Owes) : null),
            "/customers?q=" + Uri.EscapeDataString(x.Name))).ToList(), hits.Count, "/customers?q=" + Uri.EscapeDataString(q));
    }

    private async Task<SearchGroup> FindPeopleAsync(string[] terms, string q, int take, CancellationToken ct)
    {
        var hits = (await people.GetStaffAsync(ct))
            .Select(s => (Staff: s, Score: Score(terms, q, [s.Name], [s.Role])))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Staff.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The Employees page filtered to the person, with their row open.
        return new SearchGroup("people", "People", hits.Take(take).Select(x => new SearchHit(
            x.Staff.Name, x.Staff.Role,
            $"/employees?q={Uri.EscapeDataString(x.Staff.Name)}&open={Uri.EscapeDataString(x.Staff.MembershipId)}#emp-{x.Staff.MembershipId}")).ToList(),
            hits.Count, "/employees?q=" + Uri.EscapeDataString(q));
    }

    /// <summary>Equipment and sundries (dashboard database). Empty when the database isn't connected.</summary>
    private async Task<SearchGroup> FindEquipmentAsync(string[] terms, string q, int take, CancellationToken ct)
    {
        if (!equipment.Available) return new SearchGroup("equipment", "Equipment & sundries", [], 0, null);
        // One query after the other: they share the request's database connection.
        var gear = await equipment.EquipmentAsync(ct);
        var items = await equipment.ItemsAsync(ct);
        var hits = gear
            .Select(g => (Score: Score(terms, q, [g.Name, g.Tag], [g.Type, g.AssignedTo, g.JobName]), Hit: new SearchHit(
                g.Name, Join(g.Type, g.Tag, g.AssignedTo is null ? g.JobName ?? "Shop" : "with " + g.AssignedTo, g.Condition == "Good" ? null : g.Condition),
                $"/equipment?tab=equipment#gear-{g.Id}")))
            .Concat(items.Select(i => (Score: Score(terms, q, [i.Name, i.Sku], [i.Category, i.Vendor]), Hit: new SearchHit(
                i.Name, Join("Sundry", i.Category, $"{i.OnHand:0.##} {i.Unit ?? "on hand"}", EquipmentService.IsLow(i) ? "low" : null),
                $"/equipment?tab=sundries#item-{i.Id}"))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Hit.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SearchGroup("equipment", "Equipment & sundries", hits.Take(take).Select(x => x.Hit).ToList(), hits.Count, null);
    }

    /// <summary>Vendors with open QuickBooks bills, only when the bills are already cached (no QuickBooks call).</summary>
    private SearchGroup FindVendors(string[] terms, string q, int take)
    {
        var bills = payables.CachedOpenBills() ?? [];
        var hits = bills.GroupBy(b => b.Vendor, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Vendor: g.Key, Bills: g.Count(), Owed: g.Sum(b => b.Balance),
                Score: Score(terms, q, [g.Key], g.Select(b => b.DocNumber).Concat(g.Select(b => b.Memo)).ToArray())))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Owed)
            .ToList();
        return new SearchGroup("vendors", "Vendors", hits.Take(take).Select(x => new SearchHit(
            x.Vendor, Join(x.Bills == 1 ? "1 open bill" : $"{x.Bills} open bills", Format.Money(x.Owed) + " owed"),
            "/vendors?view=all&q=" + Uri.EscapeDataString(x.Vendor))).ToList(), hits.Count, "/vendors?view=all&q=" + Uri.EscapeDataString(q));
    }

    // ---- Matching ----

    /// <summary>
    /// 0 unless every typed word starts a word in some field ("sher" finds Sherwin-Williams, not pressure
    /// washer; "36" finds job 362). Otherwise higher is better: the whole query equal to a main field
    /// (a job number, a name) beats a main field starting with it, beats a main field containing it.
    /// </summary>
    private static int Score(string[] terms, string q, string?[] main, string?[] other)
    {
        var fields = main.Concat(other).Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (!terms.All(t => fields.Any(f => WordStart(f!, t)))) return 0;
        var score = 1;
        foreach (var f in main)
        {
            if (string.IsNullOrEmpty(f)) continue;
            if (f.Equals(q, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 100);
            else if (f.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 50);
            else if (f.Contains(q, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 20);
        }
        return score;
    }

    private static bool WordStart(string field, string term)
    {
        for (var i = field.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0; i = field.IndexOf(term, i + 1, StringComparison.OrdinalIgnoreCase))
            if (i == 0 || !char.IsLetterOrDigit(field[i - 1])) return true;
        return false;
    }

    private static string? Join(params string?[] parts)
    {
        var s = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return s.Length == 0 ? null : s;
    }

    // ---- Running sources ----

    private sealed record Outcome(string Name, SearchGroup? Value, bool Failed);

    private static async Task<SearchGroup> Then<T>(Task<T> task, Func<T, SearchGroup> next) => next(await task);

    /// <summary>
    /// Waits up to the time limit. A slow source keeps running in the background (so its cache fills for
    /// the next search) but this search goes on without it.
    /// </summary>
    private async Task<Outcome> Run(string name, Task<SearchGroup> task, TimeSpan limit)
    {
        try { return new Outcome(name, await task.WaitAsync(limit), false); }
        catch (TimeoutException) { logger.LogInformation("Search skipped {Source}: slower than {Seconds}s", name, limit.TotalSeconds); }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogWarning(e, "Search skipped {Source}", name); }
        return new Outcome(name, null, true);
    }
}
