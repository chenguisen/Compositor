namespace Compositor.Core.Document;

/// <summary>How long a ruler's tick is: the numbered ones, the half-way ones, and the rest.</summary>
public enum RulerTickKind
{
    Minor,
    Mid,
    Major,
}

/// <summary>A place along a ruler, in document pixels, and how long its tick is.</summary>
public readonly record struct RulerTick(double Value, RulerTickKind Kind);

/// <summary>
/// Where a ruler's ticks fall. A ruler is numbered about every 70 points whatever the zoom, so the numbers
/// move and the steps between them change as the view is zoomed: the steps are the round ones a person reads
/// without thinking — 1, 2, 5, 10, 20, 25, 50 and on up — which is what the Mac build's rulers use.
/// </summary>
public static class RulerScale
{
    /// <summary>How far apart the numbered ticks are wanted, in the view's own points.</summary>
    public const double PointsPerStep = 70;

    /// <summary>How many of the fine ticks make up one numbered step.</summary>
    public const int MinorPerStep = 10;

    private static readonly double[] Round =
    [
        1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1_000, 2_000, 2_500, 5_000, 10_000, 20_000, 25_000, 50_000,
    ];

    /// <summary>The document distance between numbered ticks: the first round step at least 70 points long.</summary>
    public static double MajorStep(double pointsPerPixel)
    {
        var wanted = PointsPerStep / Math.Max(pointsPerPixel, 0.0001);
        foreach (var step in Round)
        {
            if (step >= wanted) return step;
        }
        return Round[^1];
    }

    /// <summary>A tick's number, as a ruler writes it: whole pixels, and nothing after the zero.</summary>
    public static string Label(double value)
    {
        var rounded = Math.Round(value);
        return rounded == 0 ? "0" : rounded.ToString("0");
    }

    /// <summary>
    /// The ticks between two places on the document, in order and including both ends when they fall on one.
    /// A view so far out that a tick would be invisible gives nothing back rather than a list of millions.
    /// </summary>
    public static List<RulerTick> Between(double from, double to, double pointsPerPixel)
    {
        var ticks = new List<RulerTick>();
        if (!double.IsFinite(from) || !double.IsFinite(to)) return ticks;
        var step = MajorStep(pointsPerPixel);
        var minor = step / MinorPerStep;
        if (!(minor > 0)) return ticks;
        var first = Math.Floor(Math.Min(from, to) / minor) * minor;
        var last = Math.Ceiling(Math.Max(from, to) / minor) * minor;
        if (!double.IsFinite(first) || !double.IsFinite(last)) return ticks;
        if ((last - first) / minor > 10_000) return ticks;
        var half = minor * MinorPerStep / 2;
        for (var value = first; value <= last + 1e-6; value += minor)
        {
            ticks.Add(new RulerTick(value, Kind(value, step, half, minor)));
        }
        return ticks;
    }

    /// <summary>
    /// How long a tick is. A step and its multiples are numbered; half-way along is the mid tick. The test is
    /// against the nearest multiple rather than the remainder, so a tick that lands a hair out from rounding
    /// still counts as being on the step.
    /// </summary>
    private static RulerTickKind Kind(double value, double step, double half, double minor)
    {
        var tolerance = minor / 100;
        if (Math.Abs(value - Math.Round(value / step) * step) < tolerance) return RulerTickKind.Major;
        return Math.Abs(value - Math.Round(value / half) * half) < tolerance ? RulerTickKind.Mid : RulerTickKind.Minor;
    }
}
