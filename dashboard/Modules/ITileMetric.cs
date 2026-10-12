namespace PurpleDashboard.Modules;

/// <summary>
/// Supplies the key number on a module's menu tile. Register an implementation in
/// Program.cs; the menu shows no number for modules without one or if it fails.
/// </summary>
public interface ITileMetric
{
    string ModuleKey { get; }
    Task<string?> GetValueAsync(CancellationToken ct);
}
