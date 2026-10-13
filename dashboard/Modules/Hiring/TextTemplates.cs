using System.Globalization;
using System.Text;

namespace PurpleDashboard.Modules.Hiring;

/// <summary>What a text is filled in with: the candidate, their slot (or the next interview day) and the trial day.</summary>
public sealed record TextContext(
    string Position, string Language,
    DateOnly? Date, TimeOnly? Time, TimeOnly Start, TimeOnly End, string Location,
    DateOnly? TrialDate = null, string? TrialSupervisor = null);

/// <summary>A filled-in text, ready to copy and paste into Google Voice.</summary>
public sealed record TextMessage(string Key, string Label, string When, string Body);

/// <summary>
/// The copy-paste texts from the Interviews Template spreadsheet, in English and Spanish, filled in with the
/// day's date, time and place. Candidates get their own language; "Both" gets English then Spanish in one message.
/// Kept in code for now (not editable on the page). Texts go out from Google Voice, which doesn't open from
/// sms: links, so pages offer Copy plus an "Open Google Voice" link.
/// </summary>
public static class TextTemplates
{
    private sealed record Template(string Key, string Label, string When, Func<TextContext, string> En, Func<TextContext, string> Es, Func<TextContext, bool>? Applies = null);

    private static readonly Template[] All =
    [
        new("invite", "Invite", "First reply to an ad response",
            c => $"Hello! This is Purple Painting and we will be conducting interviews for {PositionPluralEn(c.Position)} on {DateEn(c.Date)}. Please let me know if you would like to set up an interview.",
            c => $"¡Hola! Le escribe Purple Painting. Vamos a tener entrevistas para {PositionPluralEs(c.Position)} el {DateEs(c.Date)}. Por favor avíseme si le gustaría agendar una entrevista."),
        new("position", "Position", "Screening",
            c => "Which position are you applying for? Painter, helper or foreman?",
            c => "¿Para qué puesto está aplicando? ¿Pintor, ayudante o capataz?",
            c => HiringLists.IsPainting(c.Position)),
        new("times", "Times", "Screening",
            c => $"We have time slots available from {TimeEn(c.Start)} to {TimeEn(c.End)}. What time works well for you?",
            c => $"Tenemos horarios disponibles de {TimeEs(c.Start)} a {TimeEs(c.End)}. ¿Qué hora le queda bien?"),
        new("location", "Location", "Once they pick a time",
            c => (c.Time is { } t ? $"Your interview is on {DateEn(c.Date)} at {TimeEn(t)}. " : "") + $"We will be meeting out on the patio and the address is: {c.Location}",
            c => (c.Time is { } t ? Sentence($"Su entrevista es el {DateEs(c.Date)} a las {TimeEs(t)}") : "") + $"Nos vamos a reunir en el patio y la dirección es: {c.Location}"),
        new("expectations", "Expectations", "Right after booking",
            c => "I will send a reminder text the day before and a confirmation text the morning of. Please be sure to reply to both texts to avoid being taken off the schedule.",
            c => "Le voy a mandar un mensaje de recordatorio un día antes y un mensaje de confirmación la mañana de la entrevista. Por favor asegúrese de contestar los dos mensajes para que no lo quitemos del horario."),
        new("reminder", "Reminder", "Day before",
            c => $"Hi, this is Purple Painting with a reminder about your interview tomorrow, {DateEn(c.Date)}{(c.Time is { } t ? " at " + TimeEn(t) : "")}, at {c.Location}. Please reply to confirm.",
            c => $"Hola, le escribe Purple Painting para recordarle de su entrevista mañana, {DateEs(c.Date)}{(c.Time is { } t ? " a las " + TimeEs(t) : "")}, en {c.Location}. Por favor conteste para confirmar."),
        new("dayof", "Day-of confirmation", "Interview day, around 9am",
            c => $"Will you please confirm your interview for the {PositionEn(c.Position)} position today at {(c.Time is { } t ? TimeEn(t) : "[time]")}?",
            c => $"¿Me puede confirmar su entrevista para el puesto de {PositionEs(c.Position)} hoy a las {(c.Time is { } t ? TimeEs(t) : "[hora]")}?"),
        new("supervisor", "Jobsite Supervisor invite", "Supervisor candidates",
            c => $"Hello! This is Purple Painting and we will be conducting interviews for a Jobsite Supervisor position on {DateEn(c.Date)}. Please let me know if you would like to set up an interview.",
            c => $"¡Hola! Le escribe Purple Painting. Vamos a tener entrevistas para el puesto de supervisor de obra el {DateEs(c.Date)}. Por favor avíseme si le gustaría agendar una entrevista.",
            c => c.Position == "foreman"),
        new("trial", "Trial day invite", "Invited to a trial day",
            c => $"Hi, this is Purple Painting. Thank you for interviewing with us! We would like to invite you to a paid trial day on {DateEn(c.TrialDate)}."
                 + (string.IsNullOrWhiteSpace(c.TrialSupervisor) ? "" : $" Your jobsite supervisor will be {c.TrialSupervisor}.")
                 + " We will text you the jobsite address and start time. If hired you will need: " + string.Join(", ", HiringLists.TrialRequirements.Select(r => Lower(r.En))) + ".",
            c => $"Hola, le escribe Purple Painting. ¡Gracias por su entrevista! Nos gustaría invitarle a un día de prueba pagado el {DateEs(c.TrialDate)}."
                 + (string.IsNullOrWhiteSpace(c.TrialSupervisor) ? "" : $" Su supervisor de obra será {c.TrialSupervisor}.")
                 + " Le vamos a mandar la dirección de la obra y la hora de entrada. Si lo contratamos va a necesitar: " + string.Join(", ", HiringLists.TrialRequirements.Select(r => Lower(r.Es))) + "."),
        new("tools", "Required tools", "Before the trial day",
            c => ToolsText(c.Position, es: false),
            c => ToolsText(c.Position, es: true),
            c => HiringLists.IsPainting(c.Position)),
        new("notnow", "Not this time", "Optional, if they didn't get it",
            c => "Hi, this is Purple Painting. Thank you for taking the time to interview with us. We won't be moving forward at this time, but we will keep your information on file for future openings. We wish you the best!",
            c => "Hola, le escribe Purple Painting. Gracias por tomarse el tiempo de hacer la entrevista con nosotros. Por ahora no vamos a seguir adelante, pero vamos a guardar su información para futuras oportunidades. ¡Le deseamos lo mejor!"),
    ];

