using PurpleDashboard.Modules.People;

namespace PurpleDashboard.Modules.Changes;

/// <summary>
/// Turns the email a change was logged under into a person's name. In order: the signed-in user's own
/// name, the JobTread name of anyone who linked JobTread on Connections, then a JobTread staff member whose
/// name fits the email ("kareem.smith@", "ksmith@", or a first name only one person has). Otherwise the email.
/// </summary>
public sealed class PersonNames
{
    private readonly Dictionary<string, string> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> byKey = new(StringComparer.Ordinal);

    public PersonNames(string? myEmail, string? myName, IReadOnlyDictionary<string, string> linked, IReadOnlyList<StaffMember> staff)
    {
        foreach (var (email, name) in linked) known.TryAdd(email, name);
        if (myEmail is not null && myName is not null && myName != myEmail) known[myEmail] = myName;

        foreach (var s in staff)
        {
            var parts = s.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Letters).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) continue;
            Add(string.Concat(parts), s.Name);                              // kareemsmith, kareem.smith
            if (parts.Length > 1)
            {
                Add(parts[0][0] + parts[^1], s.Name);                       // ksmith
                Add(parts[0] + parts[^1][0], s.Name);                       // kareems
            }
            Add(parts[0], s.Name);                                          // kareem (only if unique)
        }
    }

    /// <summary>Display name for an email (or whatever was logged as the user).</summary>
    public string Name(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return "Unknown";
        if (known.TryGetValue(user, out var name)) return name;
        var at = user.IndexOf('@');
        if (at <= 0) return user;   // already a display name
        return byKey.TryGetValue(Letters(user[..at]), out var match) && match.Length > 0 ? match : user;
    }

    /// <summary>A key two people share maps to nobody (empty), so a guess is never the wrong person.</summary>
    private void Add(string key, string name)
    {
        if (key.Length < 3) return;
        if (byKey.TryGetValue(key, out var existing)) { if (existing != name) byKey[key] = ""; }
        else byKey[key] = name;
    }

    private static string Letters(string s) => new(s.ToLowerInvariant().Where(char.IsLetter).ToArray());
}
