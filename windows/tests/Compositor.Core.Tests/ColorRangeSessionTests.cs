using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Colour Range while its panel is open: the colours picked one at a time, how near a colour counts, and the
/// selection built again from them on every change — which is what the Mac build's panel is a face on.
/// </summary>
public class ColorRangeSessionTests
{
    /// <summary>Three bands across a canvas: red, then green, then red again, so "wherever it is" is testable.</summary>
    private static CanvasDocument Bands()
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
        return document;
    }

    private static bool Covers(CanvasDocument document, int x, int y)
    {
        var coverage = document.Selection.Coverage(SKRectI.Create(0, 0, document.Width, document.Height));
        return coverage is not null && coverage.GetPixel(x, y).Red > 128;
    }

    [Fact]
    public void APickedColourMakesTheSelectionWhereverItAppears()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        Assert.False(session.HasColours);
        Assert.True(session.Pick(document, new SKColor(220, 40, 40), ColorRangeSession.Picking.Replace));
        Assert.True(Covers(document, 5, 15), "the first red band is not selected");
        Assert.True(Covers(document, 55, 15), "the second red band is not selected");
        Assert.False(Covers(document, 30, 15), "the green band was selected");
        Assert.Single(session.Include);
        Assert.Null(session.Problem);
    }

    [Fact]
    public void ASecondColourJoinsTheFirst()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        Assert.True(session.Pick(document, new SKColor(220, 40, 40), ColorRangeSession.Picking.Add));
        Assert.True(session.Pick(document, new SKColor(40, 200, 60), ColorRangeSession.Picking.Add));
        Assert.True(Covers(document, 5, 15) && Covers(document, 30, 15) && Covers(document, 55, 15));
        Assert.Equal(2, session.Include.Count);
    }

    /// <summary>Taking a colour away is what lets a click on the background be undone without starting over.</summary>
    [Fact]
    public void AColourTakenAwayLeavesTheRange()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        Assert.True(session.Pick(document, new SKColor(220, 40, 40), ColorRangeSession.Picking.Add));
        Assert.True(session.Pick(document, new SKColor(40, 200, 60), ColorRangeSession.Picking.Add));
        Assert.True(session.Pick(document, new SKColor(40, 200, 60), ColorRangeSession.Picking.Remove));
        Assert.True(Covers(document, 5, 15), "the red band went with the green");
        Assert.False(Covers(document, 30, 15), "the taken-away colour is still selected");
        Assert.Single(session.Exclude);
    }

    /// <summary>
    /// Picking a colour with nothing like it in the picture has to say so rather than leave an empty selection
    /// looking as if it worked, and the selection that was there has to survive.
    /// </summary>
    [Fact]
    public void AColourNothingMatchesIsRefusedAndSaysWhy()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        Assert.True(session.Pick(document, new SKColor(220, 40, 40), ColorRangeSession.Picking.Replace));
        var held = document.Selection;
        Assert.False(session.Pick(document, new SKColor(10, 10, 250), ColorRangeSession.Picking.Replace));
        Assert.NotNull(session.Problem);
        Assert.Same(held, document.Selection);
        Assert.True(Covers(document, 5, 15), "the selection that was there was lost");
    }

    /// <summary>The fuzziness is what the panel's slider moves, and it has to reach the selection.</summary>
    [Fact]
    public void TheFuzzinessDecidesHowNearCounts()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        // The colour the picture actually holds there, since a pick is matched against the canvas as it is shown
        // and not against the layer's own numbers.
        using var shown = DocumentRenderer.Render(document);
        var band = shown.GetPixel(5, 15);
        var off = new SKColor((byte)Math.Max(0, band.Red - 60), (byte)Math.Min(255, band.Green + 60),
            (byte)Math.Min(255, band.Blue + 60));
        session.Fuzziness = 0;
        Assert.True(session.Pick(document, band, ColorRangeSession.Picking.Replace));
        Assert.True(Covers(document, 5, 15));
        // A colour well off the band's is not found with no fuzziness at all…
        Assert.False(session.Pick(document, off, ColorRangeSession.Picking.Replace));
        Assert.True(Covers(document, 5, 15), "the refused pick lost the selection that was there");
        // …and is once the panel's fuzziness allows it.
        session.Fuzziness = 70;
        Assert.True(session.Pick(document, off, ColorRangeSession.Picking.Replace), $"band {band} off {off} problem {session.Problem}");
        Assert.True(Covers(document, 5, 15), "the near colour was not found within a fuzziness of 70");
    }

    /// <summary>Invert is the panel's tick, and it turns the selection over.</summary>
    [Fact]
    public void InvertTurnsTheSelectionOver()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        session.Invert = true;
        Assert.True(session.Pick(document, new SKColor(220, 40, 40), ColorRangeSession.Picking.Replace));
        Assert.False(Covers(document, 5, 15), "the red band is still selected with the tick on");
        Assert.True(Covers(document, 30, 15), "the green band is not selected with the tick on");
    }

    /// <summary>
    /// The panel's preview is the selection shaped like the canvas, which is what it draws over black: its own
    /// aspect, white where the selection holds.
    /// </summary>
    [Fact]
    public void TheMaskIsTheSelectionShapedLikeTheCanvas()
    {
        using var document = Bands();
        using var session = ColorRangeSession.Begin(document);
        Assert.NotNull(session);
        Assert.Null(session.Mask(document));
        Assert.True(session.Pick(document, new SKColor(220, 40, 40), ColorRangeSession.Picking.Replace));
        using var mask = session.Mask(document);
        Assert.NotNull(mask);
        Assert.Equal(2, mask.Width / (double)mask.Height, 1);
        Assert.True(mask.Width <= ColorRangeSession.MaskWidth && mask.Height <= ColorRangeSession.MaskHeight);
        // The left end of the strip is the red band, which is selected; the middle is the green one, which is not.
        Assert.True(mask.GetPixel(2, mask.Height / 2).Red > 128, "the selected band is not white in the preview");
        Assert.True(mask.GetPixel(mask.Width / 2, mask.Height / 2).Red < 128, "the unselected band is white in the preview");
    }
}
