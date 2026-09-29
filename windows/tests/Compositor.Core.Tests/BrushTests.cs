using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Painting a stroke into a layer's pixels.</summary>
public class BrushTests
{
    /// <summary>A layer of one flat colour, its pixel grid the same size as the document it fills.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Flat(int width, int height, SKColor colour,
        double rotation = 0, bool flipX = false)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Paint"),
            new Model.LayerTransform(0, 0, width, height, rotation, flipX, false), "Paint");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor At(ImageLayer layer, int x, int y) => layer.Asset!.Image.GetPixel(x, y);

    [Fact]
    public void AFolderIsNeverGivenPixels()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 8, 8);
        var folder = new ImageLayer(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), "Folder")
        {
            IsGroup = true,
        };
        document.Layers.Add(folder);
        var stroke = new[] { new SKPoint(4, 4) };

        // A folder has no pixels of its own, so a stroke leaves it as it is rather than making a document
        // that could not be saved.
        Assert.False(BrushEdits.EnsurePixels(document, folder.ID));
        Assert.False(BrushEdits.Paint(document, folder.ID, stroke, new BrushSettings(Diameter: 4, Red: 1)));
        Assert.Null(folder.Asset);
    }

    [Fact]
    public void ADabPaintsACircleOfTheBrushSizeAndNoMore()
    {
        var (document, layer) = Flat(40, 40, SKColors.White);
        using var _ = document;
        // Centred on pixel (20, 20), whose middle is at (20.5, 20.5) in document pixels.
        var stroke = new[] { new SKPoint(20.5f, 20.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, new BrushSettings(Diameter: 10, Red: 1, Opacity: 1)));

        Assert.Equal(255, At(layer, 20, 20).Red);
        Assert.Equal(0, At(layer, 20, 20).Green);
        // Four pixels out is inside the radius of five; six out is outside it.
        Assert.Equal(0, At(layer, 20, 16).Green);
        Assert.Equal(255, At(layer, 20, 14).Green);
        Assert.Equal(255, At(layer, 14, 20).Green);
    }

    [Fact]
    public void AHardenTipPaintsRightUpToTheRimAndASoftOneFadesBeforeIt()
    {
        var (hardDocument, hardLayer) = Flat(40, 40, SKColors.White);
        using var _ = hardDocument;
        BrushEdits.Paint(hardDocument, hardLayer.ID, [new SKPoint(20.5f, 20.5f)],
            new BrushSettings(Diameter: 10, Hardness: 1, Red: 1));
        // Four and a half from the middle is inside a radius of five, so a hard tip is at full strength.
        Assert.Equal(0, At(hardLayer, 20, 16).Green);

        var (softDocument, softLayer) = Flat(40, 40, SKColors.White);
        using var __ = softDocument;
        BrushEdits.Paint(softDocument, softLayer.ID, [new SKPoint(20.5f, 20.5f)],
            new BrushSettings(Diameter: 10, Hardness: 0, Red: 1));
        // The same pixel is part way down the falloff, so red is there but the white shows through.
        var soft = At(softLayer, 20, 16);
        Assert.True(soft.Green is > 0 and < 255, $"expected a partial blend, got {soft}");
    }

    [Fact]
    public void ErasingTakesTheAlphaAwayInsteadOfPaintingColour()
    {
        var (document, layer) = Flat(20, 20, new SKColor(10, 20, 30, 255));
        using var _ = document;
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(10.5f, 10.5f)],
            new BrushSettings(Diameter: 8, Opacity: 1, Erasing: true)));

        Assert.Equal(0, At(layer, 10, 10).Alpha);
        Assert.Equal(255, At(layer, 0, 0).Alpha);
    }

    [Fact]
    public void TwoPointsFarApartAreJoinedWithNoGap()
    {
        var (document, layer) = Flat(60, 20, SKColors.White);
        using var _ = document;
        var stroke = new[] { new SKPoint(10.5f, 10.5f), new SKPoint(50.5f, 10.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, new BrushSettings(Diameter: 8, Red: 1)));

        // Every pixel along the line is painted, so the dabs really were laid end to end.
        for (var x = 10; x <= 50; x++) Assert.Equal(255, At(layer, x, 10).Red);
        Assert.Equal(255, At(layer, 5, 10).Green);
    }

    [Fact]
    public void OverlappingDabsStayWithinTheStrokesOpacity()
    {
        var (document, layer) = Flat(20, 20, SKColors.White);
        using var _ = document;
        // Three dabs at the same spot. If their coverage added up the middle would be darker than one dab;
        // capped at the stroke's half, half the white shows through the red instead.
        var stroke = new[] { new SKPoint(10.5f, 10.5f), new SKPoint(10.5f, 10.5f), new SKPoint(10.5f, 10.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, new BrushSettings(Diameter: 8, Red: 1, Opacity: 0.5)));

        var painted = At(layer, 10, 10);
        Assert.Equal(255, painted.Red);
        Assert.InRange(painted.Green, 126, 130);
        Assert.Equal(255, painted.Alpha);
    }

    [Fact]
    public void PaintingGivesTheLayerANewPictureAndLeavesTheOldOne()
    {
        var (document, layer) = Flat(20, 20, SKColors.White);
        using var _ = document;
        var original = layer.Asset!;
        var originalPixels = original.Image;

        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(10.5f, 10.5f)],
            new BrushSettings(Diameter: 8, Red: 1)));

        Assert.NotSame(original, layer.Asset);
        // The picture that was there is untouched, which is what undo brings back.
        Assert.Equal(255, originalPixels.GetPixel(10, 10).Green);
        Assert.Equal(0, At(layer, 10, 10).Green);
    }

    [Fact]
    public void AStrokeFollowsTheLayersFlip()
    {
        var (document, layer) = Flat(20, 20, SKColors.White, flipX: true);
        using var _ = document;
        // The layer is mirrored, so a dab near the document's left edge lands near the pixels' right edge.
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(5.5f, 10.5f)],
            new BrushSettings(Diameter: 6, Red: 1)));

        Assert.Equal(0, At(layer, 14, 10).Green);
        Assert.Equal(255, At(layer, 5, 10).Green);
    }

    [Fact]
    public void AStrokeOnARotatedLayerStaysRound()
    {
        // A layer turned a quarter of a turn, filling a square canvas, so the mapping is a rotation.
        var (document, layer) = Flat(20, 20, SKColors.White, rotation: 90);
        using var _ = document;
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(10.5f, 10.5f)],
            new BrushSettings(Diameter: 6, Red: 1)));

        // The middle is painted, and a pixel four away in either direction is not.
        Assert.Equal(0, At(layer, 10, 10).Green);
        Assert.Equal(255, At(layer, 14, 10).Green);
        Assert.Equal(255, At(layer, 10, 14).Green);
    }

    [Fact]
    public void AStrokeWithNoPointsOrNoSizeChangesNothing()
    {
        var (document, layer) = Flat(20, 20, SKColors.White);
        using var _ = document;
        Assert.False(BrushEdits.Paint(document, layer.ID, [], new BrushSettings()));
        Assert.False(BrushEdits.Paint(document, layer.ID, [new SKPoint(10, 10)], new BrushSettings(Diameter: 0)));
        Assert.False(BrushEdits.Paint(document, Guid.NewGuid(), [new SKPoint(10, 10)], new BrushSettings()));
    }
}
