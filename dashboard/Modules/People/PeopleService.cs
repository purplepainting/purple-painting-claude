using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Integrations.JobTread;

namespace PurpleDashboard.Modules.People;

/// <summary>A staff member in JobTread. MembershipId is what tasks are assigned by.</summary>
public sealed record StaffMember(string MembershipId, string UserId, string Name, string Role)
{
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
                    ["memberships"] = JsonNode.Parse("""{ "$": { "size": 100, "where": ["isInternal", true] }, "nodes": { "id": {}, "user": { "id": {}, "name": {} }, "role": { "name": {} } } }"""),
                },
            };
            var nodes = (await jobTread.QueryAsync("people-staff", query, ct))["organization"]?["memberships"].Nodes() ?? [];
            return (IReadOnlyList<StaffMember>)nodes
                .Select(m => (Id: m.Str("id"), UserId: m["user"].Str("id"), Name: m["user"].Str("name"), Role: m["role"].Str("name")))
                .Where(m => m.Id is not null && m.UserId is not null && m.Name is not null)
                .Select(m => new StaffMember(m.Id!, m.UserId!, m.Name!, m.Role ?? ""))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }))!;

    /// <summary>User ids of supervisors (anyone internal who isn't a Field Employee).</summary>
    public async Task<HashSet<string>> GetSupervisorUserIdsAsync(CancellationToken ct = default) =>
        (await GetStaffAsync(ct)).Where(s => !s.IsField).Select(s => s.UserId).ToHashSet();
}
