using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Crop and canvas size: both move content rather than resampling it, and both undo in one step.</summary>
public class CanvasEditsTests
{
    private static ImageLayer Layer(double x, double y, int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Model.LayerTransform(x, y, width, height), "Layer");
    }

    /// <summary>A 40 by 40 canvas with one layer and two guides, each placed off centre.</summary>
    private static CanvasDocument Document()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        document.Layers.Add(Layer(10, 10, 8, 8));
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 12 });
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 14 });
        return document;
    }

    [Fact]
    public void CroppingMovesContentByTheCornersCorner()
    {
        using var document = Document();

        Assert.True(CanvasEdits.Crop(document, SKRectI.Create(5, 6, 20, 20)));

        Assert.Equal(20, document.Width);
        Assert.Equal(20, document.Height);
        // The layer keeps its pixels and its size; it only moves by the crop's corner.
        Assert.Equal(5, document.Layers[0].Transform.X);
        Assert.Equal(4, document.Layers[0].Transform.Y);
        Assert.Equal(8, document.Layers[0].Transform.Width);
        Assert.Equal(8, document.Layers[0].Asset!.Image.Width);
        Assert.Equal(7, document.Guides[0].Position);
        Assert.Equal(8, document.Guides[1].Position);
    }

    /// <summary>A 10 by 10 canvas with one layer in its top-left corner, so an anchor's offset shows plainly.</summary>
    private static CanvasDocument SmallDocument()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 10, 10);
        document.Layers.Add(Layer(0, 0, 4, 4));
        return document;
    }

    [Theory]
    // Anchor 0 is the top left, 2 the top right of that row, 4 the middle, 8 the bottom right. Growing puts
    // the extra pixel on the right and bottom, so 10 to 11 around the middle moves nothing at all.
    [InlineData(0, 14, 0, 0)]
    [InlineData(2, 14, 4, 0)]
    [InlineData(4, 14, 2, 2)]
    [InlineData(8, 14, 4, 4)]
    [InlineData(4, 11, 0, 0)]
    [InlineData(4, 13, 1, 1)]
    public void GrowingTheCanvasPutsTheContentWhereTheAnchorSays(int anchor, int size, int x, int y)
    {
        using var document = SmallDocument();
        Assert.True(CanvasEdits.Resize(document, size, size, anchor));
        Assert.Equal(x, document.Layers[0].Transform.X);
        Assert.Equal(y, document.Layers[0].Transform.Y);
        Assert.Equal(size, document.Width);
    }

    [Fact]
    public void ShrinkingAroundTheMiddleTakesTheExtraPixelFromTheTopLeft()
    {
        using var document = SmallDocument();
        // 10 down to 9 loses one pixel, taken from the top and left, so the content moves out by one.
        Assert.True(CanvasEdits.Resize(document, 9, 9, CanvasEdits.CentreAnchor));
        Assert.Equal(-1, document.Layers[0].Transform.X);
        Assert.Equal(-1, document.Layers[0].Transform.Y);
        Assert.Equal(9, document.Width);
    }

    [Fact]
    public void GrowingWithAFillAddsAColouredLayerBeneathTheArtwork()
    {
        using var document = SmallDocument();
        Assert.True(CanvasEdits.Resize(document, 14, 12, CanvasEdits.CentreAnchor, new CanvasFill(1, 0, 0)));

        Assert.Equal(2, document.Layers.Count);
        // The fill goes underneath everything.
        var extension = document.Layers[0];
        Assert.Equal("Canvas Extension", extension.Name);
        Assert.Equal(0, extension.Transform.X);
        Assert.Equal(0, extension.Transform.Y);
        Assert.Equal(14, extension.Transform.Width);
        Assert.Equal(12, extension.Transform.Height);
        // The old canvas's place is left transparent, so the artwork above shows through it.
        var (x, y) = CanvasEdits.AnchorOffset(CanvasEdits.CentreAnchor, 10, 10, 14, 12);
        Assert.Equal(2, x);
        Assert.Equal(1, y);
        Assert.Equal(255, extension.Asset!.Image.GetPixel(0, 0).Red);
        Assert.Equal(0, extension.Asset.Image.GetPixel(x + 1, y + 1).Alpha);
        Assert.Equal(2, document.Layers[1].Transform.X);
        Assert.Equal(1, document.Layers[1].Transform.Y);
    }

    [Fact]
    public void ACanvasSizeTheDocumentCannotHoldIsRefusedAndChangesNothing()
    {
        using var document = Document();
        Assert.False(CanvasEdits.Resize(document, 0, 10));
        Assert.False(CanvasEdits.Resize(document, 10, DocumentLimits.MaxSide + 1));
        Assert.False(CanvasEdits.Resize(document, 50, 50, anchor: 9));
        Assert.False(CanvasEdits.Crop(document, SKRectI.Create(5, 5, 0, 10)));
        // Refusals leave the document exactly as it was.
        Assert.Equal(40, document.Width);
        Assert.Equal(40, document.Height);
        Assert.Equal(10, document.Layers[0].Transform.X);
    }

    [Fact]
    public void AResizeToTheSizeItAlreadyIsChangesNothing()
    {
        using var document = Document();
        Assert.False(CanvasEdits.Resize(document, 40, 40, CanvasEdits.CentreAnchor));
    }

    [Fact]
    public void CroppingIsOneUndoStepAndUndoPutsTheCanvasBack()
    {
        using var document = Document();
        var history = new DocumentHistory();

        history.Begin("Crop", document, document.Layers[0].ID);
        Assert.True(CanvasEdits.Crop(document, SKRectI.Create(5, 6, 20, 20)));
        history.End(document, document.Layers[0].ID);

        Assert.Equal(20, document.Width);
        Assert.Equal(5, document.Layers[0].Transform.X);

        var restored = history.Undo();
        Assert.NotNull(restored);
        // Undo restores the canvas size as well as the layers.
        Assert.Equal(40, restored!.Value.Document!.Width);
        Assert.Equal(40, restored.Value.Document!.Height);
        Assert.Equal(10, restored.Value.Document!.Layers[0].Transform.X);
        Assert.Equal(12, restored.Value.Document!.Guides[0].Position);
    }
}
