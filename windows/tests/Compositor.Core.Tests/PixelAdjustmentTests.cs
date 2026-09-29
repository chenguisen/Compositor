using Compositor.Core.Pixels;

namespace Compositor.Core.Tests;

/// <summary>
/// The adjustment kernels and the hand-rolled blend modes against hand-worked values. Every expected number
/// is derived from the formula in Compositor/Rendering/AdjustPixels.c or from the formulas written out in
/// the BlendModes doc comment, plus the invariants each kernel must hold.
/// </summary>
public class PixelAdjustmentTests
{
    private static void At(byte[] pixels, int index, int red, int green, int blue, int alpha, int tolerance = 0)
    {
        var o = index * 4;
        Assert.True(Math.Abs(pixels[o] - red) <= tolerance && Math.Abs(pixels[o + 1] - green) <= tolerance
            && Math.Abs(pixels[o + 2] - blue) <= tolerance && Math.Abs(pixels[o + 3] - alpha) <= tolerance,
            $"pixel {index} is ({pixels[o]},{pixels[o + 1]},{pixels[o + 2]},{pixels[o + 3]}),"
            + $" not ({red},{green},{blue},{alpha})");
    }

    // ------------------------------------------------------------------ Gradient Map

    /// <summary>A two-stop gradient from red at 0 to blue at 255, one table row per stop.</summary>
    private static byte[] RedToBlueTable()
    {
        var table = new byte[256 * 3];
        for (var i = 0; i < 256; i++)
        {
            table[i * 3] = (byte)(255 - i);
            table[i * 3 + 1] = 0;
            table[i * 3 + 2] = (byte)i;
        }
        return table;
    }

    [Fact]
    public void AGradientMapSendsTheDarkestInputToTheFirstColour()
    {
        var pixels = new byte[] { 0, 0, 0, 255 };
        AdjustPixels.GradientMap(pixels, 1, 1, 4, RedToBlueTable());
        At(pixels, 0, 255, 0, 0, 255);
    }

    [Fact]
    public void AGradientMapSendsTheLightestInputToTheSecondColour()
    {
        // Luminance is (2126 + 7152 + 722) × 255 + 5000 over 10000, i.e. 255 (the half round down).
        var pixels = new byte[] { 255, 255, 255, 255 };
        AdjustPixels.GradientMap(pixels, 1, 1, 4, RedToBlueTable());
        At(pixels, 0, 0, 0, 255, 255);
    }

    [Fact]
    public void AGradientMapPicksTheEntryAtThePixelsLuminance()
    {
        // 128 grey: (128 × 10000 + 5000) / 10000 = 128, the middle of the red-to-blue ramp.
        var pixels = new byte[] { 128, 128, 128, 255 };
        AdjustPixels.GradientMap(pixels, 1, 1, 4, RedToBlueTable());
        At(pixels, 0, 127, 0, 128, 255);
    }

    [Fact]
    public void AGradientMapUnpremultipliesBeforeChoosingAndRepremultipliesAfter()
    {
        // Half-alpha red: premultiplied 128 is a straight 255, level (2126 × 255 + 5000) / 10000 = 54.
        var pixels = new byte[] { 128, 0, 0, 128 };
        AdjustPixels.GradientMap(pixels, 1, 1, 4, RedToBlueTable());
        At(pixels, 0, 101, 0, 27, 128);
    }

    [Fact]
    public void AGradientMapLeavesTransparentPixelsAlone()
    {
        var pixels = new byte[] { 9, 8, 7, 0 };
        AdjustPixels.GradientMap(pixels, 1, 1, 4, RedToBlueTable());
        At(pixels, 0, 9, 8, 7, 0);
    }

