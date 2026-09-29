using Compositor.Core.Model;
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
    /// The layer's pixels drawn into a premultiplied buffer a kernel can work on, with
    /// <paramref name="margin"/> transparent pixels of room around them for a filter that spreads — a blur
    /// fades at the layer's edge instead of stopping at it. <paramref name="placement"/> is the transform
    /// that puts that wider grid over exactly the document area the layer already covered, so nothing moves.
    /// False when the layer holds nothing, or when the buffer would be larger than one surface.
    /// </summary>
    public static bool Begin(ImageLayer layer, int margin, out SKBitmap work, out LayerTransform placement)
    {
        work = null!;
        placement = default;
        if (layer.Asset is not { } asset || layer.IsGroup) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0 || margin < 0) return false;
        long grownWidth = width + 2L * margin, grownHeight = height + 2L * margin;
        if (grownWidth > DocumentLimits.MaxSide || grownHeight > DocumentLimits.MaxSide
            || grownWidth * grownHeight > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }
        work = Allocate((int)grownWidth, (int)grownHeight);
        using (var canvas = new SKCanvas(work))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(margin, margin, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        placement = margin == 0 ? layer.Transform : Grown(layer.Transform, width, height, (int)grownWidth, (int)grownHeight);
        return true;
    }

    /// <summary>
    /// The transform that puts a wider grid over the same document area, at the same pixels per document
    /// unit, with its middle where the layer's middle was: the picture does not move as it is given room.
    /// The layer's own middle is the one that is placed, since the room is added on every side of it.
    /// </summary>
    private static LayerTransform Grown(LayerTransform transform, int width, int height, int grownWidth, int grownHeight)
    {
        var placedWidth = grownWidth * transform.Width / width;
        var placedHeight = grownHeight * transform.Height / height;
        var middle = BrushEdits.PixelToDocument(transform, width, height).MapPoint(width / 2f, height / 2f);
        return transform with
        {
            X = middle.X - placedWidth / 2,
            Y = middle.Y - placedHeight / 2,
            Width = placedWidth,
            Height = placedHeight,
        };
    }

    /// <summary>A premultiplied working buffer with nothing in it, the kind the kernels read.</summary>
    public static SKBitmap Allocate(int width, int height) => DocumentRenderer.Allocate(width, height);

    /// <summary>The same buffer again, for a kernel that reads one grid and writes another.</summary>
    public static SKBitmap Copy(SKBitmap source)
    {
        var copy = Allocate(source.Width, source.Height);
        using (var canvas = new SKCanvas(copy))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(source, SKRect.Create(0, 0, source.Width, source.Height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        return copy;
    }

    /// <summary>
    /// The straight-alpha format a layer's own pixels are held in. Drawing a premultiplied buffer into one
    /// of these divides the colour back out, so the layer keeps the invariant every other edit keeps.
    /// </summary>
    public static SKBitmap Stored(int width, int height) => Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));

    /// <summary>
    /// The filtered pixels mixed over the ones they came from where the selection allows, which is how a
    /// filter inside a selection leaves everything else alone. Both buffers are in the same grid, so a pixel
    /// mixes with the pixel that was in its place. Nothing to mix when there is no selection at all.
    /// </summary>
    public static void Keep(CanvasDocument document, SKBitmap was, SKBitmap filtered, LayerTransform placement)
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
        var toDocument = BrushEdits.PixelToDocument(placement, filtered.Width, filtered.Height);
        var clip = coverage.GetPixelSpan();
        var before = was.GetPixelSpan();
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
                        Math.Round(before[index + channel] + (now[index + channel] - before[index + channel]) * amount), 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// Puts a filtered buffer on the layer as its own pixels, at <paramref name="placement"/>. The bitmap is
    /// handed to the layer, so it is not disposed here: disposing it would leave the layer holding freed
    /// pixels.
    /// </summary>
    public static void Finish(ImageLayer layer, SKBitmap work, LayerTransform placement)
    {
        if (layer.Asset is not { } asset) return;
        var result = Stored(work.Width, work.Height);
        using (var canvas = new SKCanvas(result))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(work, SKRect.Create(0, 0, work.Width, work.Height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        layer.Asset = ImportedImage.Create(result, asset.Name);
        layer.Transform = placement;
    }

    /// <summary>
    /// The layer's mask drawn over its new grid, so it goes on covering the same document area at that grid's
    /// resolution rather than stretching to follow the layer. Null when the layer has no mask, or a uniform
    /// one, which looks the same over any grid.
    /// </summary>
    public static SKBitmap? PlaceMask(ImageLayer layer, LayerTransform maskPlacement, LayerTransform was)
    {
        if (layer.Mask is not { } mask || mask.Placement is not null) return null;
        if (layer.Asset is not { } asset) return null;
        var width = mask.Asset.Width;
        var height = mask.Asset.Height;
        if (width <= 1 && height <= 1) return null;
        var maskToDocument = BrushEdits.PixelToDocument(maskPlacement, width, height);
        if (!BrushEdits.PixelToDocument(was, asset.Width, asset.Height).TryInvert(out var documentToNew)) return null;

        var image = Bitmaps.Allocate(Bitmaps.MaskInfo(asset.Width, asset.Height));
        using (var canvas = new SKCanvas(image))
        {
            // Black outside, the way a mask is built: what the mask's own rectangle does not cover it hides.
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
            var placed = SKMatrix.Concat(documentToNew, maskToDocument);
            canvas.Concat(placed);
            canvas.DrawBitmap(mask.Asset.Image, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        return image;
    }
}
