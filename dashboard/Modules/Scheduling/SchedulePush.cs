using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PurpleDashboard.Integrations.JobTread;

namespace PurpleDashboard.Modules.Scheduling;

/// <summary>A task's editable fields, as the editor sends them. Dates yyyy-MM-dd, times HH:mm.</summary>
public sealed record TaskFields
{
    public string? Name { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? StartTime { get; init; }
    public string? EndTime { get; init; }
    public List<string> Crew { get; init; } = [];   // membership ids

    public bool SameAs(TaskFields o) =>
        Name == o.Name && StartDate == o.StartDate && EndDate == o.EndDate
        && Norm(StartTime) == Norm(o.StartTime) && Norm(EndTime) == Norm(o.EndTime)
        && Crew.Order().SequenceEqual(o.Crew.Order());

    private static string? Norm(string? t) => string.IsNullOrEmpty(t) ? null : t.Length >= 5 ? t[..5] : t;
}

public sealed record ScheduleChange
{
    /// <summary>"update", "create" or "delete" (falls back to renaming it DELETE and moving it to Sunday if JobTread refuses).</summary>
    public required string Kind { get; init; }
    public string? TaskId { get; init; }          // update, delete
    public string? JobId { get; init; }           // create
    public string? ParentTaskId { get; init; }    // create: put the new day under the same phase
    public string? TaskTypeId { get; init; }      // create
    public TaskFields? Original { get; init; }    // update, delete: what the editor loaded, to catch conflicts
    public TaskFields? Updated { get; init; }     // update, create; delete: the DELETE/Sunday fallback
    public string? Label { get; init; }           // for results and the log, e.g. "SBCC Cosmetology · Painting"
}

public sealed record PushRequest(List<ScheduleChange> Changes);

[JsonConverter(typeof(JsonStringEnumConverter<PushStatus>))]
public enum PushStatus { Done, Conflict, Failed }

public sealed record PushResult(string? Label, string Kind, PushStatus Status, string Message);

/// <summary>
/// Sends schedule changes to JobTread. Each change is checked against JobTread first: if the task
/// changed there since the editor loaded it, the change is skipped (not overwritten).
/// JobTread moves dependent tasks and notifies assigned crew (its defaults).
/// </summary>
public sealed class SchedulePushService(IJobTreadClient jobTread, ILogger<SchedulePushService> logger)
{
    public async Task<IReadOnlyList<PushResult>> PushAsync(IReadOnlyList<ScheduleChange> changes, string user, CancellationToken ct)
    {
        var current = await LoadCurrentAsync(changes.Where(c => c.TaskId is not null).Select(c => c.TaskId!).Distinct().ToList(), ct);
        var results = new List<PushResult>();

        foreach (var change in changes)
        {
            var result = await PushOneAsync(change, current, ct);
            results.Add(result);
            logger.LogInformation("Schedule push by {User}: {Kind} {Label} ({TaskId}) → {Status}: {Message}",
                user, change.Kind, change.Label, change.TaskId ?? change.JobId, result.Status, result.Message);
        }

        ScheduleService.ClearCache();
        return results;
    }

