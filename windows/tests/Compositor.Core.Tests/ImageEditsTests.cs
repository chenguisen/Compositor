using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Image size: the canvas and every layer's pixels are resampled, and transforms are baked in.</summary>
public class ImageEditsTests
{
    private static ImageLayer Layer(double x, double y, int width, int height, double rotation = 0,
        SKColor? colour = null)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour ?? SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Model.LayerTransform(x, y, width, height, rotation), "Layer");
    }

    private static CanvasDocument Document()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 40, 30);
        document.Layers.Add(Layer(5, 6, 20, 10));
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 10 });
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 12 });
        return document;
    }

    [Fact]
    public void ImageSizeResamplesTheCanvasAndEveryLayer()
    {
        using var document = Document();
        Assert.True(ImageEdits.Resize(document, 20, 15, 300));

        Assert.Equal(20, document.Width);
        Assert.Equal(15, document.Height);
        Assert.Equal(300, document.Resolution);
        // The layer is drawn again into the box its scaled rectangle covers: 5,6 20x10 halves to 2.5,3 10x5,
        // and the corners are rounded outwards.
        var layer = document.Layers[0];
        Assert.Equal(2, layer.Transform.X);
        Assert.Equal(3, layer.Transform.Y);
        Assert.Equal(11, layer.Transform.Width);
        Assert.Equal(5, layer.Transform.Height);
        Assert.Equal(11, layer.Asset!.Width);
        Assert.Equal(5, layer.Asset.Height);
        // The picture is the layer's colour, resampled.
        Assert.Equal(255, layer.Asset.Image.GetPixel(5, 2).Red);
    }

    [Fact]
    public void ImageSizeBakesARotatedLayerIntoAnUprightBox()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(10, 10, 20, 10, rotation: 90);
        document.Layers.Add(layer);

        Assert.True(ImageEdits.Resize(document, 20, 20, 72));

        // A quarter turn is drawn into the pixels: the rectangle that stood 10 by 20 now stands upright.
        Assert.Equal(0, document.Layers[0].Transform.Rotation);
        var drawn = document.Layers[0].Asset!;
        Assert.Equal(document.Layers[0].Transform.Width, drawn.Width);
        Assert.Equal(document.Layers[0].Transform.Height, drawn.Height);
        // Ten wide and twenty tall, halved, is five by ten within a pixel of rounding.
        Assert.InRange(document.Layers[0].Transform.Width, 5, 7);
        Assert.InRange(document.Layers[0].Transform.Height, 10, 12);
    }

    [Fact]
    public void ImageSizeScalesTheGuides()
    {
        using var document = Document();
        Assert.True(ImageEdits.Resize(document, 20, 15, 72));
        Assert.Equal(5, document.Guides[0].Position);
        Assert.Equal(6, document.Guides[1].Position);
    }

    [Fact]
    public void ImageSizeResamplesALayersMaskWithIt()
    {
        using var document = Document();
        var maskPixels = new SKBitmap(Bitmaps.MaskInfo(20, 10));
        maskPixels.Erase(new SKColor(200, 200, 200));
        document.Layers[0].Mask = Model.LayerMask.AssetFrom(maskPixels);

        Assert.True(ImageEdits.Resize(document, 20, 15, 72));

        var mask = document.Layers[0].Mask!;
        Assert.Equal(11, mask.Asset.Width);
        Assert.Equal(5, mask.Asset.Height);
        Assert.Equal(SKColorType.Gray8, mask.Asset.Image.ColorType);
    }

    [Fact]
    public void AUniformMaskIsLeftAloneBecauseItSaysTheSameThingAtAnySize()
    {
        using var document = Document();
        var uniform = new SKBitmap(Bitmaps.MaskInfo(1, 1));
        uniform.Erase(new SKColor(255, 255, 255));
        document.Layers[0].Mask = Model.LayerMask.AssetFrom(uniform);

        Assert.True(ImageEdits.Resize(document, 20, 15, 72));

        var kept = document.Layers[0].Mask!.Asset;
        Assert.Equal(1, kept.Width);
        Assert.Equal(1, kept.Height);
    }

    [Fact]
    public void ChangingOnlyTheResolutionKeepsThePixels()
    {
        using var document = Document();
        var asset = document.Layers[0].Asset;

        Assert.True(ImageEdits.Resize(document, 40, 30, 300));

        Assert.Equal(300, document.Resolution);
        Assert.Same(asset, document.Layers[0].Asset);
        Assert.Equal(5, document.Layers[0].Transform.X);
    }

    [Fact]
    public void ASizeTheDocumentCannotHoldIsRefusedAndChangesNothing()
    {
        using var document = Document();
        Assert.False(ImageEdits.Resize(document, 0, 15, 72));
        Assert.False(ImageEdits.Resize(document, 20, DocumentLimits.MaxSide + 1, 72));
        Assert.False(ImageEdits.Resize(document, 20, 15, 0));
        Assert.False(ImageEdits.Resize(document, 40, 30, 72));
        Assert.Equal(40, document.Width);
        Assert.Equal(30, document.Height);
        Assert.Equal(5, document.Layers[0].Transform.X);
        Assert.Equal(20, document.Layers[0].Asset!.Width);
    }

    [Fact]
    public void ImageSizeIsOneUndoStepAndUndoFindsTheOldPixelsStillThere()
    {
        using var document = Document();
        var history = new DocumentHistory();
        var original = document.Layers[0].Asset;

        history.Begin("Image Size", document, document.Layers[0].ID);
        Assert.True(ImageEdits.Resize(document, 20, 15, 72));
        history.End(document, document.Layers[0].ID);

        Assert.Equal(20, document.Width);
        var restored = history.Undo();
        Assert.NotNull(restored);
        Assert.Equal(40, restored!.Value.Document!.Width);
        Assert.Equal(30, restored.Value.Document!.Height);
        Assert.Equal(5, restored.Value.Document!.Layers[0].Transform.X);
        // The snapshot still names the picture that was there before the resize.
        Assert.Same(original, restored.Value.Document!.Layers[0].Asset);
        Assert.Equal(20, original!.Image.Width);
    }
}
