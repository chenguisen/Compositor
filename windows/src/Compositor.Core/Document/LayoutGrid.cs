namespace Compositor.Core.Document;

/// <summary>
/// Where a grid's lines fall on the screen: the vertical ones are x positions, the horizontal ones y, in the
/// same screen units the canvas is drawn in.
/// </summary>
public sealed record GridLines(
    double[] VerticalFine,
    double[] VerticalMajor,
    double[] HorizontalFine,
    double[] HorizontalMajor);

/// <summary>
/// The non-printing layout grid: a major line every <see cref="Spacing"/> pixels, each square split into
/// <see cref="Subdivisions"/> by the finer ones. It is a view setting, so it is not saved with the project —
/// as the Mac build does not save it either.
/// </summary>
public sealed record LayoutGrid(int Spacing = 64, int Subdivisions = 8)
{
    public const int LeastSpacing = 2;
    public const int MostSpacing = 4096;
    public const int LeastSubdivisions = 1;
    public const int MostSubdivisions = 64;

    /// <summary>Pixels between major lines.</summary>
    public int Spacing { get; init; } = ClampSpacing(Spacing);

    /// <summary>Parts each major square is split into; never finer than a pixel.</summary>
    public int Subdivisions { get; init; } = ClampSubdivisions(Spacing, Subdivisions);

    /// <summary>Pixels between the fine lines, which is the major spacing split up.</summary>
    public double Step => (double)Spacing / Subdivisions;

    private static int ClampSpacing(int spacing) => Math.Clamp(spacing, LeastSpacing, MostSpacing);

    // The spacing is clamped here too, rather than read from the property above: a record's initializers see
    // the arguments, so an out-of-range spacing would otherwise leave a range the subdivisions cannot fit in.
    private static int ClampSubdivisions(int spacing, int subdivisions) =>
        Math.Clamp(subdivisions, LeastSubdivisions, Math.Min(MostSubdivisions, ClampSpacing(spacing)));

    /// <summary>
    /// Where the lines fall at <paramref name="zoom"/>, with <paramref name="originX"/>/<paramref name="originY"/>
    /// the document point in the view's top-left corner. They are counted out from the document's own origin,
    /// so they stay put as the view moves over the picture.
    /// </summary>
    public GridLines Lines(int documentWidth, int documentHeight, double zoom, double originX, double originY)
    {
        var (verticalFine, verticalMajor) = Along(documentWidth, zoom, originX);
        var (horizontalFine, horizontalMajor) = Along(documentHeight, zoom, originY);
        return new GridLines(verticalFine, verticalMajor, horizontalFine, horizontalMajor);
    }

    /// <summary>Whether the line this far along the document is a major one.</summary>
    public bool IsMajor(double documentPosition) => Math.Abs(documentPosition % Spacing) < 1e-9;

    private (double[] Fine, double[] Major) Along(int extent, double zoom, double origin)
    {
        var fine = new List<double>();
        var major = new List<double>();
        if (zoom <= 0 || extent <= 0) return ([], []);
        // In document units first, so a line is told apart before it is placed rather than after.
        for (var at = Math.Floor(origin / Step) * Step; at <= extent; at += Step)
        {
            var screen = (at - origin) * zoom;
            if (IsMajor(at))
            {
                major.Add(screen);
            }
            else
            {
                fine.Add(screen);
            }
        }
        return ([.. fine], [.. major]);
    }
}
