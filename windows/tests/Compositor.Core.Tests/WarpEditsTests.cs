using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Smudge and Liquify: the Blur tool's two modes that push pixels around instead of mixing them. Both work on
/// the layer as the canvas shows it, at document size, and put the result back under the stroke.
/// </summary>
public class WarpEditsTests
{
    /// <summary>
    /// A layer of two colours side by side, the red half first, and the document holding it. By default the
    /// layer covers the canvas exactly, so a layer pixel and a document pixel are the same place.
    /// </summary>
    private static (CanvasDocument Document, ImageLayer Layer) Split(int side, LayerTransform? transform = null,
        int canvas = 0)
    {
        var extent = canvas > 0 ? canvas : side;
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                bitmap.SetPixel(x, y, x < side / 2 ? new SKColor(220, 40, 40) : new SKColor(40, 60, 220));
        }
        var document = new CanvasDocument(Guid.NewGuid(), extent, extent);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Split"),
            transform ?? new LayerTransform(0, 0, side, side), "Split");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor At(ImageLayer layer, int x, int y) => layer.Asset!.Image.GetPixel(x, y);

    private static bool IsRed(SKColor colour) => colour.Red > 150 && colour.Blue < 110;

    private static bool IsBlue(SKColor colour) => colour.Blue > 150 && colour.Red < 110;

    private static BrushSettings Brush(double diameter = 12, double hardness = 1, double opacity = 1) =>
        new(Diameter: diameter, Hardness: hardness, Opacity: opacity);

    [Fact]
    public void LiquifyPushesWhatIsUnderTheBrushAlongWithIt()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        Assert.True(IsBlue(At(layer, 25, 20)), $"the boundary is not where it was: {At(layer, 25, 20)}");

        // From inside the red, rightwards across the boundary: the pixels behind the brush are dragged along
        // with it, so the red half reaches further than it did.
        Assert.True(WarpEdits.Warp(document, layer.ID, [new SKPoint(10, 20), new SKPoint(30, 20)],
            WarpMode.Liquify, Brush()));

        Assert.True(IsRed(At(layer, 25, 20)), $"the pixels were not pushed along: {At(layer, 25, 20)}");
        Assert.True(IsRed(At(layer, 32, 20)), $"the red did not reach as far as the brush did: {At(layer, 32, 20)}");
        // What the brush went nowhere near is exactly as it was: red on its own side, blue past where the
        // brush's rim reached.
        Assert.Equal(new SKColor(220, 40, 40), At(layer, 4, 20));
        Assert.Equal(new SKColor(40, 60, 220), At(layer, 35, 35));
    }

    [Fact]
    public void SmudgeDragsTheColourTheBrushCarriesAlongWithIt()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        Assert.True(IsBlue(At(layer, 30, 20)), $"the boundary is not where it was: {At(layer, 30, 20)}");

        // The brush takes up the red it starts on and trails it across the boundary and on into the blue.
        Assert.True(WarpEdits.Warp(document, layer.ID, [new SKPoint(10, 20), new SKPoint(32, 20)],
            WarpMode.Smudge, Brush(diameter: 14, hardness: 0.6)));

        Assert.True(IsRed(At(layer, 30, 20)), $"the colour was not dragged along: {At(layer, 30, 20)}");
        // A smudge only moves colour under the brush, so the far corner is untouched.
        Assert.Equal(new SKColor(40, 60, 220), At(layer, 38, 38));
    }

    [Fact]
    public void ADabOnlyReachesAsFarAsTheBrushAndItsRim()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        Assert.True(WarpEdits.Warp(document, layer.ID, [new SKPoint(10, 20), new SKPoint(30, 20)],
            WarpMode.Liquify, Brush(diameter: 8)));
        // The brush is eight across, so a pixel ten below the stroke is not under it.
        Assert.Equal(new SKColor(220, 40, 40), At(layer, 12, 30));
        Assert.Equal(new SKColor(40, 60, 220), At(layer, 30, 30));
        // Nor is anything behind where the stroke began.
        Assert.Equal(new SKColor(220, 40, 40), At(layer, 2, 20));
    }

    [Fact]
    public void AStrokeThatReachesNothingLeavesTheLayerAsItWas()
    {
        var (document, layer) = Split(40, new LayerTransform(200, 200, 40, 40));
        using var _ = document;
        var before = At(layer, 10, 10);
        // The layer is off the canvas, so the stroke passes over nothing of it.
        Assert.False(WarpEdits.Warp(document, layer.ID, [new SKPoint(0, 0), new SKPoint(40, 0)],
            WarpMode.Liquify, Brush()));
        Assert.Equal(before, At(layer, 10, 10));
    }

    [Fact]
    public void AStrokeNeedsALayerWithPixelsAndSomewhereToGo()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        var settings = Brush();
        var points = new[] { new SKPoint(10, 20), new SKPoint(30, 20) };
        Assert.False(WarpEdits.Warp(document, Guid.NewGuid(), points, WarpMode.Liquify, settings));
        // Nothing dragged anywhere is nothing to do.
        Assert.False(WarpEdits.Warp(document, layer.ID, [], WarpMode.Liquify, settings));
        // A brush of no size, or of no strength, has nothing behind it.
        Assert.False(WarpEdits.Warp(document, layer.ID, points, WarpMode.Liquify, settings with { Diameter = 0 }));
        Assert.False(WarpEdits.Warp(document, layer.ID, points, WarpMode.Smudge, settings with { Opacity = 0 }));
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 40, 40), "Empty");
        document.Layers.Add(blank);
        Assert.False(WarpEdits.Warp(document, blank.ID, points, WarpMode.Smudge, settings));
        // A folder holds no pixels of its own, so there is nothing to push.
        var folder = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 40, 40), "Folder") { IsGroup = true };
        document.Layers.Add(folder);
        Assert.False(WarpEdits.Warp(document, folder.ID, points, WarpMode.Smudge, settings));
    }

    [Fact]
    public void ALayerScaledUpIsPushedWhereItIsAndKeepsItsOwnPixels()
    {
        // The stroke is measured on the document, so a layer drawn at twice the size of its pixels is pushed at
        // document size, and what comes of it goes back into the layer's own pixels at their own resolution.
        var (document, layer) = Split(20, new LayerTransform(10, 10, 40, 40), canvas: 80);
        using var _ = document;
        Assert.True(WarpEdits.Warp(document, layer.ID, [new SKPoint(20, 30), new SKPoint(34, 30)],
            WarpMode.Liquify, Brush(diameter: 16)));

        // The layer is still the picture it was, at the size it was.
        Assert.Equal(20, layer.Asset!.Width);
        Assert.Equal(20, layer.Asset.Height);
        // The boundary in the layer is at its own pixel 10, which is document 30, and the stroke crossed it:
        // the blue side now has the red dragged over it.
        var reddened = 0;
        for (var y = 6; y < 14; y++)
        {
            for (var x = 11; x < 18; x++)
            {
                if (IsRed(At(layer, x, y))) reddened++;
            }
        }
        Assert.True(reddened > 20, $"the stroke left nothing of itself behind: {reddened} pixels reddened");
        // The far corner the brush never came near is exactly what it was.
        Assert.Equal(new SKColor(220, 40, 40), At(layer, 0, 0));
        Assert.Equal(new SKColor(40, 60, 220), At(layer, 19, 19));
    }
}