    private async Task<PushResult> PushOneAsync(ScheduleChange c, Dictionary<string, TaskFields> current, CancellationToken ct)
    {
        try
        {
            switch (c.Kind)
            {
                case "update" when c.TaskId is not null && c.Original is not null && c.Updated is not null:
                {
                    if (!current.TryGetValue(c.TaskId, out var now)) return new(c.Label, c.Kind, PushStatus.Conflict, "This task no longer exists in JobTread.");
                    if (!now.SameAs(c.Original)) return new(c.Label, c.Kind, PushStatus.Conflict, "Someone changed this task in JobTread after you loaded the schedule. Reload and try again.");

                    var args = new JsonObject { ["id"] = c.TaskId };
                    var u = c.Updated; var o = c.Original;
                    if (u.Name != o.Name && !string.IsNullOrWhiteSpace(u.Name)) args["name"] = u.Name;
                    if (u.StartDate != o.StartDate) args["startDate"] = u.StartDate;
                    if (u.EndDate != o.EndDate) args["endDate"] = u.EndDate;
                    if (u.StartTime != o.StartTime) args["startTime"] = Nullable(u.StartTime);
                    if (u.EndTime != o.EndTime) args["endTime"] = Nullable(u.EndTime);
                    if (!u.Crew.Order().SequenceEqual(o.Crew.Order())) args["assignedMembershipIds"] = Ids(u.Crew);
                    if (args.Count == 1) return new(c.Label, c.Kind, PushStatus.Done, "Nothing to change.");

                    await jobTread.QueryAsync("schedule-update", new JsonObject { ["r"] = new JsonObject { ["_"] = "updateTask", ["$"] = args } }, ct);
                    return new(c.Label, c.Kind, PushStatus.Done, "Updated.");
                }
                case "create" when c.JobId is not null && c.Updated is { } n:
                {
                    var args = new JsonObject
                    {
                        ["targetType"] = "job",
                        ["targetId"] = c.JobId,
                        ["name"] = string.IsNullOrWhiteSpace(n.Name) ? "New task" : n.Name,
                        ["startDate"] = n.StartDate,
                        ["endDate"] = n.EndDate ?? n.StartDate,
                        ["startTime"] = Nullable(n.StartTime),
                        ["endTime"] = Nullable(n.EndTime),
                        ["assignedMembershipIds"] = Ids(n.Crew),
                    };
                    if (c.ParentTaskId is not null) args["parentTaskId"] = c.ParentTaskId;
                    if (c.TaskTypeId is not null) args["taskTypeId"] = c.TaskTypeId;
                    var r = await jobTread.QueryAsync("schedule-create", new JsonObject { ["r"] = new JsonObject { ["_"] = "createTask", ["$"] = args, ["createdTask"] = new JsonObject { ["id"] = new JsonObject() } } }, ct);
                    return r["r"]?["createdTask"].Str("id") is not null
                        ? new(c.Label, c.Kind, PushStatus.Done, "Added.")
                        : new(c.Label, c.Kind, PushStatus.Failed, "JobTread didn't confirm the new task.");
                }
                case "delete" when c.TaskId is not null && c.Original is not null:
                {
                    if (!current.TryGetValue(c.TaskId, out var now)) return new(c.Label, c.Kind, PushStatus.Done, "Already gone from JobTread.");
                    if (!now.SameAs(c.Original)) return new(c.Label, c.Kind, PushStatus.Conflict, "Someone changed this task in JobTread after you loaded the schedule, so it wasn't deleted.");
                    try
                    {
                        await jobTread.QueryAsync("schedule-delete", new JsonObject { ["r"] = new JsonObject { ["_"] = "deleteTask", ["$"] = new JsonObject { ["id"] = c.TaskId } } }, ct);
                        return new(c.Label, c.Kind, PushStatus.Done, "Deleted.");
                    }
                    catch (JobTreadException e) when (IsNotAllowed(e) && c.Updated is { } f)
                    {
                        // JobTread's key isn't allowed to delete: mark it instead so someone can delete it in JobTread.
                        var args = new JsonObject
                        {
                            ["id"] = c.TaskId, ["name"] = f.Name, ["startDate"] = f.StartDate, ["endDate"] = f.EndDate,
                            ["assignedMembershipIds"] = Ids(f.Crew), ["notify"] = false,
                        };
                        await jobTread.QueryAsync("schedule-mark-delete", new JsonObject { ["r"] = new JsonObject { ["_"] = "updateTask", ["$"] = args } }, ct);
                        return new(c.Label, c.Kind, PushStatus.Done, "JobTread didn't allow deleting, so it was renamed DELETE and moved to Sunday for someone to delete in JobTread.");
                    }
                }
                default:
                    return new(c.Label, c.Kind, PushStatus.Failed, "This change was incomplete and wasn't sent.");
            }
        }
        catch (JobTreadException e)
        {
            var message = IsNotAllowed(e)
                ? $"JobTread's key for the dashboard isn't allowed to {c.Kind} tasks. Allow it in JobTread (grant settings) and try again."
                : e.Message;
            return new(c.Label, c.Kind, PushStatus.Failed, message);
        }
    }

    /// <summary>Current JobTread values for the tasks being changed, 25 at a time.</summary>
    private async Task<Dictionary<string, TaskFields>> LoadCurrentAsync(List<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, TaskFields>();
        foreach (var chunk in ids.Chunk(25))
        {
            var where = new JsonArray("id", "in", Ids(chunk));
            var query = new JsonObject
            {
                ["organization"] = new JsonObject
                {
                    ["$"] = new JsonObject { ["id"] = JobTreadIds.OrganizationId },
                    ["tasks"] = new JsonObject
                    {
                        ["$"] = new JsonObject { ["where"] = where, ["size"] = 25 },
                        ["nodes"] = JsonNode.Parse("""{ "id": {}, "name": {}, "startDate": {}, "endDate": {}, "startTime": {}, "endTime": {}, "assignedMemberships": { "$": { "size": 10 }, "nodes": { "id": {} } } }"""),
                    },
                },
            };
            var nodes = (await jobTread.QueryAsync("schedule-current", query, ct))["organization"]?["tasks"].Nodes() ?? [];
            foreach (var t in nodes)
            {
                if (t.Str("id") is not { } id) continue;
                result[id] = new TaskFields
                {
                    Name = t.Str("name"),
                    StartDate = t.Str("startDate"),
                    EndDate = t.Str("endDate"),
                    StartTime = Hm(t.Str("startTime")),
                    EndTime = Hm(t.Str("endTime")),
                    Crew = t["assignedMemberships"].Nodes().Select(m => m.Str("id")).OfType<string>().ToList(),
                };
            }
        }
        return result;
    }

    private static bool IsNotAllowed(JobTreadException e) =>
        e.Message.Contains("does not allow", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("requires the", StringComparison.OrdinalIgnoreCase);

    /// <summary>"07:00:00.000" → "07:00".</summary>
    public static string? Hm(string? t) => string.IsNullOrEmpty(t) ? null : t.Length >= 5 ? t[..5] : t;

    private static JsonNode? Nullable(string? s) => string.IsNullOrEmpty(s) ? null : JsonValue.Create(s);
    private static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(i => (JsonNode)i!).ToArray());
}
