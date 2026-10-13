namespace PurpleDashboard.Modules.People;

/// <summary>Employees tile: how many internal JobTread staff there are, leaving out anyone QuickBooks has as inactive.</summary>
public sealed class TeamMembersMetric(PeopleService people, QbEmployeeStatus qbStatus) : ITileMetric
{
    public string ModuleKey => "employees";

    public async Task<string?> GetValueAsync(CancellationToken ct)
    {
        var active = await qbStatus.ActiveByIdAsync(ct);
        return (await people.GetStaffAsync(ct)).Count(s => QbEmployeeStatus.IsCurrent(s, active)).ToString();
    }
}
