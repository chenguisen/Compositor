using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The colour adjustments run from the Image menu: an adjustment applied over a layer's own pixels, held to
/// the selection, as one edit. What the operators do is covered by the pixel tests; these check the surface
/// that reaches them.
/// </summary>
public class ImageAdjustmentTests
{
    private static (CanvasDocument Document, ImageLayer Layer) Warm(int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(new SKColor(200, 60, 40));
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Warm"),
            new LayerTransform(0, 0, width, height), "Warm");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor Middle(ImageLayer layer) => layer.Asset!.Image.GetPixel(layer.Asset.Width / 2, layer.Asset.Height / 2);

    [Fact]
    public void InvertTurnsThePixelsOverAndHoldsThemTheWayALayerDoes()
    {
        var (document, layer) = Warm(20, 20);
        using var _ = document;
        Assert.True(FilterEdits.ApplyAdjustment(document, layer.ID, new LayerAdjustment { Kind = AdjustmentKind.Invert }));
        var pixel = Middle(layer);
        Assert.Equal(55, pixel.Red);
        Assert.Equal(195, pixel.Green);
        Assert.Equal(215, pixel.Blue);
        Assert.Equal(SKAlphaType.Unpremul, layer.Asset!.Image.AlphaType);
        Assert.Equal(20, layer.Asset.Width);
    }

    [Fact]
    public void ExposureLightensAndBlackAndWhiteTakesTheColourOut()
    {
        var (lit, litLayer) = Warm(20, 20);
        using var _lit = lit;
        Assert.True(FilterEdits.ApplyAdjustment(lit, litLayer.ID,
            new LayerAdjustment { Kind = AdjustmentKind.Exposure, ExposureSettings = new ExposureSettings { Exposure = 1 } }));
        Assert.True(Middle(litLayer).Red > 200, $"brighter gave {Middle(litLayer)}");

        var (grey, greyLayer) = Warm(20, 20);
        using var _grey = grey;
        Assert.True(FilterEdits.ApplyAdjustment(grey, greyLayer.ID, new LayerAdjustment { Kind = AdjustmentKind.BlackWhite }));
        var pixel = Middle(greyLayer);
        Assert.True(pixel.Red == pixel.Green && pixel.Green == pixel.Blue, $"still coloured: {pixel}");
    }

    [Fact]
    public void AnAdjustmentISRefusedWhenTheAmountsAreNotOnesItMayUse()
    {
        var (document, layer) = Warm(20, 20);
        using var _ = document;
        Assert.False(FilterEdits.ApplyAdjustment(document, layer.ID,
            new LayerAdjustment { Kind = AdjustmentKind.Exposure, ExposureSettings = new ExposureSettings { Exposure = 99 } }));
        Assert.Equal(new SKColor(200, 60, 40), Middle(layer));
    }

    [Fact]
    public void AnAdjustmentNeedsALayerWithPixelsOfItsOwn()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        Assert.False(FilterEdits.ApplyAdjustment(document, blank.ID, new LayerAdjustment { Kind = AdjustmentKind.Invert }));

        // An adjustment layer holds no pixels either: its effect is drawn as the stack is composited.
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Invert, blank.ID)!.Value;
        Assert.False(FilterEdits.ApplyAdjustment(document, made, new LayerAdjustment { Kind = AdjustmentKind.Invert }));
    }

    [Fact]
    public void AnAdjustmentInsideASelectionLeavesTheRestAlone()
    {
        var (document, layer) = Warm(40, 20);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 20, 20));
        Assert.True(FilterEdits.ApplyAdjustment(document, layer.ID, new LayerAdjustment { Kind = AdjustmentKind.Invert }));
        Assert.Equal(55, layer.Asset!.Image.GetPixel(10, 10).Red);
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset.Image.GetPixel(30, 10));
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset.Image.GetPixel(39, 19));
    }

    [Fact]
    public void ABlurAdjustmentIsGivenRoomPastTheEdgeAndTrimmedBackAgain()
    {
        // Half a layer opaque down its full height, so a blur can only have grown it by spreading past the
        // edge; the empty half is trimmed away again.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(SKColors.Transparent);
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                bitmap.SetPixel(x, y, SKColors.White);
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Half"),
            new LayerTransform(0, 0, 40, 20), "Half");
        document.Layers.Add(layer);

        Assert.True(FilterEdits.ApplyAdjustment(document, layer.ID,
            new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 3 }));
        Assert.True(layer.Asset!.Height > 20, $"the blur did not spread past the edge: {layer.Asset.Height}");
        Assert.True(layer.Asset.Width < 40, $"the empty half was not trimmed: {layer.Asset.Width}");
    }

    [Fact]
    public void GrainWithTheSameSeedIsTheSamePattern()
    {
        var (first, firstLayer) = Warm(40, 40);
        using var _first = first;
        var grain = new LayerAdjustment { Kind = AdjustmentKind.Grain, GrainSettings = new GrainSettings { Amount = 60, Seed = 4242 } };
        Assert.True(FilterEdits.ApplyAdjustment(first, firstLayer.ID, grain));

        var (second, secondLayer) = Warm(40, 40);
        using var _second = second;
        Assert.True(FilterEdits.ApplyAdjustment(second, secondLayer.ID, grain));
        for (var y = 0; y < 40; y += 7)
            for (var x = 0; x < 40; x += 7)
                Assert.Equal(firstLayer.Asset!.Image.GetPixel(x, y), secondLayer.Asset!.Image.GetPixel(x, y));
    }

    [Fact]
    public void AGradientMapSendsTheDarkTonesAndTheLightOnesToItsOwnColours()
    {
        var (document, layer) = Warm(20, 20);
        using var _ = document;
        // The warm colour's tones are somewhere in the middle, so map the darkest end to blue and the
        // lightest to nothing at all — the pixel comes out blue-ish rather than red.
        Assert.True(FilterEdits.ApplyAdjustment(document, layer.ID, new LayerAdjustment
        {
            Kind = AdjustmentKind.GradientMap,
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = AdjustmentColor.From(0, 0, 1),
                Highlights = AdjustmentColor.From(0, 0, 1),
            },
        }));
        var pixel = Middle(layer);
        Assert.True(pixel.Blue > pixel.Red, $"the gradient map did not reach the pixels: {pixel}");
    }

    [Fact]
    public void ALayerThatIsNotThereIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        Assert.False(FilterEdits.ApplyAdjustment(document, Guid.NewGuid(), new LayerAdjustment { Kind = AdjustmentKind.Invert }));
    }
}
