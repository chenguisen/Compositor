using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Curves: the value a channel takes for an input, which is what the panel draws as its line and what the
/// operator puts on the pixels. The interpolation is covered by the pixel tests; these check the two steps
/// together and that a curve reaches a layer.
/// </summary>
public class CurvesValueTests
{
    private static CurvesSettings With(int channel, params (double X, double Y)[] points)
    {
        var curves = new CurvesSettings();
        curves.Channels[channel] = points.Select(point => new CurvePoint { X = point.X, Y = point.Y }).ToList();
        return curves;
    }

    [Fact]
    public void ACurveLeftAloneIsTheStraightLine()
    {
        var curves = new CurvesSettings();
        for (var input = 0.0; input <= 255; input += 17)
        {
            Assert.Equal(input, AdjustmentOperators.CurvesValue(curves, 0, input), 3);
            Assert.Equal(input, AdjustmentOperators.CurvesValue(curves, 2, input), 3);
        }
    }

    [Fact]
    public void AHandleLiftsTheTonesAroundItAndLeavesTheEndsWhereTheyAre()
    {
        // The master curve lifted in the middle: mid grey comes out lighter, black and white do not move.
        var curves = With(0, (0, 0), (128, 180), (255, 255));
        Assert.Equal(0, AdjustmentOperators.CurvesValue(curves, 0, 0), 3);
        Assert.Equal(255, AdjustmentOperators.CurvesValue(curves, 0, 255), 3);
        Assert.True(AdjustmentOperators.CurvesValue(curves, 0, 128) > 170,
            $"mid grey came out at {AdjustmentOperators.CurvesValue(curves, 0, 128)}");
        // A little either side of it is lifted too, and the line never bends back on itself.
        var before = AdjustmentOperators.CurvesValue(curves, 0, 100);
        var after = AdjustmentOperators.CurvesValue(curves, 0, 156);
        Assert.True(before > 100 && before < 180, $"100 came out at {before}");
        Assert.True(after > 180 && after < 255, $"156 came out at {after}");
    }

    [Fact]
    public void TheMasterCurveIsAppliedBeforeTheChannelsOwn()
    {
        // The master halves everything, and red is lifted out of the dark. A red pixel is asked the master's
        // question first, and the red curve is handed that answer rather than the input.
        var curves = new CurvesSettings();
        curves.Channels[0] = [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 255, Y = 128 }];
        curves.Channels[1] = [new CurvePoint { X = 0, Y = 64 }, new CurvePoint { X = 255, Y = 255 }];

        var red = new CurvesSettings();
        red.Channels[1] = [new CurvePoint { X = 0, Y = 64 }, new CurvePoint { X = 255, Y = 255 }];

        var master = AdjustmentOperators.CurvesValue(curves, 0, 128);
        Assert.True(master < 100, $"the master did not bend the input: {master}");
        Assert.Equal(AdjustmentOperators.CurvesValue(red, 1, master), AdjustmentOperators.CurvesValue(curves, 1, 128), 3);
        // The master's own answer is its own, not bent by itself: 128 in is 128 out at the far end.
        Assert.Equal(128, AdjustmentOperators.CurvesValue(curves, 0, 255), 3);
        // Green has no curve of its own, so it takes the master's answer as it is.
        Assert.Equal(master, AdjustmentOperators.CurvesValue(curves, 2, 128), 3);
    }

    [Fact]
    public void ACurveSentToALayerLiftsItsPixels()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
        bitmap.Erase(new SKColor(128, 128, 128));
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Grey"),
            new LayerTransform(0, 0, 20, 20), "Grey");
        document.Layers.Add(layer);

        var settings = new LayerAdjustment
        {
            Kind = AdjustmentKind.Curves,
            Curves = With(0, (0, 0), (128, 180), (255, 255)),
        };
        Assert.True(FilterEdits.ApplyAdjustment(document, layer.ID, settings));
        var pixel = layer.Asset!.Image.GetPixel(10, 10);
        Assert.True(pixel.Red > 170, $"the curve did not reach the pixels: {pixel}");
        // Grey stays grey: the master curve bends all three channels the same way.
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Green, pixel.Blue);
    }

    [Fact]
    public void ACurveWithTheWrongNumberOfHandlesIsRefused()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
        bitmap.Erase(new SKColor(128, 128, 128));
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Grey"),
            new LayerTransform(0, 0, 20, 20), "Grey");
        document.Layers.Add(layer);

        var settings = new LayerAdjustment { Kind = AdjustmentKind.Curves };
        settings.Curves.Channels[2] = [new CurvePoint { X = 0, Y = 0 }];
        Assert.False(settings.IsValid);
        Assert.False(FilterEdits.ApplyAdjustment(document, layer.ID, settings));
        Assert.Equal(128, layer.Asset!.Image.GetPixel(10, 10).Red);
    }
}
