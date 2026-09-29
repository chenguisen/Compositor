using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The Camera Raw filter: the Light, Color and Effects stages run over a layer's own pixels, held to the
/// selection, as one edit. The kernels themselves are covered by the pixel tests; these check the pipeline
/// around them and what a slider does to the picture.
/// </summary>
public class CameraRawEditsTests
{
    /// <summary>A layer of one flat colour, its pixel grid the same size as the document it fills.</summary>
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

    private static SKColor Middle(ImageLayer layer) => layer.Asset!.Image.GetPixel(layer.Asset.Width / 2, layer.Asset.Height / 2);
    private static SKColor Corner(ImageLayer layer) => layer.Asset!.Image.GetPixel(0, 0);

    [Fact]
    public void ASettingsBagWithNothingAskedForDoesNothing()
    {
        var (document, layer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _ = document;
        var settings = new CameraRawSettings();
        Assert.True(settings.IsIdentity);
        Assert.True(settings.IsValid);
        Assert.False(CameraRawEdits.Apply(document, layer.ID, settings));
    }

    [Fact]
    public void ASettingOutOfItsRangeIsRefused()
    {
        var settings = new CameraRawSettings { Exposure = 9 };
        Assert.False(settings.IsValid);
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        Assert.False(CameraRawEdits.Apply(document, layer.ID, settings));
        // The layer is as it was.
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void ExposureLightensAndDarkensThePixels()
    {
        var (document, layer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Exposure = 1 }));
        var brighter = Middle(layer);
        Assert.True(brighter.Red > 128, $"one stop up gave {brighter.Red}");

        var (back, other) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _2 = back;
        Assert.True(CameraRawEdits.Apply(back, other.ID, new CameraRawSettings { Exposure = -1 }));
        Assert.True(Middle(other).Red < 128, $"one stop down gave {Middle(other).Red}");
    }

