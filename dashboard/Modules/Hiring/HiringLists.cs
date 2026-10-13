namespace PurpleDashboard.Modules.Hiring;

/// <summary>A hiring stage. Css is the chip class (good / warn / bad / active / "" for the default purple).</summary>
public sealed record HiringStage(string Key, string Label, string Css, bool Open);

/// <summary>
/// The hiring pipeline, in order: texted → booked → confirmed → interviewed → trial day → hired,
/// plus the three ways out (not hired, no-show, withdrew). From "How We Run Hiring Interviews" (Oct 2026).
/// </summary>
public static class HiringStages
{
    public const string Texted = "texted", Booked = "booked", Confirmed = "confirmed", Interviewed = "interviewed",
        Trial = "trial", Hired = "hired", NotHired = "not_hired", NoShow = "no_show", Withdrew = "withdrew",
        Past = "past";

    public static readonly HiringStage[] All =
    [
        new(Texted, "Texted", "", true),
        new(Booked, "Booked", "", true),
        new(Confirmed, "Confirmed", "active", true),
        new(Interviewed, "Interviewed", "active", true),
        new(Trial, "Trial day", "warn", true),
        new(Hired, "Hired", "good", false),
        new(NotHired, "Not hired", "bad", false),
        new(NoShow, "No-show", "bad", false),
        new(Withdrew, "Withdrew", "bad", false),
        // Imported from the old Interviews spreadsheets (2023 to 2025): we don't know how each one ended.
        new(Past, "Past applicant", "", false),
    ];

    public static HiringStage Get(string? key) => All.FirstOrDefault(s => s.Key == key) ?? All[0];
    public static string Label(string? key) => Get(key).Label;
    public static bool IsValid(string? key) => All.Any(s => s.Key == key);

    /// <summary>Position in the main line (texted 0 … hired 5); the ways out sort after.</summary>
    public static int Rank(string? key) => Array.FindIndex(All, s => s.Key == key) is var i and >= 0 ? i : 0;

    /// <summary>True when moving to <paramref name="to"/> is a step forward along the main line.</summary>
    public static bool IsBefore(string? current, string to) => Get(current).Open && Rank(current) < Rank(to);
}

public sealed record HiringPosition(string Key, string Label, string Spanish);

public static class HiringLists
{
    public static readonly HiringPosition[] Positions =
    [
        new("helper", "Helper", "ayudante"),
        new("painter", "Painter", "pintor"),
        new("foreman", "Foreman", "capataz"),
        new("sales", "Sales Rep", "representante de ventas"),
    ];

    public static HiringPosition Position(string? key) => Positions.FirstOrDefault(p => p.Key == key) ?? Positions[1];
    public static bool IsPainting(string? position) => position is "helper" or "painter" or "foreman";

    public static readonly (string Key, string Label, string Short)[] Languages = [("en", "English", "ENG"), ("es", "Spanish", "ESP"), ("both", "Both", "ENG/ESP")];
    public static string LanguageLabel(string? key) => Languages.FirstOrDefault(l => l.Key == key).Label ?? "English";
    public static string LanguageShort(string? key) => Languages.FirstOrDefault(l => l.Key == key).Short ?? "ENG";
    /// <summary>The sheet's "ENG (Y/N)" column.</summary>
    public static bool SpeaksEnglish(string? language) => language is "en" or "both";

    public static readonly string[] Sources = ["Craigslist", "Indeed", "Referral", "Other"];

    public const string DefaultLocation = "Topa Topa Brewing patio, 120 Santa Barbara St, Santa Barbara, CA 93101";
    public static readonly TimeOnly DefaultStart = new(11, 0), DefaultEnd = new(14, 0);
    public const int DefaultSlotMinutes = 15;
    /// <summary>After this time on interview day, anyone not confirmed is flagged to come off the schedule.</summary>
    public static readonly TimeOnly ConfirmBy = new(10, 0);

    /// <summary>Points to make sure come up in a painter interview.</summary>
    public static readonly (string Key, string Label)[] PainterPoints =
    [
        ("payroll", "The job is payroll only"),
        ("probation", "Starts at a probation rate for 90 days"),
        ("benefits", "Sick pay and holiday pay start after probation"),
        ("tools", "Needs the tools for their position (list on the Trial Day Info sheet)"),
    ];

    /// <summary>Sales Development Rep talking points (Sales Development Representative Interview Points).</summary>
    public static readonly (string Key, string Label)[] SalesPoints =
    [
        ("commission", "Commission only"),
        ("trial90", "90-day trial"),
        ("firstcheck", "First paycheck likely within 60 to 90 days"),
        ("target", "Target $100K to $150K a year"),
        ("parttime", "Part-time at first"),
        ("vehicle", "Needs a reliable vehicle"),
        ("doors", "Phone and door-to-door work"),
        ("office", "Some office hours"),
        ("canva", "Canva familiarity"),
        ("nosalary", "No salary after 90 days"),
    ];

    public static (string Key, string Label)[] PointsFor(string? position) => position == "sales" ? SalesPoints : PainterPoints;

