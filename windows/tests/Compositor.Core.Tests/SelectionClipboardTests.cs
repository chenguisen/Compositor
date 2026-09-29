using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The selection clipboard: pixels taken off the canvas, cut away, and put back as a layer. What is copied is
/// clipped by the selection, so a paste carries exactly what was inside it.
/// </summary>
public class SelectionClipboardTests
{
    /// <summary>A layer of two colours side by side, its own grid the same size as the document.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Split(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                bitmap.SetPixel(x, y, x < side / 2 ? new SKColor(220, 40, 40) : new SKColor(40, 60, 220));
        }
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Split"),
            new LayerTransform(0, 0, side, side), "Split");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void CopyTakesOnlyWhatTheSelectionCovers()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(4, 4, 12, 12));
        using var copied = SelectionClipboard.Copy(document, layer.ID);
        Assert.NotNull(copied);
        // The bounds cover the selection, and the image is that size.
        Assert.True(copied.Region.Left <= 4 && copied.Region.Top <= 4, $"the bounds miss the selection: {copied.Region}");
        Assert.True(copied.Region.Right >= 16 && copied.Region.Bottom >= 16, $"the bounds miss the selection: {copied.Region}");
        Assert.Equal(copied.Region.Width, copied.Image.Width);
        Assert.Equal(copied.Region.Height, copied.Image.Height);
        // Inside the selection it is the red half, opaque; outside it there is nothing at all.
        for (var y = 0; y < copied.Image.Height; y++)
        {
            for (var x = 0; x < copied.Image.Width; x++)
            {
                var documentX = copied.Region.Left + x;
                var documentY = copied.Region.Top + y;
                var inside = documentX is >= 4 and < 16 && documentY is >= 4 and < 16;
                var pixel = copied.Image.GetPixel(x, y);
                if (!inside)
                {
                    Assert.Equal(0, pixel.Alpha);
                    continue;
                }
                Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(pixel));
                Assert.Equal(255, pixel.Alpha);
            }
        }
    }

    [Fact]
    public void CopyTakesTheSelectedHalfAndNothingOfTheOther()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        // A box across the middle: the left half of it is red, the right half blue.
        SelectionEdits.Select(document, SKRectI.Create(14, 10, 12, 12));
        using var copied = SelectionClipboard.Copy(document, layer.ID);
        Assert.NotNull(copied);
        Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(At(copied, 15, 15)));
        Assert.Equal(new SKColor(40, 60, 220), WithoutAlpha(At(copied, 24, 15)));
    }

    [Fact]
    public void ASoftEdgeIsCarriedAsACoverage()
    {
        var (document, layer) = Split(60);
        using var _ = document;
        SelectionEdits.SelectEllipse(document, SKRectI.Create(10, 10, 40, 40));
        using var copied = SelectionClipboard.Copy(document, layer.ID);
        Assert.NotNull(copied);
        // The ellipse's rim is antialiased, so the copy has a partly covered pixel somewhere and nothing
        // outside the ellipse at all.
        Assert.Equal(0, copied.Image.GetPixel(0, 0).Alpha);
        var partial = false;
        for (var y = 0; y < copied.Image.Height && !partial; y++)
            for (var x = 0; x < copied.Image.Width; x++)
            {
                var alpha = copied.Image.GetPixel(x, y).Alpha;
                if (alpha is > 0 and < 255) { partial = true; break; }
            }
        Assert.True(partial, "the soft edge was not carried over");
        // And the colour is left standing while the alpha is scaled: a partly covered rim pixel is still
        // one of the two colours the layer holds, not a blend of the mask with them.
        for (var y = 0; y < copied.Image.Height; y++)
            for (var x = 0; x < copied.Image.Width; x++)
            {
                var pixel = copied.Image.GetPixel(x, y);
                if (pixel.Alpha is 0 or 255) continue;
                var colour = WithoutAlpha(pixel);
                Assert.True(colour == new SKColor(220, 40, 40) || colour == new SKColor(40, 60, 220),
                    $"a rim pixel was mixed with something: {colour}");
            }
    }

    [Fact]
    public void CopyIsRefusedWithoutASelectionOrWithoutPixels()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        Assert.Null(SelectionClipboard.Copy(document, layer.ID));

        SelectionEdits.Select(document, SKRectI.Create(4, 4, 12, 12));
        using var blank = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var empty = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 40, 40), "Empty");
        blank.Layers.Add(empty);
        SelectionEdits.Select(blank, SKRectI.Create(4, 4, 12, 12));
        Assert.Null(SelectionClipboard.Copy(blank, empty.ID));
        // And the layer's own copy still works, so the second case is not the selection's fault.
        Assert.NotNull(SelectionClipboard.Copy(document, layer.ID));
    }

    [Fact]
    public void CutClearsTheSelectionAndKeepsWhatWasOutsideIt()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(4, 4, 12, 12));
        Assert.True(SelectionClipboard.Cut(document, layer.ID, out var copied));
        using var _copied = copied;
        Assert.NotNull(copied);

        // Inside the selection the layer is now empty; outside it, exactly what it was.
        Assert.Equal(0, layer.Asset!.Image.GetPixel(10, 10).Alpha);
        Assert.Equal(0, layer.Asset.Image.GetPixel(4, 4).Alpha);
        Assert.Equal(new SKColor(40, 60, 220), WithoutAlpha(layer.Asset.Image.GetPixel(20, 10)));
        Assert.Equal(new SKColor(40, 60, 220), WithoutAlpha(layer.Asset.Image.GetPixel(30, 10)));
        // And the cut pixels are what the copy holds.
        Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(At(copied, 10, 10)));
    }

    [Fact]
    public void PastePutsTheClipboardBackAsALayerWhereItCameFrom()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(4, 4, 12, 12));
        using var copied = SelectionClipboard.Copy(document, layer.ID);
        Assert.NotNull(copied);

        var made = SelectionClipboard.Paste(document, copied, layer.ID);
        Assert.NotNull(made);
        var pasted = document.Layers.First(entry => entry.ID == made);
        Assert.Equal(new LayerTransform(copied.Region.Left, copied.Region.Top, copied.Region.Width, copied.Region.Height),
            pasted.Transform);
        Assert.Equal(copied.Region.Width, pasted.Asset!.Width);
        Assert.Equal("Pasted Layer 1", pasted.Name);
        // Above the layer it came from.
        Assert.Equal(1, document.Layers.IndexOf(pasted));

        // The clipboard keeps its pixels: pasting twice gives two layers.
        var again = SelectionClipboard.Paste(document, copied, made);
        Assert.NotNull(again);
        Assert.Equal(3, document.Layers.Count);
        Assert.Equal(new SKColor(220, 40, 40),
            WithoutAlpha(document.Layers.First(entry => entry.ID == again).Asset!.Image.GetPixel(
                copied.Region.Width / 2, copied.Region.Height / 2)));
    }

    [Fact]
    public void CopyMergedTakesWhatIsDrawnRatherThanOneLayer()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        // A blue square above the layer, covering the middle of the selection.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(12, 12));
        bitmap.Erase(new SKColor(20, 200, 40));
        var above = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Green"),
            new LayerTransform(14, 14, 12, 12), "Green");
        document.Layers.Add(above);
        SelectionEdits.Select(document, SKRectI.Create(10, 10, 20, 20));

        using var copied = SelectionClipboard.CopyMerged(document);
        Assert.NotNull(copied);
        // Document (20,20) is inside the green square, which is drawn over the split layer.
        Assert.Equal(new SKColor(20, 200, 40), WithoutAlpha(At(copied, 20, 20)));
        // And a part of the selection away from the square is the split layer's own colour.
        Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(At(copied, 12, 12)));

        // Copying one layer instead gives what that layer holds there, not what is drawn.
        using var single = SelectionClipboard.Copy(document, layer.ID);
        Assert.NotNull(single);
        Assert.Equal(new SKColor(40, 60, 220), WithoutAlpha(At(single, 20, 20)));
    }

    [Fact]
    public void LayerViaCopyMakesALayerAndLeavesTheOriginal()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(4, 4, 12, 12));
        var made = SelectionClipboard.LayerViaCopy(document, layer.ID, layer.ID);
        Assert.NotNull(made);
        var copy = document.Layers.First(entry => entry.ID == made);
        // It sits over the layer it was taken from, holding those pixels at the place they came from: the
        // selection's bounds rounded out, at the same size as the pixels.
        Assert.Equal(1, document.Layers.IndexOf(copy));
        Assert.Equal(copy.Transform.Width, copy.Asset!.Width, 3);
        Assert.Equal(copy.Transform.Height, copy.Asset.Height, 3);
        Assert.True(copy.Transform.X <= 4 && copy.Transform.Y <= 4, $"the copy misses the selection: {copy.Transform}");
        Assert.True(copy.Transform.X + copy.Transform.Width >= 16, $"the copy misses the selection: {copy.Transform}");
        Assert.Equal(255, copy.Asset.Image.GetPixel(6, 6).Alpha);

        // The original is untouched, so the document still renders as it did.
        Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(layer.Asset!.Image.GetPixel(10, 10)));
        using var rendered = Rendering.DocumentRenderer.Render(document);
        Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(rendered.GetPixel(10, 10)));
    }

    [Fact]
    public void LayerViaCopyIsRefusedWithoutASelection()
    {
        var (document, layer) = Split(40);
        using var _ = document;
        Assert.Null(SelectionClipboard.LayerViaCopy(document, layer.ID, layer.ID));
        Assert.Single(document.Layers);
    }

    /// <summary>The colour without its alpha, since a copy may be partly covered at its edge.</summary>
    private static SKColor WithoutAlpha(SKColor colour) => new(colour.Red, colour.Green, colour.Blue);

    /// <summary>A pixel of the copy, named by where it was on the document: the copy's own bounds are the
    /// selection's rounded out, so its first pixel is not the selection's first pixel.</summary>
    private static SKColor At(ClipboardImage copied, int documentX, int documentY) =>
        copied.Image.GetPixel(documentX - copied.Region.Left, documentY - copied.Region.Top);
}
