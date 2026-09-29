using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// What every destructive filter needs: the layer's pixels in the premultiplied form the kernels read —
/// they divide the colour back out themselves — and, when the kernel is done, the result turned back into
/// the straight-alpha pixels a layer is held in, mixed over the original where the selection says so.
/// </summary>
internal static class FilterSurface
{
    /// <summary>
    /// The layer's pixels drawn into a premultiplied buffer a kernel can work on. False when the layer holds
    /// nothing, or when it holds more pixels than one surface can.
    /// </summary>
    public static bool Begin(ImageLayer layer, out SKBitmap work)
    {
        work = null!;
        if (layer.Asset is not { } asset || layer.IsGroup) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }
        work = Allocate(width, height);
        using var canvas = new SKCanvas(work);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        using var source = SKImage.FromBitmap(asset.Image);
        canvas.DrawImage(source, SKRect.Create(0, 0, width, height),
            new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return true;
    }

    /// <summary>A premultiplied working buffer with nothing in it, the kind the kernels read.</summary>
    public static SKBitmap Allocate(int width, int height) => DocumentRenderer.Allocate(width, height);

    /// <summary>
    /// The straight-alpha format a layer's own pixels are held in. Drawing a premultiplied buffer into one
    /// of these divides the colour back out, so the layer keeps the invariant every other edit keeps.
    /// </summary>
    private static SKBitmap Stored(int width, int height) => Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));

    /// <summary>
    /// Puts a filtered buffer back on the layer: everything outside the selection is left exactly as it was,
    /// and the pixels are stored straight-alpha, the way a layer's own pixels are held.
    /// </summary>
    public static void Finish(CanvasDocument document, ImageLayer layer, SKBitmap work)
    {
        if (layer.Asset is not { } asset) return;
        // Not disposed here: the bitmap is handed to the layer, and disposing it would leave the layer
        // holding freed pixels.
        var result = Stored(work.Width, work.Height);
        using (var canvas = new SKCanvas(result))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(work, SKRect.Create(0, 0, work.Width, work.Height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        Keep(document, layer, asset.Image, result);
        layer.Asset = ImportedImage.Create(result, asset.Name);
    }

    /// <summary>
    /// The filtered pixels mixed over the original where the selection allows, which is how a filter inside a
    /// selection leaves everything else alone.
    /// </summary>
    private static void Keep(CanvasDocument document, ImageLayer layer, SKBitmap original, SKBitmap filtered)
    {
        if (document.Selection.Path is null) return;
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        SKBitmap? coverage;
        try
        {
            coverage = document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (coverage is null) return;
        using var _ = coverage;
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, filtered.Width, filtered.Height);
        var clip = coverage.GetPixelSpan();
        var was = original.GetPixelSpan();
        var now = filtered.GetPixelSpan();
        var stride = filtered.RowBytes;
        for (var y = 0; y < filtered.Height; y++)
        {
            for (var x = 0; x < filtered.Width; x++)
            {
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                var column = (int)Math.Floor(at.X) - region.Left;
                var row = (int)Math.Floor(at.Y) - region.Top;
                var amount = column < 0 || row < 0 || column >= region.Width || row >= region.Height
                    ? 0.0
                    : clip[row * coverage.RowBytes + column] / 255.0;
                if (amount >= 1) continue;
                var index = y * stride + x * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    now[index + channel] = (byte)Math.Clamp(
                        Math.Round(was[index + channel] + (now[index + channel] - was[index + channel]) * amount), 0, 255);
                }
            }
        }
    }
}
