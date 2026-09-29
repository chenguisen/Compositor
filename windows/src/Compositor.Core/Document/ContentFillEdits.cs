using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Filter ▸ Content-Aware Fill: whatever the selection covers is made up out of the pixels around it. The
/// layer reaches as far as the selection does, so a fill over a region bigger than the layer grows it, and
/// the fill's job is done by the PatchMatch-style kernel ported from the Mac build's C.
/// </summary>
public static class ContentFillEdits
{
    /// <summary>
    /// Fills the selected part of a layer from its unselected, opaque pixels. False when there is no
    /// selection, when the layer cannot take it, or when there is nothing to fill from — in which case the
    /// layer is left exactly as it was.
    /// </summary>
    public static bool Apply(CanvasDocument document, Guid layerID)
    {
        if (document.Selection.Path is null) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer
            || layer.Asset is not { } asset || layer.IsGroup)
        {
            return false;
        }
        var maskPlacement = layer.MaskTransform;

        // The grid to work over: the layer's own pixels, widened to cover the selection's bounding box where
        // that reaches past them. A fill is not a blur, so nothing is cut away again afterwards.
        var bounds = document.Selection.CoverageRect(document.Width, document.Height);
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, asset.Width, asset.Height);
        if (!toDocument.TryInvert(out var toLayer)) return false;
        var corners = new[]
        {
            toLayer.MapPoint(bounds.Left, bounds.Top),
            toLayer.MapPoint(bounds.Right, bounds.Top),
            toLayer.MapPoint(bounds.Left, bounds.Bottom),
            toLayer.MapPoint(bounds.Right, bounds.Bottom),
        };
        var wanted = SKRectI.Create(
            (int)Math.Floor(corners.Min(point => point.X)),
            (int)Math.Floor(corners.Min(point => point.Y)),
            (int)Math.Ceiling(corners.Max(point => point.X)) - (int)Math.Floor(corners.Min(point => point.X)),
            (int)Math.Ceiling(corners.Max(point => point.Y)) - (int)Math.Floor(corners.Min(point => point.Y)));
        var whole = SKRectI.Create(0, 0, asset.Width, asset.Height);
        var grid = SKRectI.Union(whole, wanted);
        if (grid.Width <= 0 || grid.Height <= 0) return false;

        if (!FilterSurface.Begin(layer, grid, out var work, out var placement)) return false;
        using var _ = work;

        // What to fill: one byte a pixel, set where the selection covers the layer pixel under it.
        var mask = new byte[work.Width * work.Height];
        if (!BuildMask(document, placement, work.Width, work.Height, mask)) return false;

        var pixels = work.GetPixelSpan();
        try
        {
            if (ContentFill.Fill(pixels, work.RowBytes, mask, work.Width, work.Width, work.Height) != 1) return false;
        }
        catch (OutOfMemoryException)
        {
            return false;
        }

        FilterSurface.Finish(layer, work, placement);
        if (FilterSurface.PlaceMask(layer, maskPlacement, placement) is { } carried)
        {
            var mask2 = LayerMask.AssetFrom(carried);
            mask2.IsEnabled = layer.Mask!.IsEnabled;
            mask2.IsLinked = layer.Mask.IsLinked;
            layer.Mask = mask2;
        }
        return true;
    }

    /// <summary>
    /// The selection as the kernel wants it: set wherever it covers the layer pixel whose middle maps there.
    /// False when the selection cannot be read, or covers none of the grid, which leaves the layer alone.
    /// </summary>
    private static bool BuildMask(CanvasDocument document, LayerTransform placement,
        int width, int height, Span<byte> mask)
    {
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        SKBitmap? coverage;
        try
        {
            coverage = document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        using var _ = coverage;
        if (coverage is null) return false;
        var clip = coverage.GetPixelSpan();
        var toDocument = BrushEdits.PixelToDocument(placement, width, height);
        var any = false;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                var column = (int)Math.Floor(at.X) - region.Left;
                var row = (int)Math.Floor(at.Y) - region.Top;
                if (column < 0 || row < 0 || column >= region.Width || row >= region.Height) continue;
                // Anything the selection touches at all is filled, as a filled path leaves its edge.
                if (clip[row * coverage.RowBytes + column] == 0) continue;
                mask[y * width + x] = 1;
                any = true;
            }
        }
        return any;
    }
}
