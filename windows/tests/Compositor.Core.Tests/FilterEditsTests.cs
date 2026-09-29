using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
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

    /// <summary>Half a layer opaque down its full height, the other half empty.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) HalfBlock(int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Transparent);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width / 2; x++)
                bitmap.SetPixel(x, y, SKColors.White);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Half"),
            new LayerTransform(0, 0, width, height), "Half");
        document.Layers.Add(layer);
        return (document, layer);
    }

    /// <summary>The colour under a document point, through the layer's placement as it stands now. A point
    /// the layer's pixels do not reach is nothing at all, as it would be on the canvas.</summary>
    private static SKColor AtDocument(ImageLayer layer, double x, double y)
    {
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, layer.Asset!.Width, layer.Asset.Height);
        Assert.True(toDocument.TryInvert(out var toPixel));
        var at = toPixel.MapPoint((float)x, (float)y);
        var px = (int)Math.Floor(at.X);
        var py = (int)Math.Floor(at.Y);
        if (px < 0 || py < 0 || px >= layer.Asset.Width || py >= layer.Asset.Height) return SKColors.Transparent;
        return layer.Asset.Image.GetPixel(px, py);
    }

    [Fact]
    public void GaussianBlurIsGivenRoomPastTheEdgeAndTrimmedBackAgain()
    {
        var (document, layer) = HalfBlock(40, 20);
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.GaussianBlur, new FilterSettings { BlurRadius = 3 }));

        // The layer was opaque top to bottom, so it could only have grown vertically if the blur went past
        // its edge — and the half that stayed empty was cut away again.
        Assert.True(layer.Asset!.Height > 20, $"the blur did not spread past the edge: {layer.Asset.Height}");
        Assert.True(layer.Asset.Width < 40, $"the empty half was not trimmed: {layer.Asset.Width}");
        // The pixels it kept are still where they were, and still white in the middle of the block.
        Assert.True(AtDocument(layer, 10, 10).Red >= 248, $"the block's middle became {AtDocument(layer, 10, 10)}");
        Assert.True(AtDocument(layer, 32, 10).Alpha < 20, "the empty side was not left empty");
    }

    [Fact]
    public void GaussianBlurSoftensTheEdgeRatherThanStoppingAtIt()
    {
        var (document, layer) = HalfBlock(40, 20);
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.GaussianBlur, new FilterSettings { BlurRadius = 3 }));
        var middle = layer.Asset!.Height / 2;
        var across = Enumerable.Range(0, layer.Asset.Width).Select(x => (int)layer.Asset.Image.GetPixel(x, middle).Alpha).ToList();
        Assert.True(across.Max() >= 248, $"the block's inside is not solid: {across.Max()}");
        // A hard edge would jump from nothing to everything between two pixels; a soft one spends levels on it.
        Assert.True(across.Any(a => a > 20 && a < 235), "the hard edge is still hard");
        Assert.True(across[^1] < 20, "it does not fade out on the far side");
    }

    [Fact]
    public void GaussianBlurLeavesTheLayerWhereItWasOnTheDocument()
    {
        var (document, layer) = HalfBlock(40, 20);
        using var _ = document;
        layer.Transform = new LayerTransform(100, 50, 80, 40);
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.GaussianBlur, new FilterSettings { BlurRadius = 4 }));
        // The block covers document x 100 to 140: its middle is still white, and well past either end there
        // is still nothing — the block softened outwards, but its middle did not move.
        Assert.True(AtDocument(layer, 120, 70).Red >= 248, $"the block moved: {AtDocument(layer, 120, 70)}");
        Assert.True(AtDocument(layer, 110, 60).Red >= 248, "the block moved");
        Assert.True(AtDocument(layer, 175, 70).Alpha < 20, "the block stretched");
        Assert.True(AtDocument(layer, 70, 70).Alpha < 20, "the block stretched the other way");
    }

    [Fact]
    public void GaussianBlurOfARadiusOutOfRangeIsRefused()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.GaussianBlur, new FilterSettings { BlurRadius = 0 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.GaussianBlur, new FilterSettings { BlurRadius = 900 }));
        Assert.Equal(128, Middle(layer).Red);
    }

    /// <summary>One white dot in the middle of an otherwise empty layer.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Dot(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(side / 2, side / 2, SKColors.White);
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Dot"),
            new LayerTransform(0, 0, side, side), "Dot");
        document.Layers.Add(layer);
        return (document, layer);
    }

    /// <summary>
    /// How far the ink reaches from <paramref name="origin"/> along the streak and across it, in document
    /// units, for a streak at <paramref name="radians"/> counterclockwise from horizontal.
    /// </summary>
    private static (double Along, double Across) Reach(ImageLayer layer, SKPoint origin, double radians)
    {
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, layer.Asset!.Width, layer.Asset.Height);
        Assert.True(toDocument.TryInvert(out var toPixel));
        var dx = Math.Cos(radians);
        var dy = -Math.Sin(radians);
        double along = 0, across = 0;
        for (var distance = 0.0; distance < 40; distance += 0.5)
        {
            var on = toPixel.MapPoint((float)(origin.X + dx * distance), (float)(origin.Y + dy * distance));
            if (Ink(layer, on) > 0) along = distance;
            var side = toPixel.MapPoint((float)(origin.X - dy * distance), (float)(origin.Y + dx * distance));
            if (Ink(layer, side) > 0) across = distance;
        }
        return (along, across);
    }

    private static int Ink(ImageLayer layer, SKPoint at)
    {
        var x = (int)Math.Floor(at.X);
        var y = (int)Math.Floor(at.Y);
        if (x < 0 || y < 0 || x >= layer.Asset!.Width || y >= layer.Asset.Height) return 0;
        return layer.Asset.Image.GetPixel(x, y).Alpha;
    }

    [Fact]
    public void MotionBlurStreaksAlongItsAngleCounterclockwiseFromHorizontal()
    {
        // A streak of sixteen layer pixels along a horizontal line leaves the dot's ink spread along it.
        var (horizontal, acrossLayer) = Dot(41);
        using var _horizontal = horizontal;
        Assert.True(FilterEdits.Apply(horizontal, acrossLayer.ID, FilterKind.MotionBlur,
            new FilterSettings { MotionAngle = 0, MotionDistance = 16 }));
        var flat = Reach(acrossLayer, new SKPoint(20.5f, 20.5f), 0);
        Assert.True(flat.Along > 6, $"the dot did not streak: {flat.Along}");
        Assert.True(flat.Across < 2, $"the streak is as wide as it is long: {flat.Across}");

        // Turned a quarter counterclockwise, the same streak runs up the picture instead.
        var (turned, turnedLayer) = Dot(41);
        using var _turned = turned;
        Assert.True(FilterEdits.Apply(turned, turnedLayer.ID, FilterKind.MotionBlur,
            new FilterSettings { MotionAngle = 90, MotionDistance = 16 }));
        var up = Reach(turnedLayer, new SKPoint(20.5f, 20.5f), Math.PI / 2);
        Assert.True(up.Along > 6, $"the turned dot did not streak: {up.Along}");
        Assert.True(up.Across < 2, $"the turned streak is as wide as it is long: {up.Across}");
    }

    [Fact]
    public void MotionBlurOfADistanceOutOfRangeIsRefused()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.MotionBlur, new FilterSettings { MotionDistance = 0 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.MotionBlur, new FilterSettings { MotionDistance = 9000 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.MotionBlur, new FilterSettings { MotionAngle = 120 }));
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void MotionBlurIsGivenRoomPastTheEdgeAndTrimmedBackAgain()
    {
        var (document, layer) = HalfBlock(40, 20);
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.MotionBlur,
            new FilterSettings { MotionAngle = 90, MotionDistance = 16 }));
        // Streaked upwards and downwards, the layer grew vertically; the empty half is trimmed away.
        Assert.True(layer.Asset!.Height > 20, $"the streak did not spread past the edge: {layer.Asset.Height}");
        Assert.True(layer.Asset.Width < 40, $"the empty half was not trimmed: {layer.Asset.Width}");
        Assert.True(AtDocument(layer, 10, 10).Red >= 240, $"the block's middle became {AtDocument(layer, 10, 10)}");
    }

    [Fact]
    public void MotionBlurInsideASelectionLeavesTheRestAlone()
    {
        var (document, layer) = Flat(60, 40, new SKColor(180, 180, 180));
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 30, 40));
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.MotionBlur,
            new FilterSettings { MotionAngle = 0, MotionDistance = 12 }));
        // Outside the selection the colour is exactly what it was.
        Assert.Equal(180, layer.Asset!.Image.GetPixel(58, 20).Red);
        Assert.Equal(180, layer.Asset.Image.GetPixel(40, 5).Red);
    }

    [Fact]
    public void AddNoiseSpecklesThePixelsAndTheSameSeedIsTheSamePicture()
    {
        var settings = new FilterSettings { NoiseAmount = 60 };
        var (first, firstLayer) = Flat(40, 40, new SKColor(128, 128, 128));
        using var _first = first;
        Assert.True(FilterEdits.Apply(first, firstLayer.ID, FilterKind.AddNoise, settings, seed: 4242));

        var (second, secondLayer) = Flat(40, 40, new SKColor(128, 128, 128));
        using var _second = second;
        Assert.True(FilterEdits.Apply(second, secondLayer.ID, FilterKind.AddNoise, settings, seed: 4242));

        for (var y = 0; y < 40; y += 7)
            for (var x = 0; x < 40; x += 7)
                Assert.Equal(firstLayer.Asset!.Image.GetPixel(x, y), secondLayer.Asset!.Image.GetPixel(x, y));
        // And it is noise, not a flat field.
        var values = Enumerable.Range(0, 40).Select(x => firstLayer.Asset!.Image.GetPixel(x, 20).Red).Distinct().Count();
        Assert.True(values > 3, $"only {values} different values along a row");
    }

    [Fact]
    public void MonochromaticNoiseChangesTheBrightnessAndNotTheColour()
    {
        var (document, layer) = Flat(40, 40, new SKColor(128, 128, 128));
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.AddNoise,
            new FilterSettings { NoiseAmount = 60, NoiseMonochromatic = true }, seed: 7));
        for (var x = 0; x < 40; x++)
        {
            var colour = layer.Asset!.Image.GetPixel(x, 20);
            Assert.Equal(colour.Red, colour.Green);
            Assert.Equal(colour.Green, colour.Blue);
        }
        // Colour noise, by contrast, does not keep the channels together.
        var (again, other) = Flat(40, 40, new SKColor(128, 128, 128));
        using var _2 = again;
        Assert.True(FilterEdits.Apply(again, other.ID, FilterKind.AddNoise, new FilterSettings { NoiseAmount = 60 }, seed: 7));
        Assert.True(Enumerable.Range(0, 40).Any(x =>
        {
            var colour = other.Asset!.Image.GetPixel(x, 20);
            return colour.Red != colour.Green;
        }), "colour noise left the channels together");
    }

    [Fact]
    public void AddNoiseLeavesTheSelectionAloneAndTheAlphaStanding()
    {
        var (document, layer) = Flat(40, 20, new SKColor(128, 128, 128, 200));
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 20, 20));
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.AddNoise, new FilterSettings { NoiseAmount = 80 }, seed: 11));
        Assert.Equal(200, Middle(layer).Alpha);
        Assert.Equal(128, layer.Asset!.Image.GetPixel(30, 10).Red);
        Assert.True(layer.Asset.Image.GetPixel(10, 10).Red != 128, "the selected half was not noised");
    }

    /// <summary>A bright dot on a dark field, with room around it for a glow to reach into.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) DotOnDark()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(60, 60));
        bitmap.Erase(new SKColor(10, 10, 10));
        for (var y = 28; y < 32; y++)
            for (var x = 28; x < 32; x++)
                bitmap.SetPixel(x, y, SKColors.White);
        var document = new CanvasDocument(Guid.NewGuid(), 60, 60);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Dot"),
            new LayerTransform(0, 0, 60, 60), "Dot");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void BloomSpreadsTheBrightPartsAndLeavesTheDarkOnes()
    {
        var (document, layer) = DotOnDark();
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.BloomGlow,
            new FilterSettings { BloomAmount = 80, BloomRadius = 12 }));

        // A glow spreads past the layer's own edge, so the picture is read through the placement it now has.
        var dot = AtDocument(layer, 30, 30);
        var near = AtDocument(layer, 34, 30);
        var far = AtDocument(layer, 52, 30);
        var corner = AtDocument(layer, 3, 3);
        Assert.True(dot.Red >= 250, $"the dot itself was dimmed to {dot.Red}");
        Assert.True(near.Red > 30, $"the glow did not reach the dot's neighbours: {near.Red}");
        Assert.True(near.Red > far.Red, $"the glow does not fade with distance: near {near.Red}, far {far.Red}");
        // What is dark and far from anything bright is left very nearly where it was.
        Assert.True(corner.Red < 25, $"the dark corner was lifted to {corner.Red}");
        // And the glow has spread the layer beyond the dot, into the room it was given.
        Assert.True(layer.Asset!.Width > 60, $"the glow did not spread: {layer.Asset.Width}");
    }

    [Fact]
    public void MoreBloomIsMoreGlow()
    {
        var none = NeighbourAfterBloom(20);
        var some = NeighbourAfterBloom(60);
        var lots = NeighbourAfterBloom(100);
        Assert.True(some > none, $"60 gave {some}, 20 gave {none}");
        Assert.True(lots > some, $"100 gave {lots}, 60 gave {some}");
        Assert.True(none > 10, $"even a little bloom did nothing: {none}");
    }

    /// <summary>The pixel just beside the bright dot after a bloom of this amount.</summary>
    private static byte NeighbourAfterBloom(double amount)
    {
        var (document, layer) = DotOnDark();
        using var _ = document;
        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.BloomGlow,
            new FilterSettings { BloomAmount = amount, BloomRadius = 12 }));
        return AtDocument(layer, 34, 30).Red;
    }

    [Fact]
    public void BloomOfAnAmountOutOfRangeIsRefused()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.BloomGlow, new FilterSettings { BloomAmount = 0 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.BloomGlow, new FilterSettings { BloomAmount = 400 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.BloomGlow,
            new FilterSettings { BloomAmount = 40, BloomRadius = 900 }));
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void AddNoiseWithNothingToAddIsRefused()
    {
        var (document, layer) = Flat(20, 20, SKColors.Gray);
        using var _ = document;
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.AddNoise, new FilterSettings { NoiseAmount = 0 }));
        Assert.False(FilterEdits.Apply(document, layer.ID, FilterKind.AddNoise, new FilterSettings { NoiseAmount = 500 }));
        Assert.Equal(128, Middle(layer).Red);
    }

    [Fact]
    public void AMaskIsCarriedOntoTheGridABlurGrowsTheLayerTo()
    {
        var (document, layer) = HalfBlock(40, 20);
        using var _ = document;
        // A mask painting the right half of the layer's own grid black: half the block is hidden.
        var mask = new SKBitmap(Bitmaps.MaskInfo(40, 20));
        mask.Erase(SKColors.White);
        for (var y = 0; y < 20; y++)
            for (var x = 20; x < 40; x++)
                mask.SetPixel(x, y, SKColors.Black);
        layer.Mask = LayerMask.AssetFrom(mask);

        Assert.True(FilterEdits.Apply(document, layer.ID, FilterKind.GaussianBlur, new FilterSettings { BlurRadius = 3 }));

        // The mask still hides the same document area, and it is now the layer's own grid again, so nothing
        // has been stretched to follow the layer outwards.
        Assert.NotNull(layer.Mask);
        Assert.Null(layer.Mask.Placement);
        Assert.Equal(layer.Asset!.Width, layer.Mask.Asset.Width);
        Assert.Equal(layer.Asset.Height, layer.Mask.Asset.Height);
        // The layer's own grid grew, so an untouched mask would have had to be 40 x 20 still.
        Assert.True(layer.Asset.Width != 40 || layer.Asset.Height != 20, "the layer did not change grid");
        // Read through the mask: the visible half is white in it, the hidden half is not.
        var toDocument = BrushEdits.PixelToDocument(layer.MaskTransform, layer.Mask.Asset.Width, layer.Mask.Asset.Height);
        Assert.True(toDocument.TryInvert(out var toPixel));
        var shown = toPixel.MapPoint(10, 10);
        var hidden = toPixel.MapPoint(30, 10);
        Assert.True(layer.Mask.Asset.Image.GetPixel((int)Math.Floor(shown.X), (int)Math.Floor(shown.Y)).Red > 235,
            "the mask's light half did not come across");
        Assert.True(layer.Mask.Asset.Image.GetPixel((int)Math.Floor(hidden.X), (int)Math.Floor(hidden.Y)).Red < 20,
            "the mask's dark half did not come across");
        // And the document it draws: white where the block is shown, nothing where the mask hides it.
        using var rendered = DocumentRenderer.Render(document);
        Assert.True(rendered.GetPixel(10, 10).Red >= 248 && rendered.GetPixel(10, 10).Alpha >= 248,
            $"the visible half was hidden: {rendered.GetPixel(10, 10)}");
        Assert.True(rendered.GetPixel(30, 10).Alpha < 20, $"the hidden half showed: {rendered.GetPixel(30, 10)}");
    }
}
