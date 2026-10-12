using System.Globalization;

namespace PurpleDashboard.Modules.Projects;

/// <summary>
/// Profit % color: a smooth blend between stops. 40%+ purple, 20% green, 10% yellow,
/// 5% and below orange, negative red. Colors blend around the color wheel (hue), so
/// purple → green passes through blue and teal instead of turning gray.
/// Edit the stops here to change the scale. Lightness is kept dark enough to read
/// as text on its own light tint.
/// </summary>
public static class ProfitScale
{
    // (percent, hue°, saturation %, lightness %)
    private static readonly (decimal At, double H, double S, double L)[] Stops =
    [
        (40, 272, 55, 41), // purple
        (20, 142, 60, 30), // green
        (10, 45,  100, 30), // yellow (dark gold so it's readable)
        (5,  20,  88, 40), // orange
    ];
    private static readonly (double H, double S, double L) Negative = (0, 74, 42); // red
    private static readonly (double H, double S, double L) None = (270, 10, 38);   // muted gray, no contract

    /// <summary>Inline CSS for the profit pill: text color plus a light tint of it behind.</summary>
    public static string Style(decimal? percent)
    {
        var (h, s, l) = ColorFor(percent);
        return string.Create(CultureInfo.InvariantCulture,
            $"color: hsl({h:0} {s:0}% {l:0}%); background: hsl({h:0} {s:0}% {l:0}% / 0.12);");
    }

    public static (double H, double S, double L) ColorFor(decimal? percent)
    {
        if (percent is null) return None;
        var p = percent.Value;
        if (p < 0) return Negative;
        if (p >= Stops[0].At) return (Stops[0].H, Stops[0].S, Stops[0].L);
        if (p <= Stops[^1].At) return (Stops[^1].H, Stops[^1].S, Stops[^1].L);

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
