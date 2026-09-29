using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>The brush as the tool header sets it. The diameter is in document pixels.</summary>
public readonly record struct BrushSettings(
    double Diameter = 40,
    double Hardness = 1,
    double Red = 0,
    double Green = 0,
    double Blue = 0,
    /// <summary>Caps the whole stroke, as in Photoshop: overlapping dabs never exceed it.</summary>
    double Opacity = 1,
    bool Erasing = false);

/// <summary>
/// Painting a stroke into a layer's own pixels. Mouse samples arrive in document coordinates, so they are
/// carried back through the layer's transform; the tip is stamped along the path at the spacing the Mac
/// build uses, and the whole stroke is capped by one opacity.
/// </summary>
public static class BrushEdits
{
    /// <summary>Soft-brush falloff across the region between the hardness radius and the rim.</summary>
    public static double Falloff(double u)
    {
        const double k = 2.5;
        return Math.Max(0, (Math.Exp(-k * u * u) - Math.Exp(-k)) / (1 - Math.Exp(-k)));
    }

    /// <summary>The tip's coverage at <paramref name="distance"/> from its middle.</summary>
    public static double Tip(double distance, double radius, double hardness)
    {
        if (radius <= 0) return 0;
        var solid = radius * Math.Clamp(hardness, 0, 1);
        if (distance <= solid) return 1;
        if (distance >= radius || hardness >= 1) return 0;
        return Falloff((distance - solid) / (radius - solid));
    }

    /// <summary>How far apart dabs sit: a fraction of the diameter, so a soft tip is stamped closer.</summary>
    public static double Spacing(double diameter, double hardness) =>
        Math.Max(0.25, diameter * (hardness >= 1 ? 0.015 : 0.025));

