using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Integrations.JobTread;

namespace PurpleDashboard.Modules.People;

/// <summary>A staff member in JobTread. MembershipId is what tasks are assigned by.</summary>
/// <summary>A JobTread time entry type on a person, with its burdened cost rate (e.g. "Painter" $41.66, "Prevailing Wage SBCC" $85.44).</summary>
public sealed record RateType(string Name, decimal HourlyRate);

public sealed record StaffMember(string MembershipId, string UserId, string Name, string Role, string? QboEmployeeId = null)
{
    /// <summary>JobTread burdened rates by time entry type. The first is their regular type.</summary>
    public IReadOnlyList<RateType> Rates { get; init; } = [];

    /// <summary>Regular burdened rate in JobTread (the first type that isn't overtime, prevailing wage, sick or holiday).</summary>
    public RateType? BaseRate => Rates.FirstOrDefault(r => !IsSpecial(r.Name)) ?? Rates.FirstOrDefault();

    private static bool IsSpecial(string name) =>
        name.Contains("Overtime", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" OT", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Prevailing", StringComparison.OrdinalIgnoreCase) || name.Contains("Sick", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Holiday", StringComparison.OrdinalIgnoreCase);

    /// <summary>Painters are "Field Employee"; everyone else (admin, PM, office, sales) is a supervisor.</summary>
    public bool IsField => Role == "Field Employee";

    public string FirstName => Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Name;
}

/// <summary>Internal JobTread staff and their roles, cached for 30 minutes.</summary>
public sealed class PeopleService(IJobTreadClient jobTread, IMemoryCache cache, IOptions<JobTreadOptions> options)
{
    public async Task<IReadOnlyList<StaffMember>> GetStaffAsync(CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync("people:staff", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = options.Value.OrganizationId },
                    ["memberships"] = JsonNode.Parse("""{ "$": { "size": 100, "where": ["isInternal", true] }, "nodes": { "id": {}, "qboEmployeeId": {}, "timeEntryTypes": { "name": {}, "hourlyRate": {} }, "user": { "id": {}, "name": {} }, "role": { "name": {} } } }"""),
                },
            };
            var nodes = (await jobTread.QueryAsync("people-staff", query, ct))["organization"]?["memberships"].Nodes() ?? [];
            return (IReadOnlyList<StaffMember>)nodes
                .Select(m => (Id: m.Str("id"), UserId: m["user"].Str("id"), Name: m["user"].Str("name"), Role: m["role"].Str("name"), Qbo: m.Str("qboEmployeeId"),
                    Rates: (m["timeEntryTypes"] as JsonArray ?? []).Select(t => new RateType(t.Str("name") ?? "Rate", t.Dec("hourlyRate") ?? 0)).Where(r => r.HourlyRate > 0).ToList()))
                .Where(m => m.Id is not null && m.UserId is not null && m.Name is not null)
                .Select(m => new StaffMember(m.Id!, m.UserId!, m.Name!, m.Role ?? "", m.Qbo) { Rates = m.Rates })
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }))!;

    /// <summary>User ids of supervisors (anyone internal who isn't a Field Employee).</summary>
    public async Task<HashSet<string>> GetSupervisorUserIdsAsync(CancellationToken ct = default) =>
        (await GetStaffAsync(ct)).Where(s => !s.IsField).Select(s => s.UserId).ToHashSet();
}
