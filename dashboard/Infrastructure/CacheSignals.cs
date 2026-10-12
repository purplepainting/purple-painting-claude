using Microsoft.Extensions.Primitives;

namespace PurpleDashboard.Infrastructure;

/// <summary>
/// Groups of cached JobTread data that can be dropped at once when JobTread says something changed
/// (see JobTreadChangeWatcher). Cache entries add <see cref="Token"/> for their area.
/// </summary>
public static class CacheSignals
{
    public const string Projects = "projects", Labor = "labor", Financials = "financials";

    private static readonly Dictionary<string, CancellationTokenSource> Sources = new()
    {
        [Projects] = new(), [Labor] = new(), [Financials] = new(),
    };
    private static readonly Lock Gate = new();

    public static IChangeToken Token(string area)
    {
        lock (Gate) return new CancellationChangeToken(Sources[area].Token);
    }

    public static void Reset(string area)
    {
        CancellationTokenSource old;
        lock (Gate) { old = Sources[area]; Sources[area] = new CancellationTokenSource(); }
        old.Cancel();
        old.Dispose();
    }
}
