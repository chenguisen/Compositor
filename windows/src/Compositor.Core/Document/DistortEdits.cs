using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Free distortion as one edit: a layer's four corners are somewhere new, so its pixels — and the mask that
/// goes with them — are resampled into that shape and the layer is left an ordinary rectangle over it. The
/// Mac build does the same, and leaves an unmasked layer's mask alone only when it has one of its own.
/// </summary>
public static class DistortEdits
{
    /// <summary>
    /// Resamples a layer's pixels so its corners land on <paramref name="corners"/> (handle order: top left,
    /// top right, bottom right, bottom left). False when there is no such layer, when the shape is not one
    /// that can be drawn, or when it would not fit in memory — in which case the layer is left as it was.
    /// <para>
    /// A selection does not confine a distortion: the pixels are moved, not mixed, so what would be outside it
    /// has nowhere to stay. The Mac build distorts the whole layer too.
    /// </para>
    /// </summary>
    public static bool Distort(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> corners)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        if (!DistortWarp.IsUsable(corners)) return false;
        var transform = layer.Transform;

        using var source = Premultiplied(asset.Image);
        if (DistortWarp.Warp(source, transform, corners) is not { } warped) return false;
        using (warped.Image)
        {
            // A shape whose perspective has folded flat leaves nothing drawn. Rather than quietly emptying
            // the layer, the distortion is refused and the layer is left as it was.
            Span<int> edges = stackalloc int[4];
            BrushPixels.AlphaBounds(warped.Image.GetPixelSpan(), warped.Image.Width, warped.Image.Height,
                warped.Image.RowBytes, edges);
            if (edges[2] <= edges[0] || edges[3] <= edges[1]) return false;

            // What is not covered by the shape is cut away, so the layer hugs it and its handles sit on it.
            var (pixels, placed) = LayerMerge.Trimmed(warped.Image, warped.Transform);
            using (pixels)
            {
                // A mask held on the layer's own grid goes with the pixels; one with a placement of its own
                // keeps its place on the document, which leaves it where it was.
                var trimmed = new WarpedImage(pixels, placed);
                var mask = Mask(layer, transform, corners, trimmed);
                var stored = Stored(pixels);
                layer.Asset = ImportedImage.Create(stored, asset.Name);
                layer.Transform = placed;
                if (mask is not null) layer.Mask = mask;
                return true;
            }
        }
    }

    /// <summary>
    /// The layer's mask resampled the same way, when it is held on the layer's own grid, cropped to match the
    /// pixels it now goes with. Null when there is no such mask, so the caller leaves the layer's own alone.
    /// </summary>
    private static LayerMask? Mask(ImageLayer layer, LayerTransform transform, IReadOnlyList<SKPoint> corners,
        WarpedImage warped)
    {
        if (layer.Mask is not { } owned || owned.Placement is not null) return null;
        using var source = Premultiplied(owned.Asset.Image);
        if (DistortWarp.Warp(source, transform, corners) is not { } mask) return null;
        using (mask.Image)
        {
            // The same crop the pixels took, worked out from where they ended up.
            var across = warped.Transform.Width / (double)warped.Image.Width;
            var down = warped.Transform.Height / warped.Image.Height;
            var left = (int)Math.Round((warped.Transform.X - mask.Transform.X) / across);
            var top = (int)Math.Round((warped.Transform.Y - mask.Transform.Y) / down);
            var crop = SKRectI.Create(left, top, warped.Image.Width, warped.Image.Height);
            var covered = SKRectI.Create(0, 0, mask.Image.Width, mask.Image.Height);
            if (!covered.Contains(crop)) crop = SKRectI.Intersect(crop, covered);
            if (crop.Width < 1 || crop.Height < 1) return null;
            var grayscale = Grayscale(mask.Image, crop);
            if (grayscale is null) return null;
            var replaced = LayerMask.AssetFrom(grayscale);
            replaced.IsEnabled = owned.IsEnabled;
            replaced.IsLinked = owned.IsLinked;
            return replaced;
        }
    }

    /// <summary>The layer's pixels in the premultiplied form a resample wants, so a soft edge blends right.</summary>
    private static SKBitmap Premultiplied(SKBitmap image)
    {
        var work = FilterSurface.Allocate(image.Width, image.Height);
        using var canvas = new SKCanvas(work);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        using var source = SKImage.FromBitmap(image);
        canvas.DrawImage(source, SKRect.Create(0, 0, image.Width, image.Height),
            new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return work;
    }

    /// <summary>Warped pixels as the straight-alpha ones a layer is held in.</summary>
    private static SKBitmap Stored(SKBitmap warped)
    {
        var result = FilterSurface.Stored(warped.Width, warped.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(warped, SKRect.Create(0, 0, warped.Width, warped.Height),
            new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return result;
    }

    /// <summary>A piece of a warped mask as a mask again: 8-bit gray, opaque, which is what a mask is.</summary>
    private static SKBitmap? Grayscale(SKBitmap warped, SKRectI crop)
    {
        var gray = Bitmaps.Allocate(Bitmaps.MaskInfo(crop.Width, crop.Height));
        using var canvas = new SKCanvas(gray);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(warped, SKRect.Create(crop.Left, crop.Top, crop.Width, crop.Height),
            SKRect.Create(0, 0, crop.Width, crop.Height), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        canvas.Flush();
        return Bitmaps.IsValidMask(gray) ? gray : null;
    }
}
