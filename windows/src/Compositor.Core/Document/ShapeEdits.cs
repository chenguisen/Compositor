using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>
/// Drawing a shape as a layer's pixels, and drawing it again when it is scaled — a shape layer is an
/// ordinary raster that still knows the shape it is, so a rounded corner keeps its radius at a new size
/// instead of stretching.
/// </summary>
public static class ShapeEdits
{
    /// <summary>The box a drag makes. Shift squares it; Alt grows it out from where the drag began.</summary>
    public static SKRectI Box(SKPoint anchor, SKPoint point, bool square, bool fromCentre)
    {
        var dx = Math.Round(point.X) - anchor.X;
        var dy = Math.Round(point.Y) - anchor.Y;
        if (square)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        return fromCentre
            ? SKRectI.Create((int)(anchor.X - Math.Abs(dx)), (int)(anchor.Y - Math.Abs(dy)),
                Math.Max(1, (int)(Math.Abs(dx) * 2)), Math.Max(1, (int)(Math.Abs(dy) * 2)))
            : SKRectI.Create((int)Math.Min(anchor.X, anchor.X + dx), (int)Math.Min(anchor.Y, anchor.Y + dy),
                Math.Max(1, (int)Math.Abs(dx)), Math.Max(1, (int)Math.Abs(dy)));
    }

    /// <summary>
    /// Where a line drag ends: Shift puts it on an eighth of a turn — flat, upright or at forty-five degrees —
    /// rather than squaring the box, as the Mac build does for a line.
    /// </summary>
    public static SKPoint LineEnd(SKPoint anchor, SKPoint point)
    {
        var dx = point.X - anchor.X;
        var dy = point.Y - anchor.Y;
        var eighth = Math.PI / 4;
        var angle = Math.Round(Math.Atan2(dy, dx) / eighth) * eighth;
        var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
        return new SKPoint((float)(anchor.X + Math.Cos(angle) * length), (float)(anchor.Y + Math.Sin(angle) * length));
    }

    /// <summary>Whether a box is more pixels than one shape layer may hold.</summary>
    public static bool TooLarge(int width, int height) =>
        width < 1 || height < 1 || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
        || (long)width * height > DocumentLimits.MaxSurfacePixels;

    /// <summary>
    /// The pixels a shape draws: a rectangle with its corners rounded by at most half its shorter side (so a
    /// large radius makes a pill), an ellipse, or a line stroked between the two ends the style holds as
    /// fractions of the box.
    /// </summary>
    public static SKBitmap? Image(LayerShapeStyle style, int width, int height)
    {
        if (TooLarge(width, height)) return null;
        var colour = new SKColor(
            (byte)Math.Clamp(Math.Round(style.Red * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(style.Green * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(style.Blue * 255), 0, 255));
        var image = new SKBitmap(Bitmaps.ColorInfo(width, height));
        image.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(image);
        var bounds = SKRect.Create(0, 0, width, height);
        switch (style.Kind)
        {
            case ShapeKind.Line:
            {
                // The ends sit where they were dragged; a line with none stored ran corner to corner, inset by
                // half its thickness so the stroke stays inside the layer.
                var thickness = Math.Max(1, (float)(style.LineWidth ?? 1));
                var inset = SKRect.Create(
                    Math.Min(thickness, width) / 2, Math.Min(thickness, height) / 2,
                    Math.Max(0, width - Math.Min(thickness, width)), Math.Max(0, height - Math.Min(thickness, height)));
                var from = End(style.Start, width, height, inset.Left, inset.Top);
                var to = End(style.End, width, height, inset.Right, inset.Bottom);
                using var stroke = new SKPaint
                {
                    Color = colour,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = thickness,
                    StrokeCap = SKStrokeCap.Round,
                };
                canvas.DrawLine(from, to, stroke);
                break;
            }
            case ShapeKind.Ellipse:
            {
                using var fill = new SKPaint { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };
                canvas.DrawOval(bounds, fill);
                break;
            }
            default:
            {
                var radius = Math.Min(Math.Max(0, style.CornerRadius), Math.Min(bounds.Width, bounds.Height) / 2);
                using var fill = new SKPaint { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };
                if (radius > 0) canvas.DrawRoundRect(bounds, (float)radius, (float)radius, fill);
                else canvas.DrawRect(bounds, fill);
                break;
            }
        }
        return image;
    }

    private static SKPoint End(JsonPoint? stored, int width, int height, float fallbackX, float fallbackY) =>
        stored is { } point
            ? new SKPoint((float)(point.X * width), (float)(point.Y * height))
            : new SKPoint(fallbackX, fallbackY);

    /// <summary>
    /// A new shape layer above the selected one, inside the folder that one is in. The pixels and the style
    /// share one bitmap, which is what makes the layer still a live shape.
    /// </summary>
    public static Guid? Add(CanvasDocument document, LayerShapeStyle style, SKRectI box, Guid? activeID)
    {
        if (document.Layers.Count >= LayerPlacement.MaxLayers) return null;
        if (Image(style, box.Width, box.Height) is not { } image) return null;
        var name = NextName(document, style.Kind);
        var active = activeID is { } id ? document.Layers.FirstOrDefault(layer => layer.ID == id) : null;
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(image, name),
            new LayerTransform(box.Left, box.Top, box.Width, box.Height), name)
        {
            ParentID = active is { IsGroup: true } ? active.ID : active?.ParentID,
            Shape = new LayerShape(style, image),
        };
        document.Layers.Insert(Above(document, active), layer);
        return layer.ID;
    }

    /// <summary>
    /// A live shape's pixels drawn again at the size its layer now is, so a rounded corner keeps its radius
    /// and a line keeps its ends. Null when the layer is not a live shape or the size is unchanged.
    /// </summary>
    public static (ImportedImage Asset, LayerShape Shape)? Scaled(ImageLayer layer, int width, int height)
    {
        if (layer.LiveShape is not { } style || layer.Asset is not { } asset) return null;
        if (width == asset.Width && height == asset.Height) return null;
        if (Image(style, width, height) is not { } image) return null;
        return (ImportedImage.Create(image, asset.Name), new LayerShape(style, image));
    }

    /// <summary>"Rectangle 1", "Ellipse 2", … — the first number the document is not using.</summary>
    public static string NextName(CanvasDocument document, ShapeKind kind)
    {
        var taken = document.Layers.Select(layer => layer.Name).ToHashSet();
        var prefix = kind.ToString();
        var number = 1;
        while (taken.Contains($"{prefix} {number}")) number++;
        return $"{prefix} {number}";
    }

    /// <summary>Where a new shape goes: just above the selected layer, and inside a folder above its contents.</summary>
    private static int Above(CanvasDocument document, ImageLayer? active)
    {
        if (active is null) return document.Layers.Count;
        var insertion = document.Layers.IndexOf(active) + 1;
        if (!active.IsGroup) return insertion;
        var inside = document.Descendants(active.ID);
        for (var index = document.Layers.Count - 1; index >= 0; index--)
        {
            if (!inside.Contains(document.Layers[index].ID)) continue;
            return Math.Max(insertion, index + 1);
        }
        return insertion;
    }
}
