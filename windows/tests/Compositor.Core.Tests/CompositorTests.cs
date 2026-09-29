using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The compositor against hand-computed pixels: one layer, stack order, folders, masks, clipping and the
/// blend modes. Expected values are worked out from the formulas, not from a previous run.
/// </summary>
public class CompositorTests
{
    private static CanvasDocument Doc(int width, int height) => new(Guid.NewGuid(), width, height);

    private static ImageLayer Solid(SKColor colour, double x, double y, int width, int height,
        double opacity = 1, LayerBlendMode blend = LayerBlendMode.Normal, bool visible = true)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Model.LayerTransform(x, y, width, height, 0, false, false, LayerSampling.HighQuality), "Layer")
        {
            Opacity = opacity,
            BlendMode = blend,
            IsVisible = visible,
        };
    }

    private static Model.LayerMask GreyMask(int width, int height, byte value, bool enabled = true)
    {
        var bitmap = new SKBitmap(Bitmaps.MaskInfo(width, height));
        bitmap.Erase(new SKColor(value, value, value));
        var mask = Model.LayerMask.AssetFrom(bitmap);
        mask.IsEnabled = enabled;
        return mask;
    }

    private static void Close(SKColor actual, int red, int green, int blue, int alpha, int tolerance = 2)
    {
        Assert.True(Math.Abs(actual.Red - red) <= tolerance, $"red {actual.Red} is not {red}");
        Assert.True(Math.Abs(actual.Green - green) <= tolerance, $"green {actual.Green} is not {green}");
        Assert.True(Math.Abs(actual.Blue - blue) <= tolerance, $"blue {actual.Blue} is not {blue}");
        Assert.True(Math.Abs(actual.Alpha - alpha) <= tolerance, $"alpha {actual.Alpha} is not {alpha}");
    }

    [Fact]
    public void AnOpaqueLayerPaintsItsColourOverTheWholeCanvas()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(200, 100, 50), 0, 0, 4, 4));

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 200, 100, 50, 255);
    }

    [Fact]
    public void ACanvasNothingPaintedStaysTransparent()
    {
        using var document = Doc(4, 4);
        using var result = DocumentRenderer.Render(document);
        Assert.Equal(0, result.GetPixel(1, 1).Alpha);
    }

    [Fact]
    public void OpacityScalesASolidLayer()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(200, 100, 50), 0, 0, 4, 4, opacity: 0.5));

        using var result = DocumentRenderer.Render(document);
        // 200·0.5 = 100 premultiplied over alpha 127.5 → 128, which reads back as 100/128·255 ≈ 199.
        Close(result.GetPixel(2, 2), 199, 100, 50, 128);
    }

    [Fact]
    public void LayersStackBottomToTop()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(255, 0, 0), 0, 0, 4, 4));
        document.Layers.Add(Solid(new SKColor(0, 0, 255), 0, 0, 4, 4, opacity: 0.5));

        using var result = DocumentRenderer.Render(document);
        // Half the blue over an opaque red: 0.5·0 + 0.5·255 = 127.5 for red and blue, alpha stays 255.
        Close(result.GetPixel(2, 2), 128, 0, 127, 255);
    }

    [Fact]
    public void AHiddenLayerIsNotDrawn()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(255, 0, 0), 0, 0, 4, 4, visible: false));

        using var result = DocumentRenderer.Render(document);
        Assert.Equal(0, result.GetPixel(2, 2).Alpha);
    }

    [Fact]
    public void ALayerSmallerThanTheCanvasSitsAtItsOrigin()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(SKColors.Red, 2, 2, 2, 2));

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 255, 0, 0, 255);
        Assert.Equal(0, result.GetPixel(0, 0).Alpha);
        Assert.Equal(0, result.GetPixel(3, 1).Alpha);
    }

    [Fact]
    public void ARotatedLayerCoversItsRotatedRectangle()
    {
        using var document = Doc(6, 6);
        var layer = Solid(SKColors.Red, 1, 2, 4, 2);
        layer.Transform = new Model.LayerTransform(1, 2, 4, 2, 90, false, false, LayerSampling.HighQuality);
        document.Layers.Add(layer);

        using var result = DocumentRenderer.Render(document);
        // A 4x2 box centred at (3, 3) turned a quarter turn: 2 wide, 4 tall, spanning x 2-3 and y 1-4.
        Close(result.GetPixel(2, 3), 255, 0, 0, 255);
        Close(result.GetPixel(3, 3), 255, 0, 0, 255);
        Assert.Equal(0, result.GetPixel(0, 3).Alpha);
        Assert.Equal(0, result.GetPixel(2, 0).Alpha);
    }

    [Fact]
    public void AFolderOpacityMultipliesIntoItsChildren()
    {
        using var document = Doc(4, 4);
        var folder = Solid(SKColors.White, 0, 0, 4, 4, opacity: 0.5);
        folder.IsGroup = true;
        folder.Asset?.Dispose();
        folder.Asset = null;
        var child = Solid(SKColors.Red, 0, 0, 4, 4);
        child.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(child);

        using var result = DocumentRenderer.Render(document);
        Assert.Equal(128, result.GetPixel(2, 2).Alpha);
        Close(result.GetPixel(2, 2), 255, 0, 0, 128);
    }

    [Fact]
    public void AHiddenFolderHidesWhatIsInsideIt()
    {
        using var document = Doc(4, 4);
        var folder = Solid(SKColors.White, 0, 0, 4, 4, visible: false);
        folder.IsGroup = true;
        folder.Asset?.Dispose();
        folder.Asset = null;
        var child = Solid(SKColors.Red, 0, 0, 4, 4);
        child.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(child);

        using var result = DocumentRenderer.Render(document);
        Assert.Equal(0, result.GetPixel(2, 2).Alpha);
    }

    [Fact]
    public void AMaskMultipliesTheLayerAlpha()
    {
        using var document = Doc(4, 4);
        var layer = Solid(SKColors.Red, 0, 0, 4, 4);
        layer.Mask = GreyMask(4, 4, 128);
        document.Layers.Add(layer);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 255, 0, 0, 128);
    }

    [Fact]
    public void ADisabledMaskIsIgnored()
    {
        using var document = Doc(4, 4);
        var layer = Solid(SKColors.Red, 0, 0, 4, 4);
        layer.Mask = GreyMask(4, 4, 0, enabled: false);
        document.Layers.Add(layer);

        using var result = DocumentRenderer.Render(document);
        Assert.Equal(255, result.GetPixel(2, 2).Alpha);
    }

    [Fact]
    public void AFolderMaskClipsEverythingInsideIt()
    {
        using var document = Doc(4, 4);
        var folder = Solid(SKColors.White, 0, 0, 4, 4);
        folder.IsGroup = true;
        folder.Asset?.Dispose();
        folder.Asset = null;
        folder.Mask = GreyMask(4, 4, 64);
        var child = Solid(SKColors.Red, 0, 0, 4, 4);
        child.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(child);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 255, 0, 0, 64);
    }

    [Fact]
    public void AClipRevealsItsLayerOnlyWhereTheBaseIs()
    {
        using var document = Doc(4, 4);
        var baseLayer = Solid(SKColors.White, 0, 0, 2, 2);
        var clipped = Solid(SKColors.Red, 0, 0, 4, 4);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(0, 0), 255, 0, 0, 255);
        Assert.Equal(0, result.GetPixel(2, 2).Alpha);
        Assert.Equal(0, result.GetPixel(3, 3).Alpha);
    }

    [Fact]
    public void AClipTakesItsBlendModeAndOpacityFromTheBase()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(200, 200, 200), 0, 0, 4, 4));
        var baseLayer = Solid(SKColors.White, 0, 0, 4, 4, opacity: 0.5, blend: LayerBlendMode.Multiply);
        var clipped = Solid(new SKColor(128, 128, 128), 0, 0, 4, 4, blend: LayerBlendMode.Normal);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var result = DocumentRenderer.Render(document);
        // The opaque grey covers the white base, so the stack holds grey, and the stack then carries the
        // base's Multiply and the base's 0.5: 0.5·200 + 0.5·(128·200/255) = 100 + 50 = 150. A Normal base
        // would leave 164, and a base at full opacity 100.
        Close(result.GetPixel(2, 2), 150, 150, 150, 255);
    }

    [Fact]
    public void MultiplyDarkensWhatIsBeneathIt()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(SKColors.White, 0, 0, 4, 4));
        document.Layers.Add(Solid(new SKColor(128, 128, 128), 0, 0, 4, 4, blend: LayerBlendMode.Multiply));

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 128, 128, 128, 255);
    }

    /// <summary>
    /// The eight modes Skia has no equivalent for, against values worked out from each formula with a
    /// backdrop of 200 and a source of 100 (that is 0.78431 and 0.39216 straight).
    /// </summary>
    [Theory]
    [InlineData(LayerBlendMode.LinearBurn, 45)]
    [InlineData(LayerBlendMode.LinearDodgeAdd, 255)]
    [InlineData(LayerBlendMode.VividLight, 185)]
    [InlineData(LayerBlendMode.LinearLight, 145)]
    [InlineData(LayerBlendMode.PinLight, 200)]
    [InlineData(LayerBlendMode.HardMix, 255)]
    [InlineData(LayerBlendMode.Subtract, 100)]
    [InlineData(LayerBlendMode.Divide, 255)]
    public void TheModesSkiaLacksCompositeThroughTheCompositor(LayerBlendMode mode, int expected)
    {
        Assert.True(BlendModes.IsHandRolled(BlendModes.From(mode)), $"{mode} should not be a Skia mode");
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(200, 200, 200), 0, 0, 4, 4));
        document.Layers.Add(Solid(new SKColor(100, 100, 100), 0, 0, 4, 4, blend: mode));

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), expected, expected, expected, 255, tolerance: 1);
    }

    [Fact]
    public void EveryFormatBlendModeHasAPixelMode()
    {
        // From is a switch with a fallback, so a mode added to the format without one would land on
        // Luminosity. Compare the round trip through the two enumerations' spellings instead.
        foreach (var mode in Enum.GetValues<LayerBlendMode>())
        {
            Assert.Equal(BlendModes.Name(BlendModes.From(mode)), Spell(mode));
        }

        static string Spell(LayerBlendMode mode) => mode switch
        {
            LayerBlendMode.Normal => "Normal",
            LayerBlendMode.Darken => "Darken",
            LayerBlendMode.Multiply => "Multiply",
            LayerBlendMode.ColorBurn => "Color Burn",
            LayerBlendMode.LinearBurn => "Linear Burn",
            LayerBlendMode.Lighten => "Lighten",
            LayerBlendMode.Screen => "Screen",
            LayerBlendMode.ColorDodge => "Color Dodge",
            LayerBlendMode.LinearDodgeAdd => "Linear Dodge (Add)",
            LayerBlendMode.Overlay => "Overlay",
            LayerBlendMode.SoftLight => "Soft Light",
            LayerBlendMode.HardLight => "Hard Light",
            LayerBlendMode.VividLight => "Vivid Light",
            LayerBlendMode.LinearLight => "Linear Light",
            LayerBlendMode.PinLight => "Pin Light",
            LayerBlendMode.HardMix => "Hard Mix",
            LayerBlendMode.Difference => "Difference",
            LayerBlendMode.Exclusion => "Exclusion",
            LayerBlendMode.Subtract => "Subtract",
            LayerBlendMode.Divide => "Divide",
            LayerBlendMode.Hue => "Hue",
            LayerBlendMode.Saturation => "Saturation",
            LayerBlendMode.Color => "Color",
            _ => "Luminosity",
        };
    }

    [Fact]
    public void AnAdjustmentChangesWhatIsBeneathIt()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(100, 50, 0), 0, 0, 4, 4));
        var adjustment = Solid(SKColors.White, 0, 0, 4, 4);
        adjustment.Asset?.Dispose();
        adjustment.Asset = null;
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        document.Layers.Add(adjustment);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 155, 205, 255, 255);
    }

    [Fact]
    public void AnAdjustmentIsHeldToItsOwnOpacity()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(100, 50, 0), 0, 0, 4, 4));
        var adjustment = Solid(SKColors.White, 0, 0, 4, 4, opacity: 0.5);
        adjustment.Asset?.Dispose();
        adjustment.Asset = null;
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        document.Layers.Add(adjustment);

        using var result = DocumentRenderer.Render(document);
        // Halfway from (100, 50, 0) to its inverse (155, 205, 255).
        Close(result.GetPixel(2, 2), 128, 128, 128, 255);
    }

    [Fact]
    public void AnAdjustmentIsHeldToItsMask()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(100, 50, 0), 0, 0, 4, 4));
        var adjustment = Solid(SKColors.White, 0, 0, 4, 4);
        adjustment.Asset?.Dispose();
        adjustment.Asset = null;
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        adjustment.Mask = GreyMask(4, 4, 128);
        document.Layers.Add(adjustment);

        using var result = DocumentRenderer.Render(document);
        // The mask covers half, so the inversion lands half way.
        Close(result.GetPixel(2, 2), 128, 128, 128, 255);
    }

    [Fact]
    public void AHiddenAdjustmentChangesNothing()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(100, 50, 0), 0, 0, 4, 4));
        var adjustment = Solid(SKColors.White, 0, 0, 4, 4, visible: false);
        adjustment.Asset?.Dispose();
        adjustment.Asset = null;
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        document.Layers.Add(adjustment);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 100, 50, 0, 255);
    }

    [Fact]
    public void AnAdjustmentInsideAFolderIsDampedByTheFoldersOpacity()
    {
        using var document = Doc(4, 4);
        document.Layers.Add(Solid(new SKColor(100, 50, 0), 0, 0, 4, 4));
        var folder = Solid(SKColors.White, 0, 0, 4, 4, opacity: 0.5);
        folder.IsGroup = true;
        folder.Asset?.Dispose();
        folder.Asset = null;
        var adjustment = Solid(SKColors.White, 0, 0, 4, 4);
        adjustment.Asset?.Dispose();
        adjustment.Asset = null;
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        adjustment.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(adjustment);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(2, 2), 128, 128, 128, 255);
    }

    [Fact]
    public void AUniformOnePixelMaskCoversTheWholeLayer()
    {
        using var document = Doc(4, 4);
        var layer = Solid(SKColors.Red, 0, 0, 4, 4);
        // A uniform mask is stored as one pixel and stretched over the layer, which is why the format
        // allows it and why it must not be read as covering a single pixel.
        var uniform = new SKBitmap(Bitmaps.MaskInfo(1, 1));
        uniform.Erase(new SKColor(128, 128, 128));
        layer.Mask = Model.LayerMask.AssetFrom(uniform);
        document.Layers.Add(layer);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(0, 0), 255, 0, 0, 128);
        Close(result.GetPixel(3, 3), 255, 0, 0, 128);
    }

    [Fact]
    public void AnOutsideStrokeWidensTheLayerByItsSize()
    {
        using var document = Doc(6, 6);
        var layer = Solid(SKColors.White, 2, 2, 2, 2);
        layer.Effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 1, Red = 1, Green = 0, Blue = 0, Opacity = 1 },
        };
        document.Layers.Add(layer);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(3, 3), 255, 255, 255, 255);   // the layer itself
        Close(result.GetPixel(1, 3), 255, 0, 0, 255);       // the ring, one pixel out
        Assert.Equal(0, result.GetPixel(0, 3).Alpha);       // and no further
    }

    [Fact]
    public void ADisabledEffectChangesNothing()
    {
        using var withoutEffects = Doc(6, 6);
        withoutEffects.Layers.Add(Solid(SKColors.White, 2, 2, 2, 2));
        using var plain = DocumentRenderer.Render(withoutEffects);

        using var document = Doc(6, 6);
        var layer = Solid(SKColors.White, 2, 2, 2, 2);
        layer.Effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Enabled = false, Size = 2, Red = 1, Opacity = 1 },
        };
        document.Layers.Add(layer);
        using var result = DocumentRenderer.Render(document);

        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 6; x++) Assert.Equal(plain.GetPixel(x, y), result.GetPixel(x, y));
        }
    }

    [Fact]
    public void ADropShadowFallsBelowTheLayerAndTakesItsColour()
    {
        using var document = Doc(9, 9);
        var layer = Solid(SKColors.White, 3, 1, 2, 2);
        layer.Effects = new LayerEffects
        {
            // 90 degrees is light from straight above, which drops the shadow straight down.
            Shadow = new ShadowEffect { Angle = 90, Distance = 2, Blur = 0, Opacity = 1 },
        };
        document.Layers.Add(layer);

        using var result = DocumentRenderer.Render(document);
        Close(result.GetPixel(4, 2), 255, 255, 255, 255);   // the layer covers its own shadow
        Close(result.GetPixel(3, 4), 0, 0, 0, 255);         // the shadow, two rows down
        Assert.Equal(0, result.GetPixel(3, 0).Alpha);       // nothing above it
    }
}