    /// <summary>What they'll need if hired (Trial Day Information sheet).</summary>
    public static readonly (string En, string Es)[] TrialRequirements =
    [
        ("A working email address", "Un correo electrónico que funcione"),
        ("A smartphone for the QuickBooks Workforce clock-in app", "Un teléfono inteligente para la aplicación QuickBooks Workforce (para registrar sus horas)"),
        ("A California mailing address", "Una dirección postal en California"),
        ("A bank account for direct deposit", "Una cuenta de banco para depósito directo"),
        ("Agreement on the 90-day probation pay rate", "Estar de acuerdo con el pago de prueba durante los primeros 90 días"),
        ("The tools for your position", "Las herramientas para su puesto"),
    ];

    /// <summary>Required tools by position; each level adds to the one below (Helper → Painter → Foreman).</summary>
    private static readonly (string En, string Es)[] HelperTools =
    [
        ("Masking machine", "Máquina para enmascarar (masker)"),
        ("Putty knife", "Espátula"),
        ("5-in-1 scraper", "Raspador 5 en 1"),
        ("Dusting chip brush", "Brocha para sacudir el polvo"),
        ("Razor blade", "Navaja (cuchilla)"),
    ];
    private static readonly (string En, string Es)[] PainterTools =
    [
        ("Brushes", "Brochas"),
        ("4ft ladder", "Escalera de 4 pies"),
        ("4x15 drop cloth", "Lona protectora de 4x15"),
        ("Respirator", "Respirador"),
        ("Caulking gun", "Pistola para calafatear (caulk)"),
        ("Wire brush", "Cepillo de alambre"),
        ("9\" roller frame", "Marco de rodillo de 9\""),
        ("6.5\" mini roller frame", "Marco de mini rodillo de 6.5\""),
        ("Extension pole", "Palo de extensión"),
        ("Pole sander", "Lijadora de palo"),
        ("Screwdriver", "Desarmador"),
    ];
    private static readonly (string En, string Es)[] ForemanTools =
    [
        ("6ft ladder", "Escalera de 6 pies"),
        ("Trowels and mud pan", "Llanas y charola para pasta"),
        ("Saw horses", "Caballetes"),
        ("Small shop vac", "Aspiradora pequeña (shop vac)"),
        ("Construction light", "Lámpara de construcción"),
        ("Drill/screw gun", "Taladro/atornillador"),
        ("Extension cord", "Extensión eléctrica"),
        ("Orbital sander", "Lijadora orbital"),
    ];

    /// <summary>The tools grouped by the level that adds them, up to the candidate's position. Empty for sales.</summary>
    public static IReadOnlyList<(string Level, (string En, string Es)[] Tools)> ToolsFor(string? position) => position switch
    {
        "helper" => [("Helper", HelperTools)],
        "painter" => [("Helper", HelperTools), ("Painter", PainterTools)],
        "foreman" => [("Helper", HelperTools), ("Painter", PainterTools), ("Foreman", ForemanTools)],
        _ => [],
    };

    /// <summary>Onboarding once hired (New Hire Packet and setup).</summary>
    public static readonly (string Key, string Label)[] Onboarding =
    [
        ("w4", "W4"),
        ("id", "ID photo"),
        ("deposit", "Direct deposit form"),
        ("probation", "Signed 90-day probation agreement"),
        ("policy", "Company policy"),
        ("shoplabor", "Onboarding time clocked to SHOP LABOR, note \"onboarding\""),
        ("tools", "Required Tools doc emailed"),
        ("jobtread", "Added to JobTread"),
        ("quickbooks", "Added to QuickBooks payroll"),
    ];

    /// <summary>SharePoint documents for hiring. The Task Manual is left out on purpose: it still has passwords in it.</summary>
    public static readonly (string Label, string Note, string Url)[] Documents =
    [
        ("Interview Questions.pdf", "The candidate interview sheet (print one per confirmed candidate)",
            "https://purplepainting.sharepoint.com/sites/purplepainting/Shared%20Documents/05%20HR/Recruiting%20-%20Applications%20%26%20Interviews/Painters/Interview%20Questions.pdf"),
        ("Trial Day Information", "Hire requirements and tool list",
            "https://purplepainting.sharepoint.com/sites/purplepainting/Shared%20Documents/05%20HR/Onboarding%20Documents/PURPLE%20PAINTING%20TRIAL%20DAY%20INFORMATION.docx"),
        ("Trial Day Timesheet", "Paper timesheet for the trial day",
            "https://purplepainting.sharepoint.com/sites/purplepainting/Shared%20Documents/05%20HR/Onboarding%20Documents/1-%20TRIAL%20DAY%20TIMESHEET.docx"),
        ("Sales Development Rep Interview Points", "Sales rep talking points",
            "https://purplepainting-my.sharepoint.com/personal/kareem_purplepainting_net/Documents/Shared%20OneDrive/Marketing/Sales%20%20Development%20Representative%20Interview%20Points.docx"),
        ("Interview Spreadsheets folder", "Past interview days, 2023 to 12/12/2025",
            "https://purplepainting.sharepoint.com/sites/purplepainting/Shared%20Documents/05%20HR/Recruiting%20-%20Applications%20%26%20Interviews/Painters/Interview%20Spreadsheets"),
    ];
}
