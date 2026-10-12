namespace PurpleDashboard.Modules.People;

/// <summary>Employees tile: how many internal JobTread staff there are.</summary>
public sealed class TeamMembersMetric(PeopleService people) : ITileMetric
{
    public string ModuleKey => "employees";

    public async Task<string?> GetValueAsync(CancellationToken ct) => (await people.GetStaffAsync(ct)).Count.ToString();
}
