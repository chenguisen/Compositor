using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The filters that are not Camera Raw — vignette, tonal contrast and lens correction — run over a layer's
/// own pixels, held to the selection, each as one edit. The kernels are covered by the pixel tests; these
/// check the pipeline around them and what each filter does to the picture.
/// </summary>
public class FilterEditsTests
{
    private static (CanvasDocument Document, ImageLayer Layer) Flat(int width, int height, SKColor colour)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
            new LayerTransform(0, 0, width, height), "Flat");
        document.Layers.Add(layer);
        return (document, layer);
    }

    /// <summary>Colour that grows with the distance from the middle, so a warp changes it everywhere but there.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Radial(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        var middle = side / 2.0;
        var most = Math.Sqrt(2) * middle;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dx = x + 0.5 - middle;
                var dy = y + 0.5 - middle;
                var level = (byte)Math.Round(Math.Sqrt(dx * dx + dy * dy) / most * 255, MidpointRounding.AwayFromZero);
                bitmap.SetPixel(x, y, new SKColor(level, level, level));
            }
        }
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Radial"),
            new LayerTransform(0, 0, side, side), "Radial");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor Middle(ImageLayer layer) => layer.Asset!.Image.GetPixel(layer.Asset.Width / 2, layer.Asset.Height / 2);
    private static SKColor Corner(ImageLayer layer) => layer.Asset!.Image.GetPixel(0, 0);

    [Fact]
    public void DefaultsAreTheFiltersOwnAndMostOfThemDoSomething()
    {
        var settings = new FilterSettings();
        // The vignette's defaults are not nothing, so a plain bag is not an identity the way Camera Raw's is.
        Assert.True(settings.DoesAnything(FilterKind.Vignette));
        Assert.True(settings.DoesAnything(FilterKind.TonalContrast));
        Assert.False(settings.DoesAnything(FilterKind.LensCorrection));
        Assert.True(settings.IsValid(FilterKind.Vignette));
        Assert.True(settings.IsValid(FilterKind.TonalContrast));
        Assert.True(settings.IsValid(FilterKind.LensCorrection));
    }

    [Fact]
    public void AnOutOfRangeSettingIsRefusedAndTheLayerIsLeftAlone()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings { VignetteAmount = 150 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.TonalContrast, new FilterSettings { TonalRadius = 0 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.LensCorrection, new FilterSettings { Distortion = 101 }));
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void AFilterWithNothingToDoIsRefused()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        // A zero amount, and a lens with no distortion, would only cost time.
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings { VignetteAmount = 0 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.LensCorrection, new FilterSettings { Distortion = 0 }));
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void AVignetteDarkensTheCornersAndLeavesTheMiddle()
    {
        var (document, layer) = Flat(60, 60, new SKColor(180, 180, 180));
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings()));
        Assert.True(Corner(layer).Red < Middle(layer).Red,
            $"the corner {Corner(layer).Red} is not darker than the middle {Middle(layer).Red}");
        Assert.True(Math.Abs(Middle(layer).Red - 180) <= 4, $"the middle became {Middle(layer).Red}");
    }

    [Fact]
    public void AVignettePaintsTheEdgesTowardsItsOwnColour()
    {
        var (document, layer) = Flat(60, 60, new SKColor(180, 180, 180));
        using var _ = document;
        var settings = new FilterSettings { VignetteAmount = 100, VignetteRed = 1, VignetteGreen = 0, VignetteBlue = 0 };
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, settings));
        var corner = Corner(layer);
        Assert.True(corner.Red > corner.Green && corner.Red > corner.Blue, $"the corner is {corner}");
    }

    [Fact]
    public void TonalContrastPushesTheDetailAwayFromItsBlurredSelf()
    {
        // A bright block on a dark field: at the block's middle the local detail is positive, so the pixel
        // brightens; out in the flat field there is none, so it stays.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(60, 60));
        bitmap.Erase(new SKColor(60, 60, 60));
        for (var y = 24; y < 36; y++)
            for (var x = 24; x < 36; x++)
                bitmap.SetPixel(x, y, new SKColor(220, 220, 220));
        using var document = new CanvasDocument(Guid.NewGuid(), 60, 60);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Block"),
            new LayerTransform(0, 0, 60, 60), "Block");
        document.Layers.Add(layer);

        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.TonalContrast, new FilterSettings()));
        Assert.True(layer.Asset!.Image.GetPixel(30, 30).Red > 220, $"the block's middle became {layer.Asset.Image.GetPixel(30, 30).Red}");
        Assert.True(Math.Abs(layer.Asset.Image.GetPixel(4, 4).Red - 60) <= 2, "the flat field was changed");
    }

    [Fact]
    public void LensCorrectionMovesTheEdgePixelsAndHoldsTheMiddle()
    {
        var (document, layer) = Radial(80);
        using var _ = document;
        var beforeCorner = Corner(layer).Red;
        var beforeMiddle = Middle(layer).Red;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.LensCorrection, new FilterSettings { Distortion = 100 }));
        // Pulled inward, the corner samples a smaller radius, so it darkens; the middle does not move.
        Assert.True(Corner(layer).Red < beforeCorner, $"the corner went {beforeCorner} to {Corner(layer).Red}");
        Assert.True(Math.Abs(Middle(layer).Red - beforeMiddle) <= 2, $"the middle went {beforeMiddle} to {Middle(layer).Red}");
    }

    [Fact]
    public void TheOppositeDistortionPushesTheCornersOutOfTheFrame()
    {
        var (document, layer) = Radial(80);
        using var _ = document;
        Assert.Equal(255, Corner(layer).Alpha);
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.LensCorrection, new FilterSettings { Distortion = -100 }));
        // Pushed outward, the corner samples from beyond the picture, so it has nothing there.
        Assert.Equal(0, Corner(layer).Alpha);
    }

    [Fact]
    public void AFilterInsideASelectionLeavesTheRestAlone()
    {
        var (document, layer) = Flat(60, 60, new SKColor(180, 180, 180));
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 30, 60));
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings { VignetteAmount = 100 }));

        // Inside the selection the corner is darkened; the far corner, outside it, is exactly what it was.
        Assert.True(layer.Asset!.Image.GetPixel(2, 2).Red < 180, "the selected corner was not filtered");
        Assert.Equal(180, layer.Asset.Image.GetPixel(57, 2).Red);
        Assert.Equal(180, layer.Asset.Image.GetPixel(59, 59).Red);
    }

    [Fact]
    public void TheFilteredPixelsAreHeldTheWayALayerHoldsThem()
    {
        var (document, layer) = Flat(24, 24, new SKColor(200, 60, 40));
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings()));
        Assert.Equal(SKAlphaType.Unpremul, layer.Asset!.Image.AlphaType);
        Assert.Equal(24, layer.Asset.Width);
        Assert.Equal(24, layer.Asset.Height);
        Assert.Equal("Flat", layer.Name);
    }

    [Fact]
    public void ATranslucentLayerKeepsItsAlpha()
    {
        var (document, layer) = Flat(24, 24, new SKColor(200, 60, 40, 128));
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings()));
        Assert.Equal(128, Middle(layer).Alpha);
    }

    [Fact]
    public void ALayerWithNoPixelsIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        Assert.False(FilterEdits.Apply(document, blank.ID, FilterKind.Vignette, new FilterSettings()));
    }

    [Fact]
    public void ALayerThatIsNotThereIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        Assert.False(FilterEdits.Apply(document, Guid.NewGuid(), FilterKind.Vignette, new FilterSettings()));
    }

    [Fact]
    public void AFilterWorksOnTheLayersOwnGridWhateverItsTransform()
    {
        var (document, layer) = Flat(16, 16, new SKColor(180, 180, 180));
        using var _ = document;
        // A layer stretched over a huge rectangle is still only sixteen by sixteen pixels.
        layer.Transform = new LayerTransform(0, 0, 8_000, 8_000);
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.Vignette, new FilterSettings()));
        Assert.Equal(16, layer.Asset!.Width);
        Assert.Equal(16, layer.Asset.Height);
        Assert.True(layer.Asset.Image.GetPixel(0, 0).Red < 180);
    }
}
