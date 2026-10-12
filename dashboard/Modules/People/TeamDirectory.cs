using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PurpleDashboard.Data;
using PurpleDashboard.Modules.Scheduling;

namespace PurpleDashboard.Modules.People;

/// <summary>Crew levels, in display order.</summary>
public static class Levels
{
    public const string Foreman = "foreman", Painter = "painter", Helper = "helper", Supervisor = "supervisor";
    public static readonly IReadOnlyList<string> All = [Foreman, Painter, Helper, Supervisor];

    public static int Rank(string level) => level switch { Foreman => 0, Painter => 1, Helper => 2, _ => 3 };
    public static string Label(string level) => level.Length == 0 ? level : char.ToUpperInvariant(level[0]) + level[1..];
}

/// <summary>One person: JobTread staff record plus their dashboard profile (if any).</summary>
public sealed record TeamMember(StaffMember Staff, EmployeeProfile? Profile, string Level, bool LevelIsSet, string Color)
{
    public bool IsSupervisor => Level == Levels.Supervisor;

    /// <summary>
    /// Chip color class. Crew: green by level. Management: purple by JobTread role (Admin dark,
    /// Office medium, Sales light); PMs use their own PM color (see ChipStyle). Anyone else gray.
    /// </summary>
    public string ChipClass => !IsSupervisor ? "lvl-" + Level : Staff.Role switch
    {
        "Admin" => "lvl-supervisor lvl-admin",
        "Office Administrator" => "lvl-supervisor lvl-office",
        "Salesperson" => "lvl-supervisor lvl-sales",
        "Project Manager" => "lvl-supervisor lvl-pm",
        _ => "lvl-supervisor",
    };

    /// <summary>Inline style for a PM's chip (their PM color); empty for everyone else.</summary>
    public string? ChipStyle => IsSupervisor && Staff.Role == "Project Manager" ? $"background:{Color};color:#fff" : null;
}

/// <summary>
/// Everyone's level and color, ready for pages to look up without more calls. Order of precedence:
/// the Employees page (database profile), then appsettings (Crew lists, ProjectManagers colors),
/// then the JobTread role (non-field staff are supervisors, field staff painters).
/// </summary>
public sealed class Team
{
    private readonly Dictionary<string, TeamMember> byName;
    private readonly PmColorOptions fallbackColors;

    public Team(IReadOnlyList<TeamMember> members, PmColorOptions fallbackColors)
    {
        Members = members;
        this.fallbackColors = fallbackColors;
        byName = members.GroupBy(m => m.Staff.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        ByMembership = members.ToDictionary(m => m.Staff.MembershipId);
        ByUserId = members.GroupBy(m => m.Staff.UserId).ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyList<TeamMember> Members { get; }
    public IReadOnlyDictionary<string, TeamMember> ByMembership { get; }
    public IReadOnlyDictionary<string, TeamMember> ByUserId { get; }
    public IReadOnlyList<StaffMember> Staff => Members.Select(m => m.Staff).ToList();

    public TeamMember? Find(string name) => byName.GetValueOrDefault(name);

    public string ChipClass(string name) => Find(name)?.ChipClass ?? "lvl-" + Levels.Painter;
    public string? ChipStyle(string name) => Find(name)?.ChipStyle;

    /// <summary>"foreman", "painter", "helper" or "supervisor". Unknown names count as painters.</summary>
    public string LevelOf(string name) => Find(name)?.Level ?? Levels.Painter;

    /// <summary>A PM's tag color. Unknown names use the settings colors, then gray.</summary>
    public string PmColor(string? name) => name is not null && Find(name) is { } m ? m.Color : fallbackColors.For(name);

    /// <summary>Inline style for a PM tag: colored text on a light tint of the same color.</summary>
    public string PmStyle(string? name) => PmColorOptions.StyleFor(PmColor(name));
}

/// <summary>
/// Builds the <see cref="Team"/> from JobTread staff and the dashboard database. Profiles are cached
/// for a minute and cleared when someone saves one. Works without a database (settings only).
/// </summary>
public sealed class TeamDirectory(
    PeopleService people,
    IServiceProvider services,
    DatabaseStatus database,
    IMemoryCache cache,
    IOptions<CrewLevelOptions> crewLevels,
    IOptions<PmColorOptions> pmColors,
    ChangeLogWriter changeLog,
    ILogger<TeamDirectory> logger)
{
    private const string ProfilesKey = "team:profiles";

    public bool CanEdit => database.Available;

    public async Task<Team> GetAsync(CancellationToken ct = default)
    {
        var staff = await people.GetStaffAsync(ct);
        var profiles = await GetProfilesAsync(ct);
        var members = staff.Select(s =>
        {
            var profile = profiles.GetValueOrDefault(s.MembershipId);
            var set = profile?.Level is { } l && Levels.All.Contains(l);
            var level = set ? profile!.Level! : DefaultLevel(s);
            var color = !string.IsNullOrEmpty(profile?.Color) ? profile.Color : pmColors.Value.For(s.Name);
            return new TeamMember(s, profile, level, set, color);
        }).ToList();
        return new Team(members, pmColors.Value);
    }

    /// <summary>Settings only, for when JobTread staff couldn't load.</summary>
    public Team Fallback() => new([], pmColors.Value);

    /// <summary>The level someone gets when no one has set it on the Employees page.</summary>
    public string DefaultLevel(StaffMember s) => !s.IsField ? Levels.Supervisor : crewLevels.Value.For(s.Name).ToString().ToLowerInvariant();

    public async Task SaveAsync(string membershipId, string? level, string? color, string? notes, string user, CancellationToken ct)
    {
        var db = services.GetRequiredService<DashboardDb>();
        var profile = await db.EmployeeProfiles.FirstOrDefaultAsync(p => p.MembershipId == membershipId, ct);
        if (profile is null) db.EmployeeProfiles.Add(profile = new EmployeeProfile { MembershipId = membershipId });
        var before = new { profile.Level, profile.Color, profile.Notes };

        profile.Level = level;
        profile.Color = color;
        profile.Notes = notes;
        profile.UpdatedAt = DateTime.UtcNow;
        profile.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
        cache.Remove(ProfilesKey);

        var name = (await people.GetStaffAsync(ct)).FirstOrDefault(s => s.MembershipId == membershipId)?.Name ?? membershipId;
        await changeLog.WriteAsync([ChangeLogWriter.Entry(user, "employees", "save", name, new { before, after = new { level, color, notes } }, "Done")], ct);
    }

    private async Task<IReadOnlyDictionary<string, EmployeeProfile>> GetProfilesAsync(CancellationToken ct)
    {
        if (!database.Available) return new Dictionary<string, EmployeeProfile>();
        if (cache.TryGetValue(ProfilesKey, out IReadOnlyDictionary<string, EmployeeProfile>? cached)) return cached!;
        try
        {
            var db = services.GetRequiredService<DashboardDb>();
            var profiles = await db.EmployeeProfiles.AsNoTracking().ToDictionaryAsync(p => p.MembershipId, ct);
            cache.Set(ProfilesKey, (IReadOnlyDictionary<string, EmployeeProfile>)profiles, TimeSpan.FromMinutes(1));
            return profiles;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Database hiccup: fall back to settings rather than breaking the schedule.
            logger.LogWarning(e, "Loading employee profiles failed; using settings");
            return new Dictionary<string, EmployeeProfile>();
        }
    }
}
