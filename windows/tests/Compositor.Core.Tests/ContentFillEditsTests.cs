using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Filter ▸ Content-Aware Fill over a layer's own pixels. The kernel is covered by the pixel tests; these
/// check the pipeline around it: what the selection means, what happens when it reaches past the layer, and
/// that a fill really does take the damage out.
/// </summary>
public class ContentFillEditsTests
{
    /// <summary>Vertical stripes with a red disc painted over them, the way something to be filled would be.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Striped(int width, int height, int holeX, int holeY, int radius)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var stripe = (x / 4) % 2 == 0 ? (byte)200 : (byte)60;
                bitmap.SetPixel(x, y, new SKColor(stripe, stripe, stripe));
            }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - holeX;
                var dy = y - holeY;
                if (dx * dx + dy * dy <= radius * radius) bitmap.SetPixel(x, y, new SKColor(220, 40, 40));
            }
        }
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Stripes"),
            new LayerTransform(0, 0, width, height), "Stripes");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static bool IsRed(SKColor colour) => colour.Red > colour.Green + 40 && colour.Red > colour.Blue + 40;

    [Fact]
    public void AFillTakesTheDamageOutAndLeavesTheRestOfTheLayer()
    {
        var (document, layer) = Striped(40, 40, 10, 10, 5);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(3, 3, 14, 14));
        Assert.True(ContentFillEdits.Apply(document, layer.ID));

        // Every pixel of the filled square is made of the stripes around it, so none of them is red.
        for (var y = 3; y < 17; y++)
        {
            for (var x = 3; x < 17; x++)
            {
                Assert.False(IsRed(layer.Asset!.Image.GetPixel(x, y)), $"red left at {x},{y}");
            }
        }
        // And the layer is the same size and place it was: nothing had to grow.
        Assert.Equal(40, layer.Asset!.Width);
        Assert.Equal(new LayerTransform(0, 0, 40, 40), layer.Transform);
    }

    [Fact]
    public void TheFillIsMadeOfThePixelsAroundItRatherThanOneColour()
    {
        var (document, layer) = Striped(40, 40, 20, 20, 6);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(13, 13, 14, 14));
        Assert.True(ContentFillEdits.Apply(document, layer.ID));
        // The stripes run through what was filled: the square holds both the light and the dark stripe tone.
        var tones = new HashSet<byte>();
        for (var y = 13; y < 27; y++)
            for (var x = 13; x < 27; x++)
                tones.Add(layer.Asset!.Image.GetPixel(x, y).Red);
        Assert.True(tones.Count > 1, "the fill came out flat");
        Assert.True(tones.Any(tone => tone > 120) && tones.Any(tone => tone is > 40 and < 120),
            $"the stripes did not come through: {string.Join(",", tones.OrderBy(t => t))}");
    }

    [Fact]
    public void ASelectionReachingPastTheLayerGrowsItToCoverTheSelection()
    {
        var (document, layer) = Striped(20, 20, 10, 10, 3);
        using var _ = document;
        // Document (0,0) to (12,12): the layer is 20 x 20 there, so the grid is the layer's own, and the
        // strip it does not cover is wide enough to fill from.
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 12, 12));
        Assert.True(ContentFillEdits.Apply(document, layer.ID));
        Assert.Equal(20, layer.Asset!.Width);
        Assert.Equal(20, layer.Asset.Height);

        // Now a selection on the canvas past the layer's edge: the layer reaches out to cover it.
        var (wide, other) = Striped(20, 20, 5, 5, 3);
        using var _wide = wide;
        Assert.True(CanvasEdits.Resize(wide, 60, 60));
        SelectionEdits.Select(wide, SKRectI.Create(10, 10, 20, 20));
        Assert.True(ContentFillEdits.Apply(wide, other.ID), "the selection past the layer was not filled");
        // The layer kept its pixels per unit and now covers the selection's rectangle on the document.
        Assert.Equal(other.Transform.Width, other.Asset!.Width, 3);
        Assert.True(other.Transform.X <= 10 && other.Transform.X + other.Transform.Width >= 30,
            $"the layer does not reach the selection: {other.Transform}");
        Assert.True(other.Transform.Y <= 10 && other.Transform.Y + other.Transform.Height >= 30,
            $"the layer does not reach the selection: {other.Transform}");
        // What was already there did not move: layer pixel (2,2) is still a stripe, at document (22,22).
        Assert.False(IsRed(other.Asset.Image.GetPixel(
            (int)Math.Round(22 - other.Transform.X), (int)Math.Round(22 - other.Transform.Y))));
        Assert.Equal(200, other.Asset.Image.GetPixel(
            (int)Math.Round(22 - other.Transform.X), (int)Math.Round(22 - other.Transform.Y)).Red);
    }

    [Fact]
    public void NoSelectionIsRefused()
    {
        var (document, layer) = Striped(20, 20, 10, 10, 3);
        using var _ = document;
        Assert.False(ContentFillEdits.Apply(document, layer.ID));
        Assert.Equal(20, layer.Asset!.Width);
    }

    [Fact]
    public void ASelectionWithNothingToFillFromIsRefused()
    {
        var (document, layer) = Striped(20, 20, 10, 10, 3);
        using var _ = document;
        // The whole layer selected: there is no unselected pixel anywhere to take the fill from.
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 20, 20));
        Assert.False(ContentFillEdits.Apply(document, layer.ID));
        // Left exactly as it was, red disc and all.
        Assert.True(IsRed(layer.Asset!.Image.GetPixel(10, 10)));
    }

    [Fact]
    public void ALayerWithNoPixelsIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        SelectionEdits.Select(document, SKRectI.Create(4, 4, 8, 8));
        Assert.False(ContentFillEdits.Apply(document, blank.ID));
    }

    [Fact]
    public void TheResultIsHeldTheWayALayerHoldsItsPixels()
    {
        var (document, layer) = Striped(30, 30, 15, 15, 4);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(10, 10, 10, 10));
        Assert.True(ContentFillEdits.Apply(document, layer.ID));
        Assert.Equal(SKAlphaType.Unpremul, layer.Asset!.Image.AlphaType);
        Assert.Equal("Stripes", layer.Name);
    }
}
