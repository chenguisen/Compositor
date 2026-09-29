using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The Camera Raw panel's readout: the colour of the pixel under the pointer. What is checked here is where a
/// document point is read from — inside the layer's own grid, through its turn and its mirror — and that a
/// point off the picture, or over a transparent pixel, names nothing.
/// </summary>
public class CameraRawSampleTests
{
    /// <summary>A layer of one pixel per colour given, laid out along its own x axis.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Strip(params SKColor[] colours)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(colours.Length, 1));
        for (var x = 0; x < colours.Length; x++) bitmap.SetPixel(x, 0, colours[x]);
        var document = new CanvasDocument(Guid.NewGuid(), colours.Length, 1);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Strip"),
            new LayerTransform(0, 0, colours.Length, 1), "Strip");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void APointOffThePictureNamesNothing()
    {
        var (document, layer) = Strip(SKColors.Red, SKColors.Red);
        using var _ = document;
        Assert.Null(CameraRawSample.Under(layer, new SKPoint(-1, -1)));
        Assert.Null(CameraRawSample.Under(layer, new SKPoint(2, 0.5f)));
        Assert.Null(CameraRawSample.Under(layer, new SKPoint(0.5f, 5)));
    }

    [Fact]
    public void ATransparentPixelNamesNothing()
    {
        var (document, layer) = Strip(new SKColor(10, 20, 30, 0), new SKColor(40, 50, 60));
        using var _ = document;
        Assert.Null(CameraRawSample.Under(layer, new SKPoint(0.5f, 0.5f)));
        Assert.Equal((40, 50, 60), CameraRawSample.Under(layer, new SKPoint(1.5f, 0.5f)));
    }

    [Fact]
    public void ThePlacementPutsAPointWhereItIsReadBackFrom()
    {
        // A turned, offset box: what the drawing does to place a point is what the readout undoes.
        var placement = new LayerTransform(30, 20, 40, 20, 37);
        foreach (var (u, v) in new[] { (0.1, 0.1), (0.5, 0.5), (0.9, 0.9), (0.25, 0.75) })
        {
            var at = placement.InBox(placement.Point(u, v));
            Assert.NotNull(at);
            Assert.Equal(u * placement.Width, at!.Value.X, 3);
            Assert.Equal(v * placement.Height, at.Value.Y, 3);
        }
        Assert.Null(placement.InBox(new SKPoint(-1000, -1000)));
    }

    [Fact]
    public void AFlipReadsThePixelThatIsShownThere()
    {
        // A layer mirrored across its own middle: the pixel drawn at the left of the box is the asset's last
        // one, so that is the one the readout has to name.
        var (document, layer) = Strip(new SKColor(10, 0, 0), new SKColor(20, 0, 0),
            new SKColor(30, 0, 0), new SKColor(40, 0, 0));
        using var _ = document;
        // The middle of the first pixel the box shows, with and without the mirror.
        Assert.Equal(10, CameraRawSample.Under(layer, layer.Transform.Point(0.125, 0.5))!.Value.Red);
        layer.Transform = layer.Transform with { FlipX = true };
        Assert.Equal(40, CameraRawSample.Under(layer, layer.Transform.Point(0.125, 0.5))!.Value.Red);
        Assert.Equal(10, CameraRawSample.Under(layer, layer.Transform.Point(0.875, 0.5))!.Value.Red);
    }
}