    // ------------------------------------------------------------------ Grain

    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                pixels[o] = (byte)(20 + x * 30);
                pixels[o + 1] = (byte)(40 + y * 30);
                pixels[o + 2] = (byte)(60 + (x + y) % 3 * 40);
                pixels[o + 3] = 255;
            }
        return pixels;
    }

    [Fact]
    public void GrainIsTheSameForTheSameSeed()
    {
        var first = Gradient(6, 6);
        var second = (byte[])first.Clone();
        AdjustPixels.Grain(first, 6, 6, 24, 60, 3, 0, 7, 0, 0, 1);
        AdjustPixels.Grain(second, 6, 6, 24, 60, 3, 0, 7, 0, 0, 1);
        Assert.Equal(first, second);

        var other = Gradient(6, 6);
        AdjustPixels.Grain(other, 6, 6, 24, 60, 3, 0, 8, 0, 0, 1);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void GrainLeavesAlphaAndTransparentPixelsAlone()
    {
        var pixels = new byte[] { 128, 128, 128, 255, 64, 64, 64, 128, 0, 0, 0, 0 };
        AdjustPixels.Grain(pixels, 3, 1, 12, 80, 3, 50, 1, 0, 0, 1);
        Assert.Equal(255, pixels[3]);
        Assert.Equal(128, pixels[7]);
        Assert.Equal(0, pixels[11]);
        At(pixels, 2, 0, 0, 0, 0);
    }

    [Fact]
    public void GrainAtAnOriginMatchesThatWindowOfTheWholeImage()
    {
        // Pixel (x, y) is hashed at (originX + (x + 0.5) × units, …), so a piece run at its own origin gets
        // exactly the grain the whole image has there.
        var whole = Gradient(6, 6);
        var original = (byte[])whole.Clone();
        AdjustPixels.Grain(whole, 6, 6, 24, 60, 3, 0, 2024, 0, 0, 1);

        var piece = new byte[3 * 3 * 4];
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                Array.Copy(original, ((y + 1) * 6 + (x + 2)) * 4, piece, (y * 3 + x) * 4, 4);
        AdjustPixels.Grain(piece, 3, 3, 12, 60, 3, 0, 2024, 2, 1, 1);

        var window = new byte[3 * 3 * 4];
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                Array.Copy(whole, ((y + 1) * 6 + (x + 2)) * 4, window, (y * 3 + x) * 4, 4);

        Assert.Equal(window, piece);
        Assert.NotEqual(original, whole);
    }

    [Fact]
    public void GrainOfZeroAmountOrNoUnitsChangesNothing()
    {
        var none = Gradient(4, 4);
        var before = (byte[])none.Clone();
        AdjustPixels.Grain(none, 4, 4, 16, 0, 3, 50, 1, 0, 0, 1);
        Assert.Equal(before, none);

        AdjustPixels.Grain(none, 4, 4, 16, 50, 3, 50, 1, 0, 0, 0);
        Assert.Equal(before, none);
    }

    // ------------------------------------------------------------------ Rgba clamp

    [Fact]
    public void RgbaClampPremultipliedClampsEachColourToItsAlpha()
    {
        var pixels = new byte[] { 200, 10, 5, 100, 0, 0, 0, 0, 255, 255, 255, 255, 101, 100, 99, 100 };
        AdjustPixels.RgbaClampPremultiplied(pixels, 4);
        At(pixels, 0, 100, 10, 5, 100);
        At(pixels, 1, 0, 0, 0, 0);
        At(pixels, 2, 255, 255, 255, 255);
        At(pixels, 3, 100, 100, 99, 100);
    }

    // ------------------------------------------------------------------ Black & White

    // The kernel's order: red, yellow, green, cyan, blue, magenta.
    private static float[] Weights() => new[] { 0.4f, 0.6f, 0.4f, 0.6f, 0.4f, 0.6f };

    [Fact]
    public void BlackWhiteWeighsEachPrimaryAndSecondaryByItsOwnRange()
    {
        var pixels = new byte[]
        {
            255, 0, 0, 255,     // red     → the red weight, 0.4
            0, 255, 0, 255,     // green   → the green weight, 0.4
            0, 0, 255, 255,     // blue    → the blue weight, 0.4
            255, 255, 0, 255,   // yellow  → the yellow weight, 0.6
            0, 255, 255, 255,   // cyan    → the cyan weight, 0.6
            255, 0, 255, 255,   // magenta → the magenta weight, 0.6
            128, 128, 128, 255, // neutral keeps its own level
        };
        AdjustPixels.BlackWhite(pixels, 7, 1, 28, Weights(), false, 0, 0);
        At(pixels, 0, 102, 102, 102, 255);
        At(pixels, 1, 102, 102, 102, 255);
        At(pixels, 2, 102, 102, 102, 255);
        At(pixels, 3, 153, 153, 153, 255);
        At(pixels, 4, 153, 153, 153, 255);
        At(pixels, 5, 153, 153, 153, 255);
        At(pixels, 6, 128, 128, 128, 255);
    }

    [Fact]
    public void BlackWhiteWorksOnStraightColourAndKeepsAlpha()
    {
        var pixels = new byte[] { 128, 0, 0, 128, 0, 0, 0, 0 };
        AdjustPixels.BlackWhite(pixels, 2, 1, 8, Weights(), false, 0, 0);
        // Half-alpha red is a straight red: the red weight, 0.4, premultiplied back to 0.4 × 128 = 51.2.
        At(pixels, 0, 51, 51, 51, 128);
        At(pixels, 1, 0, 0, 0, 0);
    }

    [Fact]
    public void BlackWhiteClampsItsGrayIntoRange()
    {
        var high = new byte[] { 255, 0, 0, 255 };
        AdjustPixels.BlackWhite(high, 1, 1, 4, new float[] { 2, 0, 0, 0, 0, 0 }, false, 0, 0);
        At(high, 0, 255, 255, 255, 255);

        var low = new byte[] { 255, 0, 0, 255 };
        AdjustPixels.BlackWhite(low, 1, 1, 4, new float[] { -1, 0, 0, 0, 0, 0 }, false, 0, 0);
        At(low, 0, 0, 0, 0, 255);
    }

    [Fact]
    public void ATintedBlackWhiteKeepsTheGrayAsTheLightnessOfTheHue()
    {
        // Grey 128 at hue 120 with full saturation: HSL(120, 1, 0.502) is (0.0039, 1, 0.0039).
        var pixels = new byte[] { 128, 128, 128, 255 };
        AdjustPixels.BlackWhite(pixels, 1, 1, 4, Weights(), true, 120, 1);
        At(pixels, 0, 1, 255, 1, 255);
    }

    // ------------------------------------------------------------------ Color Balance

    [Fact]
    public void ColorBalanceWithNoShiftChangesNothing()
    {
        var pixels = new byte[] { 200, 150, 100, 255, 10, 20, 30, 40 };
        var before = (byte[])pixels.Clone();
        AdjustPixels.ColorBalance(pixels, 2, 1, 8, new float[3], new float[3], new float[3], false);
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void ColorBalanceShiftsAShadowOnlyWhereTheToneIsAShadow()
    {
        // Black is all shadow: the weight is 0.7, so 0.5 red adds 0.35, premultiplied to 0.35 × 255 = 89.
        var black = new byte[] { 0, 0, 0, 255 };
        AdjustPixels.ColorBalance(black, 1, 1, 4, new float[] { 0.5f, 0, 0 }, new float[3], new float[3], false);
        At(black, 0, 89, 0, 0, 255);

        // White is no shadow at all, so the same shift does nothing.
        var white = new byte[] { 255, 255, 255, 255 };
        AdjustPixels.ColorBalance(white, 1, 1, 4, new float[] { 1, 0, 0 }, new float[3], new float[3], false);
        At(white, 0, 255, 255, 255, 255);
    }

    [Fact]
    public void ColorBalanceShiftsAMidtoneOnlyWhereTheToneIsAMidtone()
    {
        // Grey 128 is all midtone: the weight is 0.7, so 0.5 red adds 0.35 to 0.502, giving 217.
        var grey = new byte[] { 128, 128, 128, 255 };
        AdjustPixels.ColorBalance(grey, 1, 1, 4, new float[3], new float[] { 0.5f, 0, 0 }, new float[3], false);
        At(grey, 0, 217, 128, 128, 255);

        // Shadows and highlights both leave grey 128 alone.
        var untouched = new byte[] { 128, 128, 128, 255 };
        AdjustPixels.ColorBalance(untouched, 1, 1, 4, new float[] { 1, 0, 0 }, new float[3],
            new float[] { 1, 0, 0 }, false);
        At(untouched, 0, 128, 128, 128, 255);
    }

    [Fact]
    public void PreserveLuminosityPutsTheOriginalBrightnessBack()
    {
        var pixels = new byte[] { 128, 128, 128, 255 };
        AdjustPixels.ColorBalance(pixels, 1, 1, 4, new float[3], new float[] { 0.5f, 0, 0 }, new float[3], true);

        var luma = (0.299 * pixels[0] + 0.587 * pixels[1] + 0.114 * pixels[2]) / 255.0;
        Assert.Equal(128 / 255.0, luma, 3);
        Assert.True(pixels[0] > 128 && pixels[1] < 128, "the color should have moved toward red");
    }

    [Fact]
    public void ColorBalanceScalesItsShiftByAlphaAndSkipsTransparentPixels()
    {
        var pixels = new byte[] { 0, 0, 0, 0, 0, 0, 0, 128 };
        AdjustPixels.ColorBalance(pixels, 2, 1, 8, new float[] { 0.5f, 0, 0 }, new float[3], new float[3], false);
        At(pixels, 0, 0, 0, 0, 0);
        // The half-alpha black pixel gets 0.35 of red, premultiplied to 0.35 × 128 = 44.8 → 45.
        At(pixels, 1, 45, 0, 0, 128);
    }

    // ------------------------------------------------------------------ Blend modes

    private static byte[] Grey(byte value, byte alpha) => new[] { value, value, value, alpha };

    [Theory]
    // backdrop, source, expected — all three channels are the same, so one number describes the pixel.
    [InlineData(BlendMode.LinearBurn, 255, 128, 128)]   // 1 + 128/255 − 1
    [InlineData(BlendMode.LinearBurn, 128, 64, 0)]      // 128 + 64 − 255 < 0
    [InlineData(BlendMode.LinearDodgeAdd, 128, 128, 255)]
    [InlineData(BlendMode.LinearDodgeAdd, 64, 64, 128)] // 128/255
    [InlineData(BlendMode.VividLight, 128, 64, 2)]      // 1 − 127/128 = 1/128 → 1.99
    [InlineData(BlendMode.VividLight, 128, 192, 255)]   // 128/126 → 1
    [InlineData(BlendMode.LinearLight, 128, 128, 129)]  // (128 + 256 − 255)/255
    [InlineData(BlendMode.LinearLight, 64, 64, 0)]      // negative
    [InlineData(BlendMode.PinLight, 64, 192, 129)]      // max(64, 129)
    [InlineData(BlendMode.PinLight, 192, 64, 128)]      // min(192, 128)
    [InlineData(BlendMode.HardMix, 128, 128, 255)]      // exactly one
    [InlineData(BlendMode.HardMix, 64, 64, 0)]
    [InlineData(BlendMode.Subtract, 192, 64, 128)]
    [InlineData(BlendMode.Subtract, 64, 192, 0)]
    [InlineData(BlendMode.Divide, 128, 128, 255)]       // ratio one
    [InlineData(BlendMode.Divide, 128, 64, 255)]        // ratio two, clamped
    [InlineData(BlendMode.Divide, 64, 128, 128)]        // ratio a half
    [InlineData(BlendMode.Divide, 128, 0, 255)]         // a black blend channel divides to white
    public void EachHandRolledModeFollowsItsDocumentedFormula(BlendMode mode, int backdrop, int source, int expected)
    {
        var src = Grey((byte)source, 255);
        var bd = Grey((byte)backdrop, 255);
        var destination = new byte[4];
        BlendModes.CompositePixel(mode, src, bd, destination);
        At(destination, 0, expected, expected, expected, 255);
    }

    [Fact]
    public void TwoFullyTransparentPixelsCompositeToNothing()
    {
        var destination = new byte[4];
        BlendModes.CompositePixel(BlendMode.LinearBurn, new byte[4], new byte[4], destination);
        At(destination, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void ATransparentSourceLeavesTheBackdropShowing()
    {
        var destination = new byte[4];
        BlendModes.CompositePixel(BlendMode.Subtract, new byte[] { 0, 0, 0, 0 }, new byte[] { 200, 100, 50, 255 },
            destination);
        At(destination, 0, 200, 100, 50, 255);
    }

    [Fact]
    public void AnOpaqueSourceOverNothingIsTheSource()
    {
        var destination = new byte[4];
        BlendModes.CompositePixel(BlendMode.Divide, new byte[] { 100, 150, 200, 255 }, new byte[4], destination);
        At(destination, 0, 100, 150, 200, 255);
    }

    [Fact]
    public void TheCompositedAlphaAndPartialCoverageFollowTheSeparableFormula()
    {
        // Both halves: αr = 0.502 + 0.502 × 0.498 = 0.752 → 192.
        // red   = (1 − 0.502) × 0 + (1 − 0.502) × 0.502 + 0.502² × B(0, 1) = 0.25 → 64.
        // green is 0/0 on both sides, and Divide sends a zero blend channel to white: 0.502² = 0.252 → 64.
        // blue  = 0.498 × 0.502 + 0 + 0.502² × B(1, 0) = 0.502 → 128.
        var destination = new byte[4];
        BlendModes.CompositePixel(BlendMode.Divide, new byte[] { 128, 0, 0, 128 }, new byte[] { 0, 0, 128, 128 },
            destination);
        At(destination, 0, 64, 64, 128, 192);
    }

    [Fact]
    public void TheEightHandRolledModesAreTheOnesSkiaCannotDraw()
    {
        Assert.Equal(8, BlendModes.All.Count(BlendModes.IsHandRolled));
        Assert.True(BlendModes.IsHandRolled(BlendMode.LinearBurn));
        Assert.True(BlendModes.IsHandRolled(BlendMode.Divide));
        Assert.False(BlendModes.IsHandRolled(BlendMode.Multiply));
        Assert.False(BlendModes.IsHandRolled(BlendMode.Normal));
    }

    [Fact]
    public void TheTileCompositorRefusesAModeSkiaDraws()
    {
        Assert.Throws<ArgumentException>(() => BlendModes.Composite(BlendMode.Multiply, new byte[4], new byte[4],
            new byte[4], 1, 1, 4));
    }
}
