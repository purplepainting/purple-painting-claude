using System.Globalization;

namespace PurpleDashboard.Modules.Projects;

/// <summary>
/// Profit % color: one purple scale. Darker purple = more profit, lighter purple = less;
/// below 10% it shifts through pink to red at 0% and below (purple → red is a short hop on
/// the color wheel). The pill is filled with the color; text flips white/dark so it stays
/// readable. Edit the stops here to change the scale.
/// </summary>
public static class ProfitScale
{
    // (percent, hue°, saturation %, lightness %), highest profit first
    private static readonly (decimal At, double H, double S, double L)[] Stops =
    [
        (40, 272, 66, 25), // deep purple
        (20, 272, 45, 50), // mid purple
        (10, 280, 55, 80), // light lavender
        (5,  330, 60, 74), // pink
        (0,  355, 70, 55), // red-pink
    ];
    private static readonly (double H, double S, double L) Negative = (0, 74, 42); // red
    private static readonly (double H, double S, double L) None = (270, 15, 90);   // pale gray, no contract

    /// <summary>Inline CSS for the profit pill: filled background, readable text.</summary>
    public static string Style(decimal? percent)
    {
        var (h, s, l) = ColorFor(percent);
        var text = l < 60 ? "#FFFFFF" : "#2E1248";
        return string.Create(CultureInfo.InvariantCulture, $"background: hsl({h:0} {s:0}% {l:0}%); color: {text};");
    }

    public static (double H, double S, double L) ColorFor(decimal? percent)
    {
        if (percent is null) return None;
        var p = percent.Value;
        if (p < 0) return Negative;
        if (p >= Stops[0].At) return (Stops[0].H, Stops[0].S, Stops[0].L);

        for (var i = 0; i < Stops.Length - 1; i++)
        {
            var hi = Stops[i];
            var lo = Stops[i + 1];
            if (p <= hi.At && p >= lo.At)
            {
                var t = (double)((p - lo.At) / (hi.At - lo.At)); // 0 at the lower stop, 1 at the upper
                return (lo.H + (hi.H - lo.H) * t, lo.S + (hi.S - lo.S) * t, lo.L + (hi.L - lo.L) * t);
            }
        }
        return (Stops[^1].H, Stops[^1].S, Stops[^1].L);
    }
}
