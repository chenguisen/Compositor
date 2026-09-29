using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Filling and clearing through the selection: Edit ▸ Fill with the foreground or background colour, and Edit ▸
/// Clear, which is Delete with something selected. What the selection covers is what is touched, and how far it
/// covers a pixel is how much of the change it takes, so a feathered edge is filled softly rather than cut off
/// — the same rule every other destructive edit follows.
/// </summary>
public static class FillEdits
{
    /// <summary>
    /// Fills a layer's pixels with a colour, inside the selection when there is one. False when there is no such
    /// layer, when it is a folder, or when the selection covers nothing of it.
    /// </summary>
    public static bool Fill(CanvasDocument document, Guid layerID, SKColor colour)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { IsGroup: false, Asset: { } asset } layer)
        {
            return false;
        }
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;
        using var coverage = Coverage(document, out var region);
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, width, height);
        var painted = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        var pixels = painted.GetPixelSpan();
        var touched = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var amount = Amount(coverage, region, toDocument.MapPoint(x + 0.5f, y + 0.5f));
                if (amount <= 0) continue;
                BrushEdits.Blend(pixels, y * painted.RowBytes + x * 4, amount,
                    colour.Red / 255.0, colour.Green / 255.0, colour.Blue / 255.0);
                touched++;
            }
        }
        if (touched == 0)
        {
            painted.Dispose();
            return false;
        }
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        return true;
    }

    /// <summary>
    /// Clears a layer's pixels through the selection: what is selected becomes transparent, and what is only
    /// half covered is half faded, so a feathered edge clears softly. False when nothing of the layer is
    /// selected.
    /// </summary>
    public static bool Clear(CanvasDocument document, Guid layerID)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { IsGroup: false, Asset: { } asset } layer)
        {
            return false;
        }
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;
        using var coverage = Coverage(document, out var region);
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, width, height);
        var painted = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        var pixels = painted.GetPixelSpan();
        var touched = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * painted.RowBytes + x * 4;
                var amount = Amount(coverage, region, toDocument.MapPoint(x + 0.5f, y + 0.5f));
                if (amount <= 0 || pixels[at + 3] == 0) continue;
                pixels[at + 3] = (byte)Math.Clamp(
                    Math.Round(pixels[at + 3] * (1 - amount), MidpointRounding.AwayFromZero), 0, 255);
                touched++;
            }
        }
        if (touched == 0)
        {
            painted.Dispose();
            return false;
        }
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        return true;
    }

    /// <summary>
    /// Fills a layer's mask through the selection with a gray: white reveals and black hides, so this is what
    /// Edit ▸ Fill does when the mask is the thing being painted. False when the layer has no mask, or the
    /// selection covers none of it.
    /// </summary>
    public static bool FillMask(CanvasDocument document, Guid layerID, byte value)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return false;
        var width = mask.Asset.Width;
        var height = mask.Asset.Height;
        if (width <= 0 || height <= 0) return false;
        using var coverage = Coverage(document, out var region);
        var toDocument = BrushEdits.PixelToDocument(layer.MaskTransform, width, height);
        var painted = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(mask.Asset.Image, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        var pixels = painted.GetPixelSpan();
        var touched = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var amount = Amount(coverage, region, toDocument.MapPoint(x + 0.5f, y + 0.5f));
                if (amount <= 0) continue;
                var at = y * painted.RowBytes + x;
                pixels[at] = (byte)Math.Clamp(Math.Round(pixels[at] + (value - pixels[at]) * amount), 0, 255);
                touched++;
            }
        }
        if (touched == 0)
        {
            painted.Dispose();
            return false;
        }
        layer.Mask = mask.Replacing(ImportedImage.Create(painted, mask.Asset.Name));
        return true;
    }

    /// <summary>
    /// The selection's coverage over the document, or null when the whole document is open to the edit. The
    /// region it was taken over comes back with it, since that is what a document place is looked up in.
    /// </summary>
    private static SKBitmap? Coverage(CanvasDocument document, out SKRectI region)
    {
        region = SKRectI.Create(0, 0, 0, 0);
        if (document.Selection.Path is null) return null;
        region = document.Selection.CoverageRect(document.Width, document.Height);
        try
        {
            return document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>How much of a document place the selection covers: nothing outside it, all of it well inside.</summary>
    private static double Amount(SKBitmap? coverage, SKRectI region, SKPoint at)
    {
        if (coverage is null) return 1;
        var column = (int)Math.Floor(at.X) - region.Left;
        var row = (int)Math.Floor(at.Y) - region.Top;
        if (column < 0 || row < 0 || column >= region.Width || row >= region.Height) return 0;
        return coverage.GetPixelSpan()[row * coverage.RowBytes + column] / 255.0;
    }
}
