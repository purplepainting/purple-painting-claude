namespace PurpleDashboard.Infrastructure;

/// <summary>
/// How long cached JobTread data can be kept. With the change feed working (JobTreadChangeWatcher), data is
/// kept up to 30 minutes and dropped as soon as JobTread reports a change that touches it. An area only gets
/// the long time when every kind of event it depends on has actually come through the feed in the last week
/// (so if the JobTread webhook isn't sending task events, say, the schedule stays on the short 2-minute cache).
/// </summary>
public static class JobTreadFreshness
{
    public static readonly TimeSpan Short = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Long = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SeenWithin = TimeSpan.FromDays(7);
    private static readonly TimeSpan PollGrace = TimeSpan.FromMinutes(2);

    /// <summary>Event kinds (type prefixes) each cached area depends on.</summary>
    private static readonly Dictionary<string, string[]> Needs = new()
    {
        [CacheSignals.Projects] = ["job", "task", "timeEntry", "document"],
        [CacheSignals.Labor] = ["timeEntry"],
        [Schedule] = ["task", "job"],
    };

    public const string Schedule = "schedule";

    private static readonly Dictionary<string, DateTime> LastSeen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static DateTime lastPollOk = DateTime.MinValue;

    /// <summary>The feed answered (with or without events).</summary>
    public static void PollSucceeded() => lastPollOk = DateTime.UtcNow;

    /// <summary>Record event types that came through the feed, with when they happened.</summary>
    public static void Saw(IEnumerable<(string Type, DateTime At)> events)
    {
        lock (Gate)
        {
            foreach (var (type, at) in events)
            {
                var kind = Kind(type);
                if (kind.Length > 0 && (!LastSeen.TryGetValue(kind, out var prev) || at > prev)) LastSeen[kind] = at;
            }
        }
    }

    /// <summary>"taskUpdated" → "task", "documentCostItemCreated" → "documentCostItem".</summary>
    public static string Kind(string type)
    {
        foreach (var verb in new[] { "Created", "Updated", "Deleted" })
            if (type.EndsWith(verb, StringComparison.Ordinal)) return type[..^verb.Length];
        return type;
    }

    public static TimeSpan For(string area)
    {
        if (DateTime.UtcNow - lastPollOk > PollGrace) return Short;
        lock (Gate)
        {
            var cutoff = DateTime.UtcNow - SeenWithin;
            return Needs[area].All(k => LastSeen.TryGetValue(k, out var at) && at > cutoff) ? Long : Short;
        }
    }

    /// <summary>For /health: which event kinds have come through, and the cache time per area.</summary>
    public static object Describe()
    {
        Dictionary<string, DateTime> seen;
        lock (Gate) seen = LastSeen.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
        return new
        {
            feedOk = DateTime.UtcNow - lastPollOk <= PollGrace,
            seen,
            cacheMinutes = Needs.Keys.ToDictionary(a => a, a => For(a).TotalMinutes),
        };
    }
}
