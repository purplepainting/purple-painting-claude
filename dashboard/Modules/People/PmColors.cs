namespace PurpleDashboard.Modules.People;

/// <summary>A color per project manager (settings: ProjectManagers:Colors). Unlisted PMs get gray.</summary>
public sealed class PmColorOptions
{
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string For(string? pm) => pm is not null && Colors.TryGetValue(pm, out var c) ? c : "#5E566B";

    /// <summary>Inline style for a PM tag: colored text on a light tint of the same color.</summary>
    public string Style(string? pm)
    {
        var c = For(pm);
        return $"color:{c};background:color-mix(in srgb, {c} 12%, transparent);border-color:color-mix(in srgb, {c} 35%, transparent)";
    }
}
