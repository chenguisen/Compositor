namespace Compositor.Core.Format;

/// <summary>
/// What a shape layer draws, kept so the shape can be drawn again at a new size. Its PNG is still an
/// ordinary raster; once anything else changes those pixels the metadata is dropped.
/// </summary>
public sealed class LayerShapeStyle
{
    public ShapeKind Kind { get; set; }
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }

    /// <summary>Document pixels, whatever size the shape is scaled to.</summary>
    public double CornerRadius { get; set; }

    /// <summary>A line's thickness, and its two ends as fractions of the layer's box. Nil on other shapes.</summary>
    public double? LineWidth { get; set; }

    public JsonPoint? Start { get; set; }
    public JsonPoint? End { get; set; }
}
