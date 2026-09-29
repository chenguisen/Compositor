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
    /// <summary>A layer of one flat colour with a 10 x 10 patch of another in the middle, and the patch selected.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Patch()
    {
        var document = LayerPlacement.NewDocument(40, 30)
            ?? throw new InvalidOperationException("no document");
        var layer = document.Layers[0];
        var pixels = Bitmaps.Allocate(Bitmaps.ColorInfo(40, 30));
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(new SKColor(20, 20, 20));
            using var paint = new SKPaint { Color = new SKColor(240, 0, 0), IsAntialias = false };
            canvas.DrawRect(SKRect.Create(15, 10, 10, 10), paint);
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
        var before = Image(document).GetPixel(20, 15);
        Assert.Equal(240, before.Red);

        Assert.True(SelectionEdits.MovePixels(document, layer.ID, 12, 0));

        // Where the patch was is now the background, and where it went is the patch.
        Assert.Equal(20, Image(document).GetPixel(20, 15).Red);
        Assert.Equal(240, Image(document).GetPixel(32, 15).Red);
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
}