    /// <summary>
    /// Gives a blank layer pixels the size of its rectangle, which is when the Mac build allocates them: on
    /// the first paint rather than when the layer is made. A folder has no pixels of its own, so it is left
    /// alone: giving it some would make a document that cannot be saved or loaded back.
    /// </summary>
    public static bool EnsurePixels(CanvasDocument document, Guid layerID)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: null } layer) return false;
        if (layer.IsGroup) return false;
        var width = Math.Max(1, (int)Math.Round(layer.Transform.Width));
        var height = Math.Max(1, (int)Math.Round(layer.Transform.Height));
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Transparent);
        layer.Asset = ImportedImage.Create(bitmap, layer.Name);
        return true;
    }

    /// <summary>
    /// Paints a stroke through the given document points. The layer is given a new picture — the old one
    /// stays as it was, which is what lets undo bring it back — and an edit of any kind drops whatever made
    /// the layer a live shape or a live text. A folder is not paintable, as the Mac build's tool refuses it.
    /// </summary>
    public static bool Paint(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> points,
        BrushSettings settings)
    {
        if (points.Count == 0 || settings.Diameter <= 0 || settings.Opacity <= 0) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        if (layer.IsGroup) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;

        var toDocument = PixelToDocument(layer.Transform, width, height);
        if (!toDocument.TryInvert(out var toPixel)) return false;

        var coverage = new float[width * height];
        var radius = settings.Diameter / 2;
        var spacing = Spacing(settings.Diameter, settings.Hardness);
        var hard = settings.Hardness >= 1;
        // The selection is a gray coverage over its own rectangle of the document, so a dab that straddles
        // its edge is painted in part rather than all or nothing.
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        SKBitmap? clip;
        try
        {
            clip = document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
            // The coverage would not fit in memory. Painting without it would paint outside the selection,
            // so the stroke is refused instead.
            return false;
        }
        using var _ = clip;
        ReadOnlySpan<byte> clipped = clip is null ? default : clip.GetPixelSpan();
        var selection = new Clip(clipped, clip?.RowBytes ?? 0, region);
        // The first dab sits on the first sample; the rest follow it at even spacing, carrying whatever
        // distance is left over the end of one run into the next so a fast pointer leaves no gaps.
        Stamp(coverage, width, height, points[0], radius, toDocument, toPixel, hard, settings.Hardness, selection);
        var next = spacing;
        for (var index = 1; index < points.Count; index++)
        {
            var from = points[index - 1];
            var to = points[index];
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (length <= 0) continue;
            while (next <= length)
            {
                var at = new SKPoint((float)(from.X + dx * next / length), (float)(from.Y + dy * next / length));
                Stamp(coverage, width, height, at, radius, toDocument, toPixel, hard, settings.Hardness, selection);
                next += spacing;
            }
            next -= length;
        }

        var painted = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        Apply(painted, coverage, settings);
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        return true;
    }

    /// <summary>The transform that places a layer's pixels on the document.</summary>
    public static SKMatrix PixelToDocument(Model.LayerTransform transform, int width, int height)
    {
        var scaleX = transform.Width / width * (transform.FlipX ? -1 : 1);
        var scaleY = transform.Height / height * (transform.FlipY ? -1 : 1);
        var radians = transform.Radians;
        var a = scaleX * Math.Cos(radians);
        var c = -scaleY * Math.Sin(radians);
        var b = scaleX * Math.Sin(radians);
        var d = scaleY * Math.Cos(radians);
        return new SKMatrix
        {
            ScaleX = (float)a,
            SkewX = (float)c,
            TransX = (float)(transform.CenterX - (a * width + c * height) / 2),
            SkewY = (float)b,
            ScaleY = (float)d,
            TransY = (float)(transform.CenterY - (b * width + d * height) / 2),
            Persp2 = 1,
        };
    }

    /// <summary>
    /// One dab, measured in document pixels so a rotated or stretched layer still gets a round brush: every
    /// pixel near the dab is carried back to the document and asked how far it is from the middle. The
    /// selection scales each pixel, so a dab that straddles a feathered edge is painted in part.
    /// </summary>
    private static void Stamp(float[] coverage, int width, int height, SKPoint centre, double radius,
        SKMatrix toDocument, SKMatrix toPixel, bool hard, double hardness, Clip selection)
    {
        // The document square around the dab, brought into pixels: a generous box, since the transform may
        // turn it.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (dx, dy) in new[] { (-radius, -radius), (radius, -radius), (radius, radius), (-radius, radius) })
        {
            var corner = toPixel.MapPoint(centre.X + (float)dx, centre.Y + (float)dy);
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }
        var left = Math.Max(0, (int)Math.Floor(minX) - 1);
        var top = Math.Max(0, (int)Math.Floor(minY) - 1);
        var right = Math.Min(width, (int)Math.Ceiling(maxX) + 1);
        var bottom = Math.Min(height, (int)Math.Ceiling(maxY) + 1);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                var distance = Math.Sqrt(Math.Pow(at.X - centre.X, 2) + Math.Pow(at.Y - centre.Y, 2));
                var tip = Tip(distance, radius, hardness) * selection.At(at.X, at.Y);
                if (tip <= 0) continue;
                var index = y * width + x;
                // Overlapping dabs within one stroke must not build up: they take the larger coverage, or
                // blend, so the stroke's opacity is what caps it.
                coverage[index] = hard ? Math.Max(coverage[index], (float)tip)
                    : coverage[index] + (float)tip - coverage[index] * (float)tip;
            }
        }
    }

    /// <summary>
    /// The selection as gray coverage over one rectangle of the document. Empty pixels mean there is no
    /// selection at all, so everything is painted; a rectangle with no size means nothing is.
    /// </summary>
    private readonly ref struct Clip
    {
        private readonly ReadOnlySpan<byte> _pixels;
        private readonly int _stride;
        private readonly SKRectI _region;

        public Clip(ReadOnlySpan<byte> pixels, int stride, SKRectI region)
        {
            _pixels = pixels;
            _stride = stride;
            _region = region;
        }

        /// <summary>How much of a document pixel the selection lets through, from 0 to 1.</summary>
        public float At(float x, float y)
        {
            if (_pixels.IsEmpty) return 1;
            var column = (int)Math.Floor(x) - _region.Left;
            var row = (int)Math.Floor(y) - _region.Top;
            if (column < 0 || row < 0 || column >= _region.Width || row >= _region.Height) return 0;
            return _pixels[row * _stride + column] / 255f;
        }
    }

    /// <summary>Paints the colour through the coverage, or takes the alpha away when erasing.</summary>
    private static void Apply(SKBitmap pixels, float[] coverage, BrushSettings settings)
    {
        var span = pixels.GetPixelSpan();
        for (var index = 0; index < coverage.Length; index++)
        {
            var alpha = Math.Clamp(coverage[index] * settings.Opacity, 0, 1);
            if (alpha <= 0) continue;
            var at = index * 4;
            var destination = span[at + 3] / 255.0;
            if (settings.Erasing)
            {
                span[at + 3] = (byte)Math.Round(destination * (1 - alpha) * 255);
                continue;
            }
            // Straight (unpremultiplied) source-over, which is the form a layer's pixels are held in.
            var outAlpha = alpha + destination * (1 - alpha);
            if (outAlpha <= 0) continue;
            foreach (var (channel, value) in new[] { (0, settings.Red), (1, settings.Green), (2, settings.Blue) })
            {
                var under = span[at + channel] / 255.0 * destination * (1 - alpha);
                span[at + channel] = (byte)Math.Clamp(Math.Round((value * alpha + under) / outAlpha * 255), 0, 255);
            }
            span[at + 3] = (byte)Math.Round(outAlpha * 255);
        }
    }
}