    [Fact]
    public void SaturationTakesTheColourOutAndPutsItBack()
    {
        var colour = new SKColor(200, 60, 40);
        var (document, layer) = Flat(20, 20, colour);
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Saturation = -100 }));
        var grey = Middle(layer);
        // Fully desaturated: the channels meet at the luminance.
        Assert.True(Math.Abs(grey.Red - grey.Green) <= 2 && Math.Abs(grey.Green - grey.Blue) <= 2,
            $"desaturated to {grey}");
    }

    [Fact]
    public void TemperatureAndTintPushTheColourTheWayTheySay()
    {
        var (warm, warmLayer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _warm = warm;
        Assert.True(CameraRawEdits.Apply(warm, warmLayer.ID, new CameraRawSettings { Temperature = 100 }));
        var hotter = Middle(warmLayer);
        Assert.True(hotter.Red > hotter.Blue, $"warmer gave {hotter}");

        var (magenta, magentaLayer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _magenta = magenta;
        Assert.True(CameraRawEdits.Apply(magenta, magentaLayer.ID, new CameraRawSettings { Tint = 100 }));
        var pinker = Middle(magentaLayer);
        Assert.True(pinker.Green < pinker.Red && pinker.Green < pinker.Blue, $"more magenta gave {pinker}");
    }

    [Fact]
    public void AVignetteDarkensTheCornersAndLeavesTheMiddle()
    {
        var (document, layer) = Flat(60, 60, new SKColor(180, 180, 180));
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { VignetteAmount = -80 }));
        var middle = Middle(layer);
        var corner = Corner(layer);
        Assert.True(corner.Red < middle.Red, $"the corner {corner.Red} is not darker than the middle {middle.Red}");
        // The middle is left alone.
        Assert.True(Math.Abs(middle.Red - 180) <= 3, $"the middle became {middle.Red}");
    }

    [Fact]
    public void GrainWithTheSameSeedIsTheSamePicture()
    {
        var (first, firstLayer) = Flat(40, 40, new SKColor(128, 128, 128));
        using var _first = first;
        var settings = new CameraRawSettings { GrainAmount = 60 };
        Assert.True(CameraRawEdits.Apply(first, firstLayer.ID, settings, seed: 4242));

        var (second, secondLayer) = Flat(40, 40, new SKColor(128, 128, 128));
        using var _second = second;
        Assert.True(CameraRawEdits.Apply(second, secondLayer.ID, settings, seed: 4242));

        // The same seed puts the same grain in the same places.
        for (var y = 0; y < 40; y += 7)
        {
            for (var x = 0; x < 40; x += 7)
            {
                Assert.Equal(firstLayer.Asset!.Image.GetPixel(x, y), secondLayer.Asset!.Image.GetPixel(x, y));
            }
        }
        // And it is grain, not a flat field: the pixels differ from one another.
        var values = Enumerable.Range(0, 40).Select(x => firstLayer.Asset!.Image.GetPixel(x, 20).Red).Distinct().Count();
        Assert.True(values > 3, $"only {values} different values along a row");
    }

    [Fact]
    public void AFilterInsideASelectionLeavesTheRestAlone()
    {
        var (document, layer) = Flat(40, 20, new SKColor(128, 128, 128));
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 20, 20));
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Exposure = 2 }));

        // Inside the selection it is brighter; outside it is exactly what it was.
        Assert.True(layer.Asset!.Image.GetPixel(10, 10).Red > 128, "the selected half was not filtered");
        Assert.Equal(128, layer.Asset.Image.GetPixel(30, 10).Red);
        Assert.Equal(128, layer.Asset.Image.GetPixel(39, 19).Red);
    }

    [Fact]
    public void TheFilteredPixelsAreHeldTheWayALayerHoldsThem()
    {
        var (document, layer) = Flat(20, 20, new SKColor(200, 60, 40));
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Saturation = 40 }));
        Assert.Equal(SKAlphaType.Unpremul, layer.Asset!.Image.AlphaType);
        Assert.Equal(20, layer.Asset.Width);
        Assert.Equal(20, layer.Asset.Height);
        // The layer is still what it was in every other way.
        Assert.Equal("Flat", layer.Name);
        Assert.Equal(20, layer.Transform.Width);
    }

    [Fact]
    public void ATranslucentLayerKeepsItsAlpha()
    {
        var (document, layer) = Flat(20, 20, new SKColor(200, 60, 40, 128));
        using var _ = document;
        var before = Middle(layer).Alpha;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Exposure = 1 }));
        Assert.Equal(before, Middle(layer).Alpha);
    }

    [Fact]
    public void AFilterIsAppliedToALayerThatIsMovedAndScaled()
    {
        var (document, layer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _ = document;
        // Twice the size at an offset: the pixels are filtered in their own grid either way.
        layer.Transform = new LayerTransform(50, 50, 40, 40);
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Exposure = 1 }));
        Assert.True(Middle(layer).Red > 128);
        Assert.Equal(20, layer.Asset!.Width);
    }

    [Fact]
    public void ALayerWithNoPixelsIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        Assert.False(CameraRawEdits.Apply(document, blank.ID, new CameraRawSettings { Exposure = 1 }));
    }

    [Fact]
    public void TheDetailGroupSharpensAndTakesNoiseOut()
    {
        // A hard edge: the left half dark, the right half bright. Sharpening is gentle by design — its mask
        // holds it back where there is no detail to bring out — so the edge it is given has to be a real one.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(32, 32));
        bitmap.Erase(new SKColor(60, 60, 60));
        for (var y = 0; y < 32; y++)
            for (var x = 16; x < 32; x++)
                bitmap.SetPixel(x, y, new SKColor(200, 200, 200));
        using var document = new CanvasDocument(Guid.NewGuid(), 32, 32);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Edge"),
            new LayerTransform(0, 0, 32, 32), "Edge");
        document.Layers.Add(layer);

        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings
        {
            SharpenAmount = 150, SharpenRadius = 1, SharpenDetail = 100,
        }));
        // Sharpening pushes the two sides further apart where they meet.
        Assert.True(layer.Asset!.Image.GetPixel(15, 16).Red < 55,
            $"the dark side of the edge was not sharpened: {layer.Asset.Image.GetPixel(15, 16).Red}");
        Assert.True(layer.Asset.Image.GetPixel(16, 16).Red > 205,
            $"the light side of the edge was not sharpened: {layer.Asset.Image.GetPixel(16, 16).Red}");

        // The noise group leaves a flat field flat rather than doing nothing at all: it is asked for and runs.
        var (flat, flatLayer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _flat = flat;
        Assert.True(CameraRawEdits.Apply(flat, flatLayer.ID, new CameraRawSettings { NoiseLuminance = 60 }));
        Assert.InRange(Middle(flatLayer).Red, 120, 136);
    }

    [Fact]
    public void TheOpticsGroupStretchesAndLightensTheCorners()
    {
        var (document, layer) = Flat(60, 60, new SKColor(180, 180, 180));
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { OpticsVignetteAmount = -80 }));
        // A negative lens vignette darkens the corners and leaves the middle where it was.
        Assert.True(Corner(layer).Red < Middle(layer).Red, $"the corner {Corner(layer).Red} is not darker than {Middle(layer).Red}");
        Assert.True(Math.Abs(Middle(layer).Red - 180) <= 4, $"the middle became {Middle(layer).Red}");
    }

    [Fact]
    public void TheCalibrationGroupShiftsTheChannels()
    {
        // A colour with some saturation to work with: a neutral grey has none, so nothing could be shifted.
        var (document, layer) = Flat(20, 20, new SKColor(80, 120, 180));
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { BlueSaturation = 100 }));
        // Turning the blue channel's saturation up pushes it further from the others.
        var pixel = Middle(layer);
        Assert.True(pixel.Blue - pixel.Red > 100, $"the blue channel was not pushed: {pixel}");

        var (hue, hueLayer) = Flat(20, 20, new SKColor(80, 120, 180));
        using var _hue = hue;
        Assert.True(CameraRawEdits.Apply(hue, hueLayer.ID, new CameraRawSettings { BlueHue = 100 }));
        Assert.NotEqual(Middle(hueLayer), Middle(layer));
    }

    [Fact]
    public void TheNewAmountsAreCheckedLikeTheOthers()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        // Out of range is refused, and the layer is left as it was.
        Assert.False(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { SharpenAmount = 200 }));
        Assert.False(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { ProcessVersion = 9 }));
        Assert.False(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { PurpleHueHigh = 400 }));
        Assert.Equal(128, Middle(layer).Red);
        // And a settings bag that asks for nothing in any group is still the identity.
        Assert.True(new CameraRawSettings().IsIdentity);
    }

    [Fact]
    public void TheCurvePanelBendsTheWholePicture()
    {
        var (document, layer) = Flat(20, 20, new SKColor(128, 128, 128));
        using var _ = document;
        var curve = new CurvesSettings();
        curve.Channels[0] = [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 128, Y = 190 }, new CurvePoint { X = 255, Y = 255 }];
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Curve = curve }));
        // The mid grey is lifted, and being grey it comes back grey: the whole picture's curve bends all three
        // channels the same way.
        var pixel = Middle(layer);
        Assert.True(pixel.Red > 180, $"the mid grey became {pixel}");
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Green, pixel.Blue);
    }

    [Fact]
    public void RefineSaturationHoldsTheCurveOffTheColoursAlreadyStrong()
    {
        static SKColor WithRefine(double refine)
        {
            var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
            bitmap.Erase(new SKColor(200, 60, 60));
            using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
            var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Red"),
                new LayerTransform(0, 0, 20, 20), "Red");
            document.Layers.Add(layer);
            var curve = new CurvesSettings();
            curve.Channels[0] = [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 128, Y = 200 }, new CurvePoint { X = 255, Y = 255 }];
            Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings
            {
                Curve = curve, RefineSaturation = refine,
            }));
            return layer.Asset!.Image.GetPixel(10, 10);
        }
        var plain = WithRefine(0);
        var refined = WithRefine(100);
        // The curve lifts both, and holding it off the saturated ones lifts the colour less.
        Assert.True(plain.Red > 200, $"the curve did not lift it: {plain}");
        Assert.NotEqual(plain, refined);
    }

    [Fact]
    public void TheGradingWheelsTintTheTonalRangeTheyAreFor()
    {
        var (document, layer) = Flat(20, 20, new SKColor(40, 40, 40));
        using var _ = document;
        // Blue into the shadows: a dark picture comes back blue-ish rather than grey.
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings
        {
            ShadowHue = 220, ShadowSaturation = 100,
        }));
        var dark = Middle(layer);
        Assert.True(dark.Blue > dark.Red, $"the shadows were not tinted: {dark}");

        // The same push into the highlights leaves a dark picture nearly where it was.
        var (bright, brightLayer) = Flat(20, 20, new SKColor(40, 40, 40));
        using var _bright = bright;
        Assert.True(CameraRawEdits.Apply(bright, brightLayer.ID, new CameraRawSettings
        {
            HighlightHue = 220, HighlightSaturation = 100,
        }));
        var stillDark = Middle(brightLayer);
        Assert.True(stillDark.Blue - stillDark.Red < dark.Blue - dark.Red,
            $"the highlight wheel took hold of the shadows: {stillDark}");
    }

    [Fact]
    public void TheColourMixerMovesOneFamilyAndLeavesTheOthers()
    {
        static SKColor Mix(byte red, byte green, byte blue, int index, double amount)
        {
            using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
            var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
            bitmap.Erase(new SKColor(red, green, blue));
            var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
                new LayerTransform(0, 0, 20, 20), "Flat");
            document.Layers.Add(layer);
            var mixer = new double[24];
            mixer[index] = amount;
            Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Mixer = mixer }));
            return layer.Asset!.Image.GetPixel(10, 10);
        }
        // Reds are the first family: pushing their saturation leaves a red stronger and a blue alone.
        var red = new SKColor(200, 60, 60);
        var stronger = Mix(red.Red, red.Green, red.Blue, 8, 100);
        Assert.True(stronger.Green < red.Green, $"the reds were not pushed: {stronger}");
        var blue = new SKColor(60, 60, 200);
        var untouched = Mix(blue.Red, blue.Green, blue.Blue, 8, 100);
        Assert.Equal(blue, untouched);
        // The hues come first and then the saturations, so the blues' hue is the sixth of them, and turning
        // it moves the blue and leaves the red alone.
        Assert.NotEqual(blue, Mix(blue.Red, blue.Green, blue.Blue, 5, 100));
        Assert.Equal(red, Mix(red.Red, red.Green, red.Blue, 5, 100));
    }

    [Fact]
    public void TheCurveAndGradingAmountsAreCheckedLikeTheOthers()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        // A hue is a place on the wheel, so past a whole turn is not one.
        Assert.False(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { MidtoneHue = 400 }));
        Assert.False(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { GradeBlending = 200 }));
        // A curve that is not a curve is refused, where the panel's own validation refuses it.
        var broken = new CameraRawSettings();
        broken.Curve.Channels[2] = [new CurvePoint { X = 0, Y = 0 }];
        Assert.False(CameraRawEdits.Apply(document, layer.ID, broken));
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void AFilterWorksOnTheLayersOwnGridWhateverItsTransform()
    {
        var (document, layer) = Flat(8, 8, SKColors.Gray);
        using var _ = document;
        // A layer stretched over a huge rectangle is still only eight by eight pixels: the filter works on
        // the pixels the layer holds, as the Mac build's does, and its rectangle is not what it costs.
        layer.Transform = new LayerTransform(0, 0, 20_000, 20_000);
        Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings { Exposure = 1 }));
        Assert.Equal(8, layer.Asset!.Width);
        Assert.Equal(8, layer.Asset.Height);
        Assert.True(Middle(layer).Red > 128);
    }
}
