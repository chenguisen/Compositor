using Compositor.Core.Format;
using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>Unrotated bounds in document pixels; rotation is clockwise around their center.</summary>
public readonly record struct LayerTransform(
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation = 0,
    bool FlipX = false,
    bool FlipY = false,
    LayerSampling Sampling = LayerSampling.HighQuality)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public double Radians => Rotation % 360 * Math.PI / 180;

    /// <summary>The same place on the document, whatever the sampling.</summary>
    public bool SamePlacement(LayerTransform other) => this with { Sampling = other.Sampling } == other;

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && double.IsFinite(Rotation)
        && Width is >= 1 and <= 300_000 && Height is >= 1 and <= 300_000
        && Math.Abs(X) <= 1_000_000 && Math.Abs(Y) <= 1_000_000;

    /// <summary>Both sides set to `percent` of `pixelSize`, keeping the center (and rotation and flips).</summary>
    public LayerTransform ScaledToPercent(double percent, double pixelWidth, double pixelHeight)
    {
        var width = pixelWidth * percent / 100;
        var height = pixelHeight * percent / 100;
        return this with { Width = width, Height = height, X = CenterX - width / 2, Y = CenterY - height / 2 };
    }

    /// <summary>
    /// A point in the box, given in units of its width and height — (0, 0) is the top left of the unrotated
    /// box and (1, 1) the bottom right — placed on the document, so it turns with the box.
    /// </summary>
    public SKPoint Point(double unitX, double unitY)
    {
        var x = (unitX - 0.5) * Width;
        var y = (unitY - 0.5) * Height;
        var cos = Math.Cos(Radians);
        var sin = Math.Sin(Radians);
        return new SKPoint((float)(CenterX + x * cos - y * sin), (float)(CenterY + x * sin + y * cos));
    }

    /// <summary>Whether a document point is inside the box.</summary>
    public bool Contains(SKPoint point)
    {
        var x = point.X - CenterX;
        var y = point.Y - CenterY;
        var cos = Math.Cos(Radians);
        var sin = Math.Sin(Radians);
        return Math.Abs(x * cos + y * sin) <= Width / 2 && Math.Abs(-x * sin + y * cos) <= Height / 2;
    }

    /// <summary>
    /// Where a document point falls in the box's own pixels — (0, 0) is the box's top left, so the point's
    /// place is the pixel to read — or null when it is outside the box. This is the way back through what the
    /// drawing does: the turn is undone, then the mirror, so a flipped layer reads the pixel that is shown
    /// there rather than the one opposite it.
    /// </summary>
    public (double X, double Y)? InBox(SKPoint point)
    {
        var x = point.X - CenterX;
        var y = point.Y - CenterY;
        var cos = Math.Cos(Radians);
        var sin = Math.Sin(Radians);
        var across = (x * cos + y * sin) * (FlipX ? -1 : 1) + Width / 2;
        var down = (-x * sin + y * cos) * (FlipY ? -1 : 1) + Height / 2;
        if (across < 0 || across > Width || down < 0 || down > Height) return null;
        return (across, down);
    }

    /// <summary>
    /// This placement mirrored across a vertical line at <paramref name="axis"/> — or, when
    /// <paramref name="horizontally"/> is false, a horizontal one: the picture flips, its angle turns the
    /// other way, and its middle crosses to the other side of the line.
    /// </summary>
    public LayerTransform Mirrored(bool horizontally, double axis) => horizontally
        ? this with { FlipX = !FlipX, X = 2 * axis - CenterX - Width / 2, Rotation = -Rotation }
        : this with { FlipY = !FlipY, Y = 2 * axis - CenterY - Height / 2, Rotation = -Rotation };
}
