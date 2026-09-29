using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Select ▸ Colour Range: everything in the picture near a colour, wherever it is. The kernel is covered by
/// the pixel tests; these check the path from a sample to a selection.
/// </summary>
public class ColorRangeTests
{
    /// <summary>Three bands across a canvas: red, then green, then red again, so "wherever it is" is testable.</summary>
    private static (CanvasDocument Document, SKBitmap Sample) Bands()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 60, 30);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(60, 30));
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 60; x++)
            {
                var colour = x < 20 || x >= 40 ? new SKColor(220, 40, 40) : new SKColor(40, 200, 60);
                bitmap.SetPixel(x, y, colour);
            }
        }
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Bands"),
            new LayerTransform(0, 0, 60, 30), "Bands"));
        using var sample = Rendering.DocumentRenderer.Render(document);
        return (document, sample.Copy(SKColorType.Rgba8888));
    }

    private static bool Covers(CanvasDocument document, int x, int y)
    {
        var coverage = document.Selection.Coverage(SKRectI.Create(0, 0, document.Width, document.Height));
        return coverage is not null && coverage.GetPixel(x, y).Red > 128;
    }

    [Fact]
    public void EveryPixelNearTheColourIsSelectedWhereverItIs()
    {
        var (document, sample) = Bands();
        using var _ = document;
        using var _sample = sample;
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(220, 40, 40)], [], 30, false,
            SelectionMode.Replace));
        // Both red bands, and not the green one between them.
        Assert.True(Covers(document, 5, 15), "the first red band is not selected");
        Assert.True(Covers(document, 55, 15), "the second red band is not selected");
        Assert.False(Covers(document, 30, 15), "the green band was selected");
    }

    [Fact]
    public void TheFuzzinessDecidesHowNearCounts()
    {
        var (document, sample) = Bands();
        using var _ = document;
        using var _sample = sample;
        // The exact colour is found even with no fuzziness at all, which is what "within nothing" means.
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(220, 40, 40)], [], 0, false,
            SelectionMode.Replace));
        Assert.True(Covers(document, 5, 15));
        // Five levels away is not the colour, and not within nothing of it: looking for it in Replace mode
        // finds nothing, so the selection is let go rather than left as it was.
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(225, 40, 40)], [], 0, false,
            SelectionMode.Replace));
        Assert.False(Covers(document, 5, 15));
        // And the fuzziness is what brings it in.
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(225, 40, 40)], [], 5, false,
            SelectionMode.Replace));
        Assert.True(Covers(document, 5, 15));
    }

    [Fact]
    public void TheEverythingElseSwitchTakesTheRestInstead()
    {
        var (document, sample) = Bands();
        using var _ = document;
        using var _sample = sample;
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(220, 40, 40)], [], 30, invert: true,
            SelectionMode.Replace));
        Assert.False(Covers(document, 5, 15));
        Assert.True(Covers(document, 30, 15), "the green band is not selected when the rest is");
    }

    [Fact]
    public void TheSelectionCanBeAddedToAndTakenFrom()
    {
        var (document, sample) = Bands();
        using var _ = document;
        using var _sample = sample;
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(220, 40, 40)], [], 30, false,
            SelectionMode.Replace));
        // Add the green band as well, and then take the first red one away again.
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(40, 200, 60)], [], 30, false,
            SelectionMode.Add));
        Assert.True(Covers(document, 30, 15));
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(220, 40, 40)], [], 30, false,
            SelectionMode.Subtract));
        // Taking a colour away takes every pixel of it, which is both red bands here — the point of a range
        // rather than a wand: it is not one region that goes, it is the colour.
        Assert.False(Covers(document, 5, 15), "the first red band was not taken away");
        Assert.False(Covers(document, 55, 15), "the second red band was not taken away");
        Assert.True(Covers(document, 30, 15), "the green band was taken away too");
    }

    [Fact]
    public void AColourThatIsNotThereLeavesNothingSelected()
    {
        var (document, sample) = Bands();
        using var _ = document;
        using var _sample = sample;
        // Replace with nothing found lets the selection go, as the Mac build does.
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(220, 40, 40)], [], 30, false,
            SelectionMode.Replace));
        Assert.True(Covers(document, 5, 15));
        Assert.True(SelectionEdits.SelectColorRange(document, sample, [new SKColor(10, 10, 240)], [], 5, false,
            SelectionMode.Replace));
        Assert.False(Covers(document, 5, 15));
        // And with nothing to replace there is no selection to change.
        Assert.False(SelectionEdits.SelectColorRange(document, sample, [], [], 30, false, SelectionMode.Replace));
    }

    [Fact]
    public void TheWandStillSelectsThroughTheSameOutline()
    {
        var (document, sample) = Bands();
        using var _ = document;
        using var _sample = sample;
        // The wand shares the outline the colour range uses, so it is checked here too.
        Assert.True(SelectionEdits.SelectWand(document, sample, 5, 15, new WandOptions(), SelectionMode.Replace));
        Assert.True(Covers(document, 5, 15));
        Assert.False(Covers(document, 30, 15));
    }
}
