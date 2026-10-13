using System.Text.Json;

namespace PurpleDashboard.Modules.Hiring;

/// <summary>
/// The one-page Interview Questions sheet (Painters folder, Interview Questions.pdf), filled in on a phone or
/// tablet. Stored as JSON on the candidate (HiringCandidate.SheetJson). Yes/No answers are null until answered.
/// </summary>
public sealed class InterviewSheet
{
    public static readonly string[] YearsOptions = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10+"];
    public static readonly string[] SkillOptions = ["Painting", "Staining", "Spraying"];
    public static readonly string[] TransportOptions = ["Truck", "Car", "None"];

    public DateOnly? Date { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    /// <summary>Where they live (city).</summary>
    public string? Location { get; set; }
    public string? Position { get; set; }
    public bool? Bilingual { get; set; }
    public string? DesiredPay { get; set; }
    /// <summary>The rate they'll start at for the 90-day probation.</summary>
    public string? StartRate { get; set; }
    public string? Years { get; set; }
    public string? Companies { get; set; }
    public List<string> Skills { get; set; } = [];
    public string? Transportation { get; set; }
    public bool? BasicTools { get; set; }
    public bool? PayrollOk { get; set; }
    public bool? Felony { get; set; }
    public bool? PartTimeOk { get; set; }
    public string? Hobbies { get; set; }
    public bool? EverFired { get; set; }
    public string? FiredWhy { get; set; }

    // Filled in by us
    public string? Interviewer { get; set; }
    public int? Rating { get; set; }
    public string? Notes { get; set; }
    /// <summary>Keys from HiringLists.PointsFor(position) that came up.</summary>
    public List<string> Points { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static InterviewSheet? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<InterviewSheet>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static string YesNo(bool? v) => v switch { true => "Yes", false => "No", null => "—" };
}
