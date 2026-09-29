using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>What the brush does to the pixels under it.</summary>
public enum BrushMode
{
    /// <summary>Paints the foreground colour.</summary>
    Paint,

    /// <summary>Paints what the layer shows elsewhere, offset from the brush: the Clone Stamp.</summary>
    Clone,

    /// <summary>Paints a softened copy of the layer, so going over an area again softens it further.</summary>
    Blur,

    /// <summary>Rebuilds the area from its surroundings: Spot Healing.</summary>
    Heal,
}

/// <summary>How Spot Healing works out what to put in the painted area.</summary>
public enum HealingMode
{
    ContentAware,
    CreateTexture,
    ProximityMatch,
}

/// <summary>
/// The brush as the tool header sets it. The diameter is in document pixels.
/// <para>
/// A class rather than a struct on purpose: <c>new BrushSettings()</c> on a record struct zero-initializes
/// it instead of applying the parameter defaults, which would quietly turn the brush into a zero-diameter
/// one that paints nothing.
/// </para>
/// </summary>
public sealed record BrushSettings(
    double Diameter = 40,
    double Hardness = 1,
    double Red = 0,
    double Green = 0,
    double Blue = 0,
    /// <summary>Caps the whole stroke, as in Photoshop: overlapping dabs never exceed it.</summary>
    double Opacity = 1,
    bool Erasing = false,
    BrushMode Mode = BrushMode.Paint,
    /// <summary>What the Clone Stamp copies from, as a whole-pixel offset in document pixels.</summary>
    SKPointI? CloneFrom = null,
    /// <summary>The Clone Stamp reads every visible layer as shown, rather than the active layer alone.</summary>
    bool CloneAllLayers = false,
    /// <summary>How a Spot Healing stroke rebuilds the area.</summary>
    HealingMode Healing = HealingMode.ContentAware,
    /// <summary>The grain Spot Healing adds when it fills smoothly. Zero picks a new pattern each stroke.</summary>
    uint Seed = 0);

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
        if (settings.Mode == BrushMode.Clone && settings.CloneFrom is null) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;

        var toDocument = PixelToDocument(layer.Transform, width, height);
        if (!toDocument.TryInvert(out var toPixel)) return false;

        // What the stroke paints from, taken now: the Clone Stamp's sample of the layer or the canvas, or
        // the layer as it is before the stroke, softened. Taken once, so going over an area again within a
        // stroke does not blur what it has just painted.
        SKBitmap? sample = null;
        if (settings.Mode is BrushMode.Clone or BrushMode.Blur)
        {
            sample = Sampled(document, layer, settings);
            if (sample is null) return false;
        }
        using var _sample = sample;
        if (!Stroke(document, points, settings, toDocument, toPixel, width, height, out var coverage)) return false;

        var painted = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        var paintedNow = sample is not null
            ? ApplySampled(painted, coverage, settings, sample, toDocument)
            : settings.Mode == BrushMode.Heal ? Heal(painted, coverage, settings) : Apply(painted, coverage, settings);
        if (!paintedNow)
        {
            painted.Dispose();
            return false;
        }
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        return true;
    }

    /// <summary>
    /// Spot Healing: the area the stroke covered is rebuilt from what surrounds it, by the ported kernel,
    /// in one go. The Mac build shows a dark wash while the stroke is being drawn and does this when it
    /// ends; this port does the same work without the wash, so the picture changes on release.
    /// </summary>
    private static bool Heal(SKBitmap painted, float[] coverage, BrushSettings settings)
    {
        var width = painted.Width;
        var height = painted.Height;
        var bounds = new int[4];
        CoverageBounds(coverage, width, height, bounds);
        if (bounds[2] <= bounds[0] || bounds[3] <= bounds[1]) return true;

        // Room for the kernel's patch search, which looks up to about three spot-widths away.
        var portrait = Math.Max(bounds[2] - bounds[0], bounds[3] - bounds[1]);
        var reach = (int)Math.Ceiling((portrait + 32) * 3.2);
        var left = Math.Max(0, bounds[0] - reach);
        var top = Math.Max(0, bounds[1] - reach);
        var right = Math.Min(width, bounds[2] + reach);
        var bottom = Math.Min(height, bounds[3] + reach);
        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0) return true;

        // The kernel works on premultiplied pixels, which is what a render is held in; a layer's own
        // pixels are straight, so the region is premultiplied on the way in and unpremultiplied on the way
        // back out.
        var room = new byte[w * h * 4];
        var gray = new byte[w * h];
        var pixels = painted.GetPixelSpan();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var from = (top + y) * painted.RowBytes + (left + x) * 4;
                var to = (y * w + x) * 4;
                var alpha = pixels[from + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    room[to + channel] = (byte)((pixels[from + channel] * alpha + 127) / 255);
                }
                room[to + 3] = alpha;
                gray[y * w + x] = (byte)Math.Clamp(Math.Round(coverage[(top + y) * width + left + x] * 255), 0, 255);
            }
        }

        var seed = settings.Seed != 0 ? settings.Seed : (uint)Random.Shared.Next(1, int.MaxValue);
        var mode = (int)settings.Healing;
        if (HealPixels.SpotHeal(room, gray, w, h, w * 4, (float)settings.Opacity, mode, seed) != 0) return false;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var from = (y * w + x) * 4;
                var to = (top + y) * painted.RowBytes + (left + x) * 4;
                var alpha = room[from + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[to + channel] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (room[from + channel] * 255 + alpha / 2) / alpha);
                }
                pixels[to + 3] = alpha;
            }
        }
        return true;
    }

    /// <summary>Half-open bounds of the pixels a stroke covered, written as x0, y0, x1, y1.</summary>
    private static void CoverageBounds(float[] coverage, int width, int height, int[] bounds)
    {
        int left = width, top = height, right = 0, bottom = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (coverage[y * width + x] <= 0) continue;
                if (x < left) left = x;
                if (x + 1 > right) right = x + 1;
                if (y < top) top = y;
                if (y + 1 > bottom) bottom = y + 1;
            }
        }
        bounds[0] = left;
        bounds[1] = top;
        bounds[2] = right;
        bounds[3] = bottom;
    }

    /// <summary>
    /// The coverage of a stroke over one pixel grid: the tip stamped along the path at the spacing the Mac
    /// build uses, scaled by the selection so a dab that straddles a feathered edge is painted in part
    /// rather than all or nothing. False when the selection's coverage will not fit in memory, since
    /// painting without it would paint outside the selection.
    /// </summary>
    private static bool Stroke(CanvasDocument document, IReadOnlyList<SKPoint> points, BrushSettings settings,
        SKMatrix toDocument, SKMatrix toPixel, int width, int height, out float[] coverage)
    {
        coverage = new float[width * height];
        var radius = settings.Diameter / 2;
        var spacing = Spacing(settings.Diameter, settings.Hardness);
        var hard = settings.Hardness >= 1;
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        SKBitmap? clip;
        try
        {
            clip = document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
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
        return true;
    }

    /// <summary>
    /// Gives a uniform mask the pixels of the layer it covers, which is what the Mac build does when a brush
    /// first paints on one: until then the mask is one value stretched over the layer, and painting on it
    /// would write to that single pixel.
    /// </summary>
    public static bool GrowMask(CanvasDocument document, Guid layerID)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return false;
        if (mask.Asset.Width > 1 || mask.Asset.Height > 1) return false;
        var width = Math.Max(1, (int)Math.Round(layer.Transform.Width));
        var height = Math.Max(1, (int)Math.Round(layer.Transform.Height));
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }
        var grew = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
        grew.Erase(mask.Asset.Image.GetPixel(0, 0));
        layer.Mask = mask.Replacing(ImportedImage.Create(grew, mask.Asset.Name));
        return true;
    }

    /// <summary>
    /// Paints a stroke into a layer's mask instead of its pixels: white reveals what the mask hides and
    /// black hides what it shows, at the brush's opacity. The mask keeps its placement, whether it is
    /// enabled and whether it is linked, and the layer's own pixels are not touched.
    /// </summary>
    public static bool PaintMask(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> points,
        BrushSettings settings)
    {
        if (points.Count == 0 || settings.Diameter <= 0 || settings.Opacity <= 0) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: not null }) return false;
        // A mask the menu made is one pixel stretched over the layer, which a dab cannot land in: it takes
        // the layer's own pixels first, as the Mac build's brush does.
        GrowMask(document, layerID);
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return false;
        var width = mask.Asset.Width;
        var height = mask.Asset.Height;
        if (width <= 0 || height <= 0) return false;
        var toDocument = PixelToDocument(layer.MaskTransform, width, height);
        if (!toDocument.TryInvert(out var toPixel)) return false;
        if (!Stroke(document, points, settings, toDocument, toPixel, width, height, out var coverage)) return false;

        var painted = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(mask.Asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        ApplyToMask(painted.GetPixelSpan(), coverage, settings);
        layer.Mask = mask.Replacing(ImportedImage.Create(painted, mask.Asset.Name));
        return true;
    }

    /// <summary>Moves a mask's gray level towards the brush's, so a mask stroke sets coverage rather than painting a colour.</summary>
    private static void ApplyToMask(Span<byte> mask, float[] coverage, BrushSettings settings)
    {
        var value = (byte)Math.Clamp(Math.Round((settings.Red + settings.Green + settings.Blue) / 3 * 255), 0, 255);
        for (var index = 0; index < coverage.Length; index++)
        {
            var alpha = Math.Clamp(coverage[index] * settings.Opacity, 0, 1);
            if (alpha <= 0) continue;
            mask[index] = (byte)Math.Round(mask[index] + (value - mask[index]) * alpha);
        }
    }

    /// <summary>
    /// What a Clone Stamp or Blur stroke paints from, at document size: the layer's own pixels, or every
    /// visible layer as the canvas shows them, softened by an amount that follows the brush size when the
    /// brush is the Blur tool. Null when the layer holds nothing to copy.
    /// </summary>
    private static SKBitmap? Sampled(CanvasDocument document, ImageLayer layer, BrushSettings settings)
    {
        SKBitmap? sample = null;
        try
        {
            sample = DocumentRenderer.Allocate(document.Width, document.Height);
            using (var canvas = new SKCanvas(sample))
            {
                if (settings.Mode == BrushMode.Clone && settings.CloneAllLayers) return Composite(document);
                DocumentRenderer.DrawLayerPixels(canvas, layer);
            }
            if (settings.Mode == BrushMode.Blur)
            {
                // A blur softens by an amount that follows the brush, as Photoshop's does.
                var sigma = Math.Clamp(settings.Diameter / 10, 1.5, 30);
                GaussianBlur.Clamped(sample.GetPixelSpan(), sample.Width, sample.Height, 4, sample.RowBytes, sigma);
            }
            return sample;
        }
        catch (InvalidOperationException)
        {
            sample?.Dispose();
            return null;
        }
    }

    /// <summary>The whole canvas as it is shown, in a buffer the sample can be read from.</summary>
    private static SKBitmap Composite(CanvasDocument document)
    {
        using var rendered = DocumentRenderer.Render(document);
        var copy = DocumentRenderer.Allocate(rendered.Width, rendered.Height);
        using (var canvas = new SKCanvas(copy))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(rendered, SKRect.Create(0, 0, rendered.Width, rendered.Height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        return copy;
    }

    /// <summary>
    /// Paints what the sample holds, through the coverage: the Clone Stamp's copy, or the softened copy the
    /// Blur brush paints. A layer pixel is carried to the document, the sample read a whole-pixel offset
    /// from there, and the sample's own alpha scales the dab — so a transparent part of the sample leaves
    /// the layer as it was, as drawing it would.
    /// </summary>
    private static bool ApplySampled(SKBitmap painted, float[] coverage, BrushSettings settings, SKBitmap sample,
        SKMatrix toDocument)
    {
        var offset = settings.CloneFrom ?? default;
        var destination = painted.GetPixelSpan();
        var source = sample.GetPixelSpan();
        var sourceStride = sample.RowBytes;
        for (var y = 0; y < painted.Height; y++)
        {
            for (var x = 0; x < painted.Width; x++)
            {
                var index = y * painted.Width + x;
                if (coverage[index] <= 0) continue;
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                var sx = (int)Math.Floor(at.X) + offset.X;
                var sy = (int)Math.Floor(at.Y) + offset.Y;
                if (sx < 0 || sy < 0 || sx >= sample.Width || sy >= sample.Height) continue;
                var from = sy * sourceStride + sx * 4;
                var sourceAlpha = source[from + 3];
                var alpha = Math.Clamp(coverage[index] * settings.Opacity * (sourceAlpha / 255.0), 0, 1);
                if (alpha <= 0) continue;
                // The sample is premultiplied, as every render is; the layer's pixels are straight.
                var red = sourceAlpha == 0 ? 0 : Math.Min(255.0, source[from] * 255.0 / sourceAlpha) / 255.0;
                var green = sourceAlpha == 0 ? 0 : Math.Min(255.0, source[from + 1] * 255.0 / sourceAlpha) / 255.0;
                var blue = sourceAlpha == 0 ? 0 : Math.Min(255.0, source[from + 2] * 255.0 / sourceAlpha) / 255.0;
                Blend(destination, index * 4, alpha, red, green, blue);
            }
        }
        return true;
    }

    /// <summary>Straight (unpremultiplied) source-over, which is the form a layer's pixels are held in.</summary>
    private static void Blend(Span<byte> pixels, int at, double alpha, double red, double green, double blue)
    {
        var under = pixels[at + 3] / 255.0;
        var outAlpha = alpha + under * (1 - alpha);
        if (outAlpha <= 0) return;
        Span<double> colour = stackalloc double[3];
        colour[0] = red;
        colour[1] = green;
        colour[2] = blue;
        for (var channel = 0; channel < 3; channel++)
        {
            var behind = pixels[at + channel] / 255.0 * under * (1 - alpha);
            pixels[at + channel] = (byte)Math.Clamp(Math.Round((colour[channel] * alpha + behind) / outAlpha * 255), 0, 255);
        }
        pixels[at + 3] = (byte)Math.Round(outAlpha * 255);
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
    private static bool Apply(SKBitmap pixels, float[] coverage, BrushSettings settings)
    {
        var span = pixels.GetPixelSpan();
        for (var index = 0; index < coverage.Length; index++)
        {
            var alpha = Math.Clamp(coverage[index] * settings.Opacity, 0, 1);
            if (alpha <= 0) continue;
            var at = index * 4;
            if (settings.Erasing)
            {
                span[at + 3] = (byte)Math.Round(span[at + 3] / 255.0 * (1 - alpha) * 255);
                continue;
            }
            Blend(span, at, alpha, settings.Red, settings.Green, settings.Blue);
        }
        return true;
    }
}
