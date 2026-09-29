using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Image ▸ Auto Levels: the levels a picture asks for, worked out from its own histogram. What the levels
/// operator then does is covered by the pixel tests; these check what is asked for.
/// </summary>
public class LevelsEditsTests
{
    /// <summary>A layer of one flat colour, its pixel grid the same size as the document it fills.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Flat(int side, SKColor colour)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(colour);
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
            new LayerTransform(0, 0, side, side), "Flat");
        document.Layers.Add(layer);
        return (document, layer);
    }

    /// <summary>A layer washed out into the middle of the range: nothing darker than 100 or lighter than 150.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) WashedOut(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var level = (byte)(100 + x * 50 / Math.Max(1, side - 1));
                bitmap.SetPixel(x, y, new SKColor(level, level, level));
            }
        }
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Washed"),
            new LayerTransform(0, 0, side, side), "Washed");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void AutoContrastStretchesTheRangeThePictureActuallyUses()
    {
        var (document, layer) = WashedOut(64);
        using var _ = document;
        Assert.True(LevelsEdits.Auto(document, layer.ID, LevelsAuto.Contrast));
        // The darkest pixel is now black and the lightest white, which is what a stretch means.
        Assert.True(layer.Asset!.Image.GetPixel(0, 10).Red <= 4, $"the dark end became {layer.Asset.Image.GetPixel(0, 10).Red}");
        Assert.True(layer.Asset.Image.GetPixel(63, 10).Red >= 251, $"the light end became {layer.Asset.Image.GetPixel(63, 10).Red}");
        // And the middle of the ramp is still in the middle.
        var middle = layer.Asset.Image.GetPixel(32, 10).Red;
        Assert.InRange(middle, 100, 160);
    }

    [Fact]
    public void AFewStrayPixelsDoNotSetTheRange()
    {
        var (document, layer) = WashedOut(64);
        using var _ = document;
        // One black pixel and one white among eight hundred greys: the tenth of a percent at each end is
        // left out, so neither of them sets an endpoint.
        layer.Asset!.Image.SetPixel(5, 5, SKColors.Black);
        layer.Asset.Image.SetPixel(6, 5, SKColors.White);
        var settings = LevelsEdits.Automatic(layer.Asset.Image, LevelsAuto.Contrast);
        Assert.True(settings.Ranges[0].Black >= 99, $"the black point came out at {settings.Ranges[0].Black}");
        Assert.True(settings.Ranges[0].White <= 151, $"the white point came out at {settings.Ranges[0].White}");
    }

    [Fact]
    public void AutoColourSetsEachChannelOnItsOwnWhichTakesACastOut()
    {
        // A bluish grey: every channel is one value, so no channel has a range and nothing is asked for —
        // which is the honest answer for a picture with nothing in it to stretch.
        var (flat, flatLayer) = Flat(32, new SKColor(120, 126, 180));
        using var _flat = flat;
        var nothing = LevelsEdits.Automatic(flatLayer.Asset!.Image, LevelsAuto.Color);
        Assert.Equal(0, nothing.Ranges[1].Black);
        Assert.Equal(255, nothing.Ranges[1].White);
        Assert.Equal(1, nothing.Ranges[1].Gamma, 6);

        // A picture with some range in each channel, the channels sitting at different heights: a cast.
        var ramp = new SKBitmap(Bitmaps.ColorInfo(64, 8));
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var along = x * 160 / 63;
                ramp.SetPixel(x, y, new SKColor((byte)(40 + along), (byte)(60 + along), (byte)(80 + along)));
            }
        }

        var colour = LevelsEdits.Automatic(ramp, LevelsAuto.Color);
        // Each channel is stretched from its own ends, so they come out different: that is the cast going.
        Assert.Equal(40, colour.Ranges[1].Black, 6);
        Assert.Equal(200, colour.Ranges[1].White, 6);
        Assert.Equal(80, colour.Ranges[3].Black, 6);
        Assert.Equal(240, colour.Ranges[3].White, 6);

        var contrast = LevelsEdits.Automatic(ramp, LevelsAuto.Contrast);
        // One interval for all three, and the red channel asks for nothing of its own.
        Assert.Equal(40, contrast.Ranges[0].Black, 6);
        Assert.Equal(240, contrast.Ranges[0].White, 6);
        Assert.Equal(0, contrast.Ranges[1].Black);
        Assert.Equal(255, contrast.Ranges[1].White);
    }

    [Fact]
    public void NeutralMidtonesBringTheMidtoneBackToTheMiddle()
    {
        // A dark picture: most of it is in the bottom third, so its midtone is below the middle.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(64, 8));
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var level = (byte)(x * 60 / 63);
                bitmap.SetPixel(x, y, new SKColor(level, level, level));
            }
        }
        var settings = LevelsEdits.Automatic(bitmap, LevelsAuto.Neutral);
        foreach (var channel in new[] { 1, 2, 3 })
        {
            // The gamma is asked for, and it is above one because the midtone was dark.
            Assert.True(settings.Ranges[channel].Gamma > 1, $"the gamma came out at {settings.Ranges[channel].Gamma}");
        }
        // Setting colour rather than neutral leaves the gamma where it was.
        var plain = LevelsEdits.Automatic(bitmap, LevelsAuto.Color);
        Assert.Equal(1, plain.Ranges[1].Gamma, 6);
    }

    [Fact]
    public void AFlatPictureAsksForNothing()
    {
        var (document, layer) = Flat(16, SKColors.Gray);
        using var _ = document;
        // One value repeated has no range, so there is nothing to stretch and the layer is left alone.
        Assert.False(LevelsEdits.Auto(document, layer.ID, LevelsAuto.Contrast));
        Assert.Equal(128, layer.Asset!.Image.GetPixel(8, 8).Red);
    }

    [Fact]
    public void AutoLevelsIsRefusedForALayerThatIsNotThereOrHasNoPixels()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 16, 16);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 16, 16), "Empty");
        document.Layers.Add(blank);
        Assert.False(LevelsEdits.Auto(document, blank.ID, LevelsAuto.Contrast));
        Assert.False(LevelsEdits.Auto(document, Guid.NewGuid(), LevelsAuto.Contrast));
    }

    [Fact]
    public void AutoLevelsIsHeldToTheSelection()
    {
        var (document, layer) = WashedOut(64);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 32, 64));
        Assert.True(LevelsEdits.Auto(document, layer.ID, LevelsAuto.Contrast));
        // The stretched half is stretched, and the half outside the selection is what it was.
        Assert.True(layer.Asset!.Image.GetPixel(1, 10).Red <= 8, "the selected half was not stretched");
        var untouched = layer.Asset.Image.GetPixel(40, 10).Red;
        Assert.InRange(untouched, 128, 136);
    }
}