    /// <summary>Every text that applies to this candidate, in their language.</summary>
    public static IReadOnlyList<TextMessage> For(TextContext c) =>
        All.Where(t => t.Applies?.Invoke(c) ?? true).Select(t => Build(t, c, c.Language)).ToList();

    /// <summary>One text in a given language ("en", "es" or "both"), for the day's copy panel.</summary>
    public static TextMessage One(string key, TextContext c, string language) => Build(All.First(t => t.Key == key), c, language);

    public const string GoogleVoiceUrl = "https://voice.google.com/u/0/messages";

    /// <summary>Texts that don't depend on the candidate (for the day page's "texts for this day" panel).</summary>
    public static readonly string[] DayKeys = ["invite", "position", "times", "location", "expectations", "supervisor"];

    private static TextMessage Build(Template t, TextContext c, string language)
    {
        var body = language switch
        {
            "es" => t.Es(c),
            "both" => t.En(c) + "\n\n" + t.Es(c),
            _ => t.En(c),
        };
        return new TextMessage(t.Key, t.Label, t.When, body);
    }

    /// <summary>"(805) 555-1234" for 10-digit US numbers, otherwise as typed.</summary>
    public static string? PrettyPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        return digits.Length == 10 ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : phone.Trim();
    }

    public static string ToolsText(string? position, bool es)
    {
        var levels = HiringLists.ToolsFor(position);
        if (levels.Count == 0) return "";
        var label = HiringLists.Position(position);
        var sb = new StringBuilder(es
            ? $"Herramientas requeridas para {label.Spanish}:"
            : $"Required tools for {label.Label}:");
        foreach (var (_, tools) in levels)
            foreach (var tool in tools) sb.Append("\n- ").Append(es ? tool.Es : tool.En);
        return sb.ToString();
    }

    // ---- Wording ----

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    // Spanish names are spelled out so the text never depends on the server having Spanish culture data.
    private static readonly string[] DaysEs = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];
    private static readonly string[] MonthsEs = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>"Wednesday, October 14".</summary>
    public static string DateEn(DateOnly? d) => d is { } x ? x.ToString("dddd, MMMM d", Us) : "[date]";
    /// <summary>"miércoles 14 de octubre".</summary>
    public static string DateEs(DateOnly? d) => d is { } x ? $"{DaysEs[(int)x.DayOfWeek]} {x.Day} de {MonthsEs[x.Month - 1]}" : "[fecha]";
    /// <summary>"11am", "11:15am".</summary>
    public static string TimeEn(TimeOnly t) => t.ToString(t.Minute == 0 ? "htt" : "h:mmtt", Us).ToLowerInvariant();
    /// <summary>"11 a.m.", "11:15 a.m.".</summary>
    public static string TimeEs(TimeOnly t) => t.ToString(t.Minute == 0 ? "%h" : "h:mm", Us) + (t.Hour < 12 ? " a.m." : " p.m.");

    private static string PositionEn(string? p) => p == "sales" ? "Sales Development Rep" : HiringLists.Position(p).Label.ToLowerInvariant();
    private static string PositionEs(string? p) => p == "sales" ? "representante de desarrollo de ventas" : HiringLists.Position(p).Spanish;
    private static string PositionPluralEn(string? p) => p == "sales" ? "a Sales Development Rep position" : "painters";
    private static string PositionPluralEs(string? p) => p == "sales" ? "un puesto de representante de desarrollo de ventas" : "pintores";
    /// <summary>Ends a sentence without doubling the dot after "p.m.".</summary>
    private static string Sentence(string s) => s.EndsWith('.') ? s + " " : s + ". ";
    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
