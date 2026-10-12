namespace PurpleDashboard.Integrations.JobTread;

public sealed class JobTreadOptions
{
    /// <summary>Grant key for the Pave API. Azure setting JOBTREAD_API_KEY (same name as the function app).</summary>
    public string? GrantKey { get; set; }

    public string OrganizationId { get; set; } = JobTreadIds.OrganizationId;

    /// <summary>
    /// Local development only: a folder of saved JobTread responses ({queryName}.json) to use
    /// instead of calling the API. See docs/JOBTREAD.md.
    /// </summary>
    public string? FixtureDirectory { get; set; }
}
