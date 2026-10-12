namespace PurpleDashboard.Modules;

/// <summary>
/// Supplies the key number on a module's launchpad tile. Register an implementation in
/// Program.cs; the launchpad shows "—" for modules without one or if it fails.
/// </summary>
public interface ITileMetric
{
    string ModuleKey { get; }
    Task<string?> GetValueAsync(CancellationToken ct);
}
