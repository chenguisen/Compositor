using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Moving the pixels inside a selection: what Command with an arrow key does, and what dragging a selection's
/// contents does on the Mac. The pixels come out of where they were, land on the grid they are moved to, and the
/// outline goes with them.
/// </summary>
public class SelectionPixelsTests
{
    /// <summary>
    /// A layer with a 10 x 10 red patch in the middle, a 3 x 3 blue mark well away from it, and the patch
    /// selected. The mark is what proves a move carries the selection and nothing else: a whole layer put down
    /// again shifted — which is what an unguarded copy does — shows that mark twice.
    /// </summary>
    private static (CanvasDocument Document, ImageLayer Layer) Patch()
    {
        var document = LayerPlacement.NewDocument(40, 30)
            ?? throw new InvalidOperationException("no document");
        var layer = document.Layers[0];
        var pixels = Bitmaps.Allocate(Bitmaps.ColorInfo(40, 30));
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(new SKColor(20, 20, 20));
            using var paint = new SKPaint { IsAntialias = false };
            paint.Color = new SKColor(240, 0, 0);
            canvas.DrawRect(SKRect.Create(15, 10, 10, 10), paint);
            paint.Color = new SKColor(0, 0, 240);
            canvas.DrawRect(SKRect.Create(32, 24, 3, 3), paint);
        }
        layer.Asset = ImportedImage.Create(pixels, "pixels");
        // The patch selected, hard-edged so a pixel is either in it or out of it.
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(15, 10, 10, 10), antialiased: false));
        return (document, layer);
    }

    private static SKBitmap Image(CanvasDocument document) => document.Layers[0].Asset!.Image;

    [Fact]
    public void ThePixelsComeOutOfWhereTheyWereAndLandWhereTheyAreMoved()
    {
        var (document, layer) = Patch();
        using var _ = document;
        Assert.Equal(240, Image(document).GetPixel(20, 15).Red);

        // Moved left, so that a whole layer put down again shifted would land inside the picture where it can be
        // seen: that is what the mark away from the selection is for.
        Assert.True(SelectionEdits.MovePixels(document, layer.ID, -12, 0));

        // Where the patch was is a hole — the layer's own pixels there are gone, not the colour around them.
        Assert.Equal(0, Image(document).GetPixel(20, 15).Alpha);
        Assert.Equal(240, Image(document).GetPixel(8, 15).Red);
        // The mark away from the selection is where it was, and the place a shifted whole-layer copy would have
        // put it is still the background.
        Assert.Equal(240, Image(document).GetPixel(33, 25).Blue);
        Assert.Equal(20, Image(document).GetPixel(21, 25).Blue);
    }

    [Fact]
    public void TheOutlineGoesWithThePixels()
    {
        var (document, layer) = Patch();
        using var _ = document;
        var was = document.Selection.Path!.Bounds;
        Assert.True(SelectionEdits.MovePixels(document, layer.ID, 0, 8));
        var now = document.Selection.Path!.Bounds;
        Assert.Equal(was.Left, now.Left);
        Assert.Equal(was.Top + 8, now.Top);
    }

    [Fact]
    public void MovingItAgainAndAgainNeverResamplesIt()
    {
        // The whole reason the move is whole pixels: the pixels can be walked across the canvas without the
        // edge they have being blurred by one pass after another.
        var (document, layer) = Patch();
        using var _ = document;
        var solid = Image(document).GetPixel(20, 15);
        for (var step = 0; step < 8; step++)
        {
            Assert.True(SelectionEdits.MovePixels(document, layer.ID, 1, 1));
        }
        // Eight single-pixel moves and one eight-pixel move leave the same pixels behind.
        var (once, onceLayer) = Patch();
        using var _once = once;
        Assert.True(SelectionEdits.MovePixels(once, onceLayer.ID, 8, 8));

        Assert.Equal(solid, Image(document).GetPixel(28, 23));
        Assert.Equal(Image(once).GetPixel(28, 23), Image(document).GetPixel(28, 23));
    }

    [Fact]
    public void AMoveWithNothingToMoveIsNoEdit()
    {
        var (document, layer) = Patch();
        using var _ = document;
        Assert.False(SelectionEdits.MovePixels(document, layer.ID, 0, 0));
        Assert.True(SelectionEdits.Deselect(document));
        Assert.False(SelectionEdits.MovePixels(document, layer.ID, 3, 3));
    }

    [Fact]
    public void LiftingLeavesTheHoleAndHoldsThePixels()
    {
        var (document, layer) = Patch();
        using var _ = document;
        using var floating = SelectionEdits.LiftPixels(document, layer.ID);
        Assert.NotNull(floating);

        // The hole is in the layer straight away, so a drag shows it while it goes.
        Assert.Equal(0, Image(document).GetPixel(20, 15).Alpha);
        // And the pixels being carried are the patch: the cut is the size of the region the selection covers and
        // is aligned with the document, so its own pixel for document 20,15 holds the patch's colour.
        var cutX = 20 - (int)floating.CutAt.X;
        var cutY = 15 - (int)floating.CutAt.Y;
        Assert.Equal(240, floating.Cut.GetPixel(cutX, cutY).Red);
        // Its corner is on the selection's, give or take the pixel a coverage region may be rounded out by.
        Assert.True(Math.Abs(floating.CutAt.X - 15) <= 1 && Math.Abs(floating.CutAt.Y - 10) <= 1,
            $"the cut starts at {floating.CutAt.X},{floating.CutAt.Y}, not on the selection");
    }

    [Fact]
    public void WhatWasLiftedIsPutDownWhereTheDragEnds()
    {
        var (document, layer) = Patch();
        using var _ = document;
        var floating = SelectionEdits.LiftPixels(document, layer.ID);
        Assert.NotNull(floating);
        Assert.True(SelectionEdits.SettlePixels(document, floating, 12, 4));

        Assert.Equal(0, Image(document).GetPixel(20, 15).Alpha);
        Assert.Equal(240, Image(document).GetPixel(32, 19).Red);
        // The outline came with them.
        Assert.Equal(27, document.Selection.Path!.Bounds.Left);
    }

    [Fact]
    public void ADragThatComesToNothingPutsTheLayerBackExactly()
    {
        // A cancel must leave no trace at all: the pixels, the outline and the layer's own picture.
        var (document, layer) = Patch();
        using var _ = document;
        var before = Image(document);
        var was = System.Security.Cryptography.SHA256.HashData(Pixels(before));
        var outline = document.Selection.Path!.Bounds;

        var floating = SelectionEdits.LiftPixels(document, layer.ID);
        Assert.NotNull(floating);
        SelectionEdits.DropPixels(document, floating);

        var after = System.Security.Cryptography.SHA256.HashData(Pixels(Image(document)));
        Assert.Equal(was, after);
        Assert.Equal(outline, document.Selection.Path!.Bounds);
    }

    /// <summary>A layer's pixels as bytes, whatever the row padding is.</summary>
    private static byte[] Pixels(SKBitmap bitmap)
    {
        var tight = new byte[bitmap.Width * bitmap.Height * 4];
        var source = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            source.Slice(y * bitmap.RowBytes, bitmap.Width * 4).CopyTo(tight.AsSpan(y * bitmap.Width * 4));
        }
        return tight;
    }
}
