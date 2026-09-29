using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// What the Camera Raw panel paints over the picture while it is being worked on: clipped shadows in blue,
/// clipped highlights in red, and the sharpening mask. These are looks, not edits — the window only ever shows
/// them through a preview, and committing the filter leaves them off, as the Mac build does.
/// </summary>
public class CameraRawOverlayTests
{
    /// <summary>A layer of three bands: clipped to black, mid grey, clipped to white.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Bands()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(30, 30));
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 30; x++)
            {
                var colour = x < 10 ? SKColors.Black : x < 20 ? new SKColor(128, 128, 128) : SKColors.White;
                bitmap.SetPixel(x, y, colour);
            }
        }
        var document = new CanvasDocument(Guid.NewGuid(), 30, 30);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Bands"),
            new LayerTransform(0, 0, 30, 30), "Bands");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor At(ImageLayer layer, int x, int y) => layer.Asset!.Image.GetPixel(x, y);

    [Fact]
    public void ClippedShadowsAreShownInBlueAndClippedHighlightsInRed()
    {
        var (document, layer) = Bands();
        using var _ = document;
        Assert.True(CameraRawEdits.Overlay(document, layer.ID, new CameraRawSettings(),
            shadows: true, highlights: false, sharpenMask: false));
        // The band that is clipped to black reads blue, and nothing else was touched.
        var dark = At(layer, 5, 15);
        Assert.True(dark.Blue > 150 && dark.Blue > dark.Red + 100, $"the clipped shadows are not blue: {dark}");
        Assert.Equal(new SKColor(128, 128, 128), At(layer, 15, 15));
        Assert.Equal(SKColors.White, At(layer, 25, 15));

        var (other, light) = Bands();
        using var _light = other;
        Assert.True(CameraRawEdits.Overlay(other, light.ID, new CameraRawSettings(),
            shadows: false, highlights: true, sharpenMask: false));
        var bright = At(light, 25, 15);
        Assert.True(bright.Red > 200 && bright.Red > bright.Blue, $"the clipped highlights are not red: {bright}");
        Assert.Equal(SKColors.Black, At(light, 5, 15));
        Assert.Equal(new SKColor(128, 128, 128), At(light, 15, 15));
    }

    [Fact]
    public void BothEndsCanBeShownAtOnce()
    {
        var (document, layer) = Bands();
        using var _ = document;
        Assert.True(CameraRawEdits.Overlay(document, layer.ID, new CameraRawSettings(),
            shadows: true, highlights: true, sharpenMask: false));
        Assert.True(At(layer, 5, 15).Blue > 150, "the shadows are not blue");
        Assert.True(At(layer, 25, 15).Red > 200, "the highlights are not red");
        Assert.Equal(new SKColor(128, 128, 128), At(layer, 15, 15));
    }

    [Fact]
    public void TheOverlayIsOverTheGradeThePanelIsShowing()
    {
        // Flattening the contrast lifts the black band to mid grey, so nothing is clipped any more and the
        // shadow view leaves it alone: what is shown is the picture as the panel has it, not as it was before.
        var (document, layer) = Bands();
        using var _ = document;
        var lifted = new CameraRawSettings { Contrast = -100 };
        Assert.True(CameraRawEdits.Overlay(document, layer.ID, lifted,
            shadows: true, highlights: false, sharpenMask: false));
        var risen = At(layer, 5, 15);
        Assert.True(risen.Red > 100, $"the black band was not lifted by the contrast: {risen}");
        Assert.True(Math.Abs(risen.Blue - risen.Red) < 40, $"the shadows were still shown as clipped: {risen}");
    }

    [Fact]
    public void TheSharpeningMaskCanBeShownOverThePicture()
    {
        // A hard edge with the mask all the way up: the mask view paints over the picture, so the result is
        // not the grade on its own.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 40));
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++) bitmap.SetPixel(x, y, x < 20 ? new SKColor(60, 60, 60) : new SKColor(200, 200, 200));
        }
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Edge"),
            new LayerTransform(0, 0, 40, 40), "Edge");
        document.Layers.Add(layer);
        var settings = new CameraRawSettings { SharpenAmount = 60, SharpenRadius = 2, SharpenDetail = 50, SharpenMasking = 100 };
        Assert.True(CameraRawEdits.Overlay(document, layer.ID, settings,
            shadows: false, highlights: false, sharpenMask: true));
        // The picture is now the mask over the graded edge rather than the graded edge.
        var shown = At(layer, 19, 20);
        Assert.True(shown.Red != 60 && shown.Green != 60, $"the mask view left the edge as it was: {shown}");
        Assert.Equal(40, layer.Asset!.Width);
    }

    [Fact]
    public void AnOverlayThatAsksForNothingIsRefused()
    {
        var (document, layer) = Bands();
        using var _ = document;
        var original = layer.Asset;
        Assert.False(CameraRawEdits.Overlay(document, layer.ID, new CameraRawSettings(),
            shadows: false, highlights: false, sharpenMask: false));
        Assert.Same(original, layer.Asset);
        Assert.False(CameraRawEdits.Overlay(document, Guid.NewGuid(), new CameraRawSettings(),
            shadows: true, highlights: true, sharpenMask: false));
        // Amounts outside their range are refused rather than shown as something they are not.
        Assert.False(CameraRawEdits.Overlay(document, layer.ID, new CameraRawSettings { Exposure = 99 },
            shadows: true, highlights: true, sharpenMask: false));
    }
}
