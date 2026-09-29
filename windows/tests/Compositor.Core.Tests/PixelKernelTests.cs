using Compositor.Core.Pixels;

namespace Compositor.Core.Tests;

/// <summary>
/// The ported C pixel kernels against hand-worked values. Expected values come from the formulas in
/// Compositor/Rendering/*.c and from invariants the kernels must hold (identity, determinism, an anchored
/// window matching the whole), never from a run of this code.
/// </summary>
public class PixelKernelTests
{
    private static byte[] Solid(int width, int height, byte red, byte green, byte blue, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = red;
            pixels[i * 4 + 1] = green;
            pixels[i * 4 + 2] = blue;
            pixels[i * 4 + 3] = alpha;
        }
        return pixels;
    }

    private static void Put(byte[] pixels, int width, int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        var o = (y * width + x) * 4;
        pixels[o] = red;
        pixels[o + 1] = green;
        pixels[o + 2] = blue;
        pixels[o + 3] = alpha;
    }

    private static void At(byte[] pixels, int index, int red, int green, int blue, int alpha, int tolerance = 0)
    {
        var o = index * 4;
        Assert.True(Math.Abs(pixels[o] - red) <= tolerance && Math.Abs(pixels[o + 1] - green) <= tolerance
            && Math.Abs(pixels[o + 2] - blue) <= tolerance && Math.Abs(pixels[o + 3] - alpha) <= tolerance,
            $"pixel {index} is ({pixels[o]},{pixels[o + 1]},{pixels[o + 2]},{pixels[o + 3]}),"
            + $" not ({red},{green},{blue},{alpha})");
    }

    // ------------------------------------------------------------------ Levels

    /// <summary>Table values are straight colors in 0–1, the range Levels.swift builds them in.</summary>
    private static float[] IdentityTables()
    {
        var tables = new float[3 * 256];
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < 256; i++)
                tables[c * 256 + i] = i / 255f;
        return tables;
    }

    private static float[] ConstantTables(float value)
    {
        var tables = new float[3 * 256];
        Array.Fill(tables, value);
        return tables;
    }

    [Fact]
    public void AnIdentityLevelsTableLeavesAnOpaquePixelAlone()
    {
        // x is the straight value, the table returns x/255, and round(x/255 × 255) is x again.
        var pixels = new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 128, 64, 32, 255 };
        var before = (byte[])pixels.Clone();
        LevelsPixels.LevelsApply(pixels, 3, IdentityTables());
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void LevelsApplyWorksOnUnpremultipliedColour()
    {
        // Half-alpha red: 64 premultiplied is a straight 127.5, which round-trips to 64.
        var pixels = new byte[] { 64, 0, 0, 128 };
        LevelsPixels.LevelsApply(pixels, 1, IdentityTables());
        At(pixels, 0, 64, 0, 0, 128);
    }

    [Fact]
    public void AConstantLevelsTableSetsEveryChannelToThatColour()
    {
        // 0.25 straight, premultiplied by the pixel's alpha: 0.25 × 255 = 63.75 → 64, 0.25 × 128 = 32.
        var pixels = new byte[] { 200, 100, 50, 255, 100, 60, 20, 128 };
        LevelsPixels.LevelsApply(pixels, 2, ConstantTables(0.25f));
        At(pixels, 0, 64, 64, 64, 255);
        At(pixels, 1, 32, 32, 32, 128);
    }

    [Fact]
    public void LevelsApplyClampsItsOutputToAlpha()
    {
        // A table above one asks for 510 premultiplied at full alpha and 256 at half: both clamp to alpha.
        var pixels = new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 };
        LevelsPixels.LevelsApply(pixels, 2, ConstantTables(2f));
        At(pixels, 0, 255, 255, 255, 255);
        At(pixels, 1, 128, 128, 128, 128);
    }

    [Fact]
    public void ALevelsHistogramWeighsPixelsByAlphaAndSplitsTheLuminanceBin()
    {
        // An opaque (10,20,30) and a fifth-alpha pixel whose straight colour is white.
        var pixels = new byte[] { 10, 20, 30, 255, 51, 51, 51, 51 };
        var bins = new double[1024];
        LevelsPixels.LevelsHistogram(pixels, ReadOnlySpan<byte>.Empty, 2, bins);

        // The luminance block gets a third of each pixel's weight; the channel blocks get all of it.
        Assert.Equal(1.0 / 3.0, bins[10], 12);
        Assert.Equal(1.0 / 3.0, bins[20], 12);
        Assert.Equal(1.0 / 3.0, bins[30], 12);
        Assert.Equal(1.0, bins[256 + 10], 12);
        Assert.Equal(1.0, bins[512 + 20], 12);
        Assert.Equal(1.0, bins[768 + 30], 12);

        // 51/255 of a white pixel: 0.2 into every block for its value of 255.
        Assert.Equal(0.2, bins[255], 12);
        Assert.Equal(0.2, bins[256 + 255], 12);
        Assert.Equal(0.2, bins[512 + 255], 12);
        Assert.Equal(0.2, bins[768 + 255], 12);
    }

    [Fact]
    public void ALevelsHistogramIgnoresPixelsWeightedOutByCoverage()
    {
        var pixels = new byte[] { 10, 20, 30, 255 };
        var bins = new double[1024];
        LevelsPixels.LevelsHistogram(pixels, new byte[] { 0 }, 1, bins);
        Assert.All(bins, bin => Assert.Equal(0.0, bin));

        var half = new double[1024];
        LevelsPixels.LevelsHistogram(pixels, new byte[] { 128 }, 1, half);
        Assert.Equal(128 / 255.0, half[256 + 10], 12);
        // The luminance block gets a third of that weight.
        Assert.Equal(128 / 255.0 / 3.0, half[10], 12);
    }

    [Fact]
    public void ALevelsHistogramSkipsTransparentPixels()
    {
        var pixels = new byte[] { 10, 20, 30, 0 };
        var bins = new double[1024];
        LevelsPixels.LevelsHistogram(pixels, ReadOnlySpan<byte>.Empty, 1, bins);
        Assert.All(bins, bin => Assert.Equal(0.0, bin));
    }

    /// <summary>A cube whose entry at (r, g, b) is exactly (r, g, b, 1).</summary>
    private static float[] IdentityCube()
    {
        var cube = new float[8 * 4];
        for (var b = 0; b < 2; b++)
            for (var g = 0; g < 2; g++)
                for (var r = 0; r < 2; r++)
                {
                    var i = (r + g * 2 + b * 4) * 4;
                    cube[i] = r;
                    cube[i + 1] = g;
                    cube[i + 2] = b;
                    cube[i + 3] = 1;
                }
        return cube;
    }

    [Fact]
    public void AnIdentityCubeIsANoOp()
    {
        var pixels = new byte[] { 128, 64, 32, 255, 0, 0, 0, 255, 255, 255, 255, 255, 7, 200, 99, 255, 64, 32, 16, 128 };
        var before = (byte[])pixels.Clone();
        LevelsPixels.CubeApply(pixels, 5, IdentityCube(), 2);
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void ACubeThatSwapsChannelsActuallySwapsThem()
    {
        // red ← blue, green ← red, blue ← green: the pure primaries cycle r → g → b → r.
        var cube = new float[8 * 4];
        for (var b = 0; b < 2; b++)
            for (var g = 0; g < 2; g++)
                for (var r = 0; r < 2; r++)
                {
                    var i = (r + g * 2 + b * 4) * 4;
                    cube[i] = b;
                    cube[i + 1] = r;
                    cube[i + 2] = g;
                    cube[i + 3] = 1;
                }
        var pixels = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255 };
        LevelsPixels.CubeApply(pixels, 3, cube, 2);
        At(pixels, 0, 0, 255, 0, 255);
        At(pixels, 1, 0, 0, 255, 255);
        At(pixels, 2, 255, 0, 0, 255);
    }

    [Fact]
    public void ACubeSmallerThanTwoEntriesIsLeftAlone()
    {
        // The C indexes below zero for a one-entry cube; the port refuses instead and changes nothing.
        var pixels = new byte[] { 10, 20, 30, 255 };
        LevelsPixels.CubeApply(pixels, 1, new float[4], 1);
        At(pixels, 0, 10, 20, 30, 255);
    }

    // ------------------------------------------------------------------ Brush

    [Fact]
    public void AlphaBoundsIsHalfOpenAroundTheNonzeroAlpha()
    {
        var pixels = new byte[4 * 4 * 4];
        Put(pixels, 4, 1, 1, 9, 9, 9, 255);
        Put(pixels, 4, 2, 3, 9, 9, 9, 255);
        var bounds = new int[4];
        BrushPixels.AlphaBounds(pixels, 4, 4, 16, bounds);
        Assert.Equal(new[] { 1, 1, 3, 4 }, bounds);
    }

    [Fact]
    public void AlphaBoundsOfNothingIsAllZero()
    {
        var bounds = new int[4];
        BrushPixels.AlphaBounds(new byte[4 * 4 * 4], 4, 4, 16, bounds);
        Assert.Equal(new[] { 0, 0, 0, 0 }, bounds);
    }

    [Fact]
    public void AlphaBoundsOfASinglePixelIsOneSquare()
    {
        var bounds = new int[4];
        BrushPixels.AlphaBounds(new byte[] { 0, 0, 0, 255 }, 1, 1, 4, bounds);
        Assert.Equal(new[] { 0, 0, 1, 1 }, bounds);
    }

    [Fact]
    public void AlphaBoundsFollowsTheRowsThroughTheirStride()
    {
        var pixels = new byte[3 * 4 * 2];
        Put(pixels, 3, 0, 0, 0, 0, 0, 0);
        Put(pixels, 3, 1, 0, 0, 0, 0, 255);
        Put(pixels, 3, 0, 1, 0, 0, 0, 255);
        Put(pixels, 3, 1, 1, 0, 0, 0, 0);
        var bounds = new int[4];
        BrushPixels.AlphaBounds(pixels, 2, 2, 12, bounds);
        Assert.Equal(new[] { 0, 0, 2, 2 }, bounds);
    }

    [Fact]
    public void ExtractAlphaCopiesTheFourthByteOfEveryPixel()
    {
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var gray = new byte[3 * 2];
        BrushPixels.ExtractAlpha(pixels, 8, gray, 3, 2, 2);
        Assert.Equal(new byte[] { 4, 8, 0, 12, 16, 0 }, gray);
    }

    [Fact]
    public void UnpremultiplyOpaqueDividesByAlphaInBytes()
    {
        // (64 × 255 + 64) / 128 = 128, and (200 × 255 + 50) / 100 = 510 clamps to 255.
        var pixels = new byte[] { 64, 0, 0, 128, 255, 0, 0, 255, 200, 10, 5, 100, 0, 0, 0, 0 };
        BrushPixels.UnpremultiplyOpaque(pixels, 16, 4, 1);
        At(pixels, 0, 128, 0, 0, 255);
        At(pixels, 1, 255, 0, 0, 255);
        At(pixels, 2, 255, 26, 13, 255);
        At(pixels, 3, 0, 0, 0, 255);
    }

    [Fact]
    public void RestoreAlphaMultipliesByCoverageInBytes()
    {
        // (255 × 128 + 127) / 255 = 128; a zero coverage byte clears the colour.
        var pixels = new byte[] { 255, 255, 255, 255, 100, 200, 50, 255, 10, 20, 30, 255 };
        var alpha = new byte[] { 128, 0, 255 };
        BrushPixels.RestoreAlpha(pixels, 12, alpha, 3, 3, 1);
        At(pixels, 0, 128, 128, 128, 128);
        At(pixels, 1, 0, 0, 0, 0);
        At(pixels, 2, 10, 20, 30, 255);
    }

    // ------------------------------------------------------------------ Lens

    [Fact]
    public void LensDistortAtZeroIsTheIdentity()
    {
        var source = new byte[] { 10, 20, 30, 255, 40, 50, 60, 128, 1, 2, 3, 0, 200, 100, 50, 200 };
        var destination = new byte[source.Length];
        LensPixels.LensDistort(source, destination, 4, 1, 16, 0);
        Assert.Equal(source, destination);
    }

    [Fact]
    public void AConstantImageIsUnchangedByAnInwardLensDistortion()
    {
        // k > 0 only ever samples nearer the centre, so a uniform image has nothing to interpolate toward.
        var source = Solid(8, 8, 90, 140, 210, 255);
        var destination = new byte[source.Length];
        LensPixels.LensDistort(source, destination, 8, 8, 32, 0.3);
        Assert.Equal(source, destination);
    }

    // ------------------------------------------------------------------ Noise

    [Fact]
    public void NoiseAddIsTheSameForTheSameSeed()
    {
        var first = Solid(8, 8, 128, 128, 128, 255);
        var second = (byte[])first.Clone();
        NoisePixels.NoiseAdd(first, 8, 8, 32, 50, false, true, 12345);
        NoisePixels.NoiseAdd(second, 8, 8, 32, 50, false, true, 12345);
        Assert.Equal(first, second);
        Assert.NotEqual(Solid(8, 8, 128, 128, 128, 255), first);
    }

    [Fact]
    public void NoiseAddDiffersForADifferentSeed()
    {
        var first = Solid(8, 8, 128, 128, 128, 255);
        var second = Solid(8, 8, 128, 128, 128, 255);
        NoisePixels.NoiseAdd(first, 8, 8, 32, 50, false, true, 1);
        NoisePixels.NoiseAdd(second, 8, 8, 32, 50, false, true, 2);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NoiseAddLeavesAlphaAndTransparentPixelsAlone()
    {
        var pixels = new byte[] { 128, 128, 128, 255, 64, 64, 64, 128, 0, 0, 0, 0 };
        NoisePixels.NoiseAdd(pixels, 3, 1, 12, 80, false, false, 7);
        Assert.Equal(255, pixels[3]);
        Assert.Equal(128, pixels[7]);
        Assert.Equal(0, pixels[11]);
        At(pixels, 2, 0, 0, 0, 0);
    }

    [Fact]
    public void MonochromaticNoiseKeepsAGreyPixelGrey()
    {
        var pixels = Solid(4, 4, 128, 128, 128, 255);
        NoisePixels.NoiseAdd(pixels, 4, 4, 16, 50, false, true, 99);
        for (var i = 0; i < 16; i++)
            Assert.True(pixels[i * 4] == pixels[i * 4 + 1] && pixels[i * 4 + 1] == pixels[i * 4 + 2],
                $"pixel {i} is not monochromatic: ({pixels[i * 4]},{pixels[i * 4 + 1]},{pixels[i * 4 + 2]})");
    }

    [Fact]
    public void PerChannelNoiseIsNotMonochromatic()
    {
        var mono = Solid(4, 4, 128, 128, 128, 255);
        var channel = (byte[])mono.Clone();
        NoisePixels.NoiseAdd(mono, 4, 4, 16, 50, false, true, 99);
        NoisePixels.NoiseAdd(channel, 4, 4, 16, 50, false, false, 99);
        Assert.NotEqual(mono, channel);
        Assert.Contains(Enumerable.Range(0, 16), i => channel[i * 4] != channel[i * 4 + 1]);
    }

    [Fact]
    public void NoiseAddAtAnchorsAPieceOfAnImageAtTheSamePlaceAsTheWhole()
    {
        var whole = Solid(8, 8, 128, 128, 128, 255);
        NoisePixels.NoiseAdd(whole, 8, 8, 32, 50, false, true, 4242);

        var window = new byte[3 * 3 * 4];
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                Array.Copy(whole, ((y + 3) * 8 + (x + 2)) * 4, window, (y * 3 + x) * 4, 4);

        var piece = Solid(3, 3, 128, 128, 128, 255);
        NoisePixels.NoiseAddAt(piece, 3, 3, 12, 50, false, true, 4242, 2, 3);
        Assert.Equal(window, piece);

        // Without the origin it hashes at the piece's own coordinates and no longer matches.
        var unanchored = Solid(3, 3, 128, 128, 128, 255);
        NoisePixels.NoiseAdd(unanchored, 3, 3, 12, 50, false, true, 4242);
        Assert.NotEqual(window, unanchored);
    }

    [Fact]
    public void NoiseOfZeroAmountChangesNothing()
    {
        var pixels = Solid(4, 4, 128, 128, 128, 255);
        var before = (byte[])pixels.Clone();
        NoisePixels.NoiseAdd(pixels, 4, 4, 16, 0, false, false, 5);
        Assert.Equal(before, pixels);
    }

    // ------------------------------------------------------------------ Wand

    private static byte[] TwoColours(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Put(pixels, width, x, y, x < width / 2 ? (byte)255 : (byte)0, 0,
                    x < width / 2 ? (byte)0 : (byte)255, 255);
        return pixels;
    }

    [Fact]
    public void WandMaskSelectsEveryPixelOfTheClickedColour()
    {
        var pixels = TwoColours(4, 4);
        var mask = new byte[16];
        var count = WandPixels.WandMask(pixels, 4, 4, 16, 0, 0, 0, 0, false, mask);
        Assert.Equal(8, count);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                Assert.Equal(x < 2 ? (byte)255 : (byte)0, mask[y * 4 + x]);
    }

    [Fact]
    public void WandMaskContiguousStopsAtTheColourBoundary()
    {
        var pixels = TwoColours(4, 4);
        var mask = new byte[16];
        Assert.Equal(8, WandPixels.WandMask(pixels, 4, 4, 16, 0, 0, 0, 0, true, mask));
        Assert.Equal(8, WandPixels.WandMask(pixels, 4, 4, 16, 2, 0, 0, 0, true, mask));
        Assert.Equal((byte)0, mask[0]);
        Assert.Equal((byte)255, mask[3]);
    }

    [Fact]
    public void WandMaskToleranceIsInclusive()
    {
        var pixels = new byte[] { 100, 0, 0, 255, 110, 0, 0, 255 };
        var mask = new byte[2];
        Assert.Equal(2, WandPixels.WandMask(pixels, 2, 1, 8, 0, 0, 0, 10, false, mask));
        Assert.Equal(1, WandPixels.WandMask(pixels, 2, 1, 8, 0, 0, 0, 9, false, mask));
    }

    [Fact]
    public void WandMaskAveragesTheReferenceColourOverTheRadius()
    {
        // Red values 10, 20, 30: the radius-1 average is (60 + 3 / 2) / 3 = 20, so only the middle matches.
        var pixels = new byte[] { 10, 0, 0, 255, 20, 0, 0, 255, 30, 0, 0, 255 };
        var mask = new byte[3];
        var count = WandPixels.WandMask(pixels, 3, 1, 12, 1, 0, 1, 0, false, mask);
        Assert.Equal(1, count);
        Assert.Equal(new byte[] { 0, 255, 0 }, mask);
    }

    [Fact]
    public void WandMaskOutsideTheImageSelectsNothing()
    {
        var pixels = TwoColours(4, 4);
        var mask = new byte[16];
        Assert.Equal(0, WandPixels.WandMask(pixels, 4, 4, 16, 9, 0, 0, 0, false, mask));
    }

    [Fact]
    public void ColorRangeMaskSelectsTheIncludedColoursAndNeverTransparentPixels()
    {
        var pixels = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 0, 0 };
        var mask = new byte[3];
        var include = new byte[] { 255, 0, 0 };

        Assert.Equal(1, WandPixels.ColorRangeMask(pixels, 3, 1, 12, include, 1, ReadOnlySpan<byte>.Empty, 0, 0, false, mask));
        Assert.Equal(new byte[] { 255, 0, 0 }, mask);

        // Inverted, the pixels that don't match are selected — including the transparent one.
        Assert.Equal(2, WandPixels.ColorRangeMask(pixels, 3, 1, 12, include, 1, ReadOnlySpan<byte>.Empty, 0, 0, true, mask));

        Assert.Equal(0, WandPixels.ColorRangeMask(pixels, 3, 1, 12, include, 1, include, 1, 0, false, mask));
    }

    [Fact]
    public void ColorRangeMaskWorksOnStraightColourAndHonoursFuzziness()
    {
        // Premultiplied (128, 0, 0, 128) is a straight red of 255.
        var red = new byte[] { 128, 0, 0, 128 };
        var mask = new byte[1];
        Assert.Equal(1, WandPixels.ColorRangeMask(red, 1, 1, 4, new byte[] { 255, 0, 0 }, 1,
            ReadOnlySpan<byte>.Empty, 0, 0, false, mask));

        var near = new byte[] { 110, 0, 0, 255 };
        Assert.Equal(1, WandPixels.ColorRangeMask(near, 1, 1, 4, new byte[] { 100, 0, 0 }, 1,
            ReadOnlySpan<byte>.Empty, 0, 10, false, mask));
        Assert.Equal(0, WandPixels.ColorRangeMask(near, 1, 1, 4, new byte[] { 100, 0, 0 }, 1,
            ReadOnlySpan<byte>.Empty, 0, 9, false, mask));
    }

    private static HashSet<(int X, int Y)> Corners(int[] points, int pointCount)
    {
        var corners = new HashSet<(int, int)>();
        for (var i = 0; i < pointCount; i++)
            corners.Add((points[i * 2], points[i * 2 + 1]));
        return corners;
    }

    [Fact]
    public void WandTraceOutlinesASolidSquareAlongItsEdges()
    {
        var mask = new byte[3 * 3];
        Array.Fill(mask, (byte)255);
        var status = WandPixels.WandTrace(mask, 3, 3, out var points, out var pointCount, out var loops, out var loopCount);
        Assert.Equal(0, status);
        Assert.Equal(1, loopCount);
        Assert.Equal(4, loops[0]);
        Assert.Equal(4, pointCount);
        // Corner coordinates run along pixel edges, so a 3 × 3 square ends at 3.
        Assert.True(Corners(points, pointCount).SetEquals(new[] { (0, 0), (3, 0), (3, 3), (0, 3) }));
    }

    [Fact]
    public void WandTraceOutlinesASinglePixel()
    {
        var status = WandPixels.WandTrace(new byte[] { 255 }, 1, 1, out var points, out var pointCount,
            out var loops, out var loopCount);
        Assert.Equal(0, status);
        Assert.Equal(1, loopCount);
        Assert.Equal(4, loops[0]);
        Assert.True(Corners(points, pointCount).SetEquals(new[] { (0, 0), (1, 0), (1, 1), (0, 1) }));
    }

    [Fact]
    public void WandTraceOutlinesAHoleCounterclockwise()
    {
        var mask = new byte[5 * 5];
        Array.Fill(mask, (byte)255);
        mask[2 * 5 + 2] = 0;
        var status = WandPixels.WandTrace(mask, 5, 5, out var points, out var pointCount, out var loops, out var loopCount);
        Assert.Equal(0, status);
        Assert.Equal(2, loopCount);
        Assert.Equal(4, loops[0]);
        Assert.Equal(4, loops[1]);
        Assert.True(Corners(points, pointCount).SetEquals(new[]
        {
            (0, 0), (5, 0), (5, 5), (0, 5), (2, 2), (3, 2), (3, 3), (2, 3),
        }));
    }

    // ------------------------------------------------------------------ Content fill

    [Fact]
    public void AnEmptyContentFillMaskReportsSuccessAndChangesNothing()
    {
        // The C sets donorCount so that "nothing to fill" still returns 1, and callers rely on it.
        var pixels = Solid(8, 8, 0, 0, 255, 255);
        var before = (byte[])pixels.Clone();
        Assert.Equal(1, ContentFill.Fill(pixels, 32, new byte[64], 8, 8, 8));
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void AContentFillWithNoDonorReportsFailure()
    {
        // Nothing opaque is available to copy from: every unselected pixel is transparent.
        var pixels = Solid(4, 4, 0, 0, 0, 0);
        var mask = new byte[16];
        mask[0] = 255;
        Assert.Equal(0, ContentFill.Fill(pixels, 16, mask, 4, 4, 4));
    }

    [Fact]
    public void AContentFillCopiesTheSurroundingColourIntoTheHole()
    {
        var pixels = Solid(16, 16, 0, 0, 255, 255);
        Put(pixels, 16, 7, 7, 0, 255, 0, 255);
        Put(pixels, 16, 8, 7, 0, 255, 0, 255);
        Put(pixels, 16, 7, 8, 0, 255, 0, 255);
        Put(pixels, 16, 8, 8, 0, 255, 0, 255);
        var mask = new byte[16 * 16];
        mask[7 * 16 + 7] = mask[7 * 16 + 8] = mask[8 * 16 + 7] = mask[8 * 16 + 8] = 255;

        Assert.Equal(1, ContentFill.Fill(pixels, 64, mask, 16, 16, 16));

        // The only donor colour is the surrounding blue, so the hole has to come out blue.
        At(pixels, 7 * 16 + 7, 0, 0, 255, 255);
        At(pixels, 7 * 16 + 8, 0, 0, 255, 255);
        At(pixels, 8 * 16 + 7, 0, 0, 255, 255);
        At(pixels, 8 * 16 + 8, 0, 0, 255, 255);
    }

    [Fact]
    public void AContentFillLeavesThePixelsOutsideItsMaskAlone()
    {
        var pixels = Solid(16, 16, 0, 0, 255, 255);
        var before = (byte[])pixels.Clone();
        foreach (var (x, y) in new[] { (7, 7), (8, 7), (7, 8), (8, 8) })
            Put(pixels, 16, x, y, 0, 255, 0, 255);
        var mask = new byte[16 * 16];
        foreach (var (x, y) in new[] { (7, 7), (8, 7), (7, 8), (8, 8) })
            mask[y * 16 + x] = 255;

        Assert.Equal(1, ContentFill.Fill(pixels, 64, mask, 16, 16, 16));

        // Donors are read, never written: only the selected pixels may differ from the input.
        for (var i = 0; i < 256; i++)
        {
            if (mask[i] != 0) continue;
            Assert.Equal(before[i * 4], pixels[i * 4]);
            Assert.Equal(before[i * 4 + 1], pixels[i * 4 + 1]);
            Assert.Equal(before[i * 4 + 2], pixels[i * 4 + 2]);
            Assert.Equal(before[i * 4 + 3], pixels[i * 4 + 3]);
        }
    }

    // ------------------------------------------------------------------ Heal

    [Fact]
    public void CoverageBoundsIsHalfOpenAroundTheNonzeroBytes()
    {
        var gray = new byte[3 * 6];
        gray[1 * 6 + 1] = 1;
        gray[1 * 6 + 2] = 1;
        gray[2 * 6 + 1] = 1;
        var bounds = new int[4];
        HealPixels.CoverageBounds(gray, 4, 3, 6, bounds);
        Assert.Equal(new[] { 1, 1, 3, 3 }, bounds);
    }

    [Fact]
    public void CoverageBoundsOfNothingIsAllZero()
    {
        var bounds = new int[4];
        HealPixels.CoverageBounds(new byte[4 * 4], 4, 4, 4, bounds);
        Assert.Equal(new[] { 0, 0, 0, 0 }, bounds);
    }

    [Fact]
    public void CoverageBoundsOfASinglePixelIsOneSquare()
    {
        var gray = new byte[] { 7, 0, 0, 0 };
        var bounds = new int[4];
        HealPixels.CoverageBounds(gray, 2, 2, 2, bounds);
        Assert.Equal(new[] { 0, 0, 1, 1 }, bounds);
    }

    [Fact]
    public void AUniformRegionComesBackFromSpotHealingUnchanged()
    {
        var pixels = Solid(32, 32, 120, 120, 120, 255);
        // A transparent block far from the spot, to show fully transparent pixels are left alone.
        for (var y = 2; y < 5; y++)
            for (var x = 2; x < 5; x++)
                Put(pixels, 32, x, y, 0, 0, 0, 0);
        var before = (byte[])pixels.Clone();
        var coverage = new byte[32 * 32];
        for (var y = 14; y < 18; y++)
            for (var x = 14; x < 18; x++)
                coverage[y * 32 + x] = 255;

        // Create Texture fills smoothly from the spot's edges: a uniform spot has no difference to carry in.
        Assert.Equal(0, HealPixels.SpotHeal(pixels, coverage, 32, 32, 128, 1f, 1, 7));
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void SpotHealingAnEmptyCoverageDoesNothing()
    {
        var pixels = Solid(16, 16, 10, 20, 30, 255);
        var before = (byte[])pixels.Clone();
        Assert.Equal(0, HealPixels.SpotHeal(pixels, new byte[16 * 16], 16, 16, 64, 1f, 1, 3));
        Assert.Equal(before, pixels);
    }

    // ------------------------------------------------------------------ Dither

    private static DitherParams Params(int style, int levels = 2) => new()
    {
        Style = style,
        Levels = levels,
        Diffusion = 1,
        Density = 0,
        Contrast = 0,
        Cell = 8,
        Angle = 0,
        LightOnDark = false,
        OriginalColors = false,
        Dark = new byte[] { 0, 0, 0 },
        Light = new byte[] { 255, 255, 255 },
    };

    [Fact]
    public void AnOrderedDitherSplitsMidGreyInHalf()
    {
        var pixels = Solid(8, 8, 128, 128, 128, 255);
        Assert.Equal(1, DitherPixels.DitherApply(pixels, 8, 8, 32, Params(DitherPixels.Bayer8)));

        // 128/255 is above the 32 thresholds 0.5 to 0.5078 and below the 32 below them: exactly half.
        var on = 0;
        for (var i = 0; i < 64; i++)
        {
            var value = pixels[i * 4];
            Assert.True(value is 0 or 255, $"pixel {i} is {value}, not on or off");
            Assert.True(pixels[i * 4] == pixels[i * 4 + 1] && pixels[i * 4 + 1] == pixels[i * 4 + 2]);
            Assert.Equal(255, pixels[i * 4 + 3]);
            if (value == 255) on++;
        }
        Assert.Equal(32, on);
    }

    [Fact]
    public void AnOrderedDitherSendsWhiteToWhiteAndBlackToBlack()
    {
        var white = Solid(8, 8, 255, 255, 255, 255);
        DitherPixels.DitherApply(white, 8, 8, 32, Params(DitherPixels.Bayer8));
        for (var i = 0; i < 64; i++) At(white, i, 255, 255, 255, 255);

        var black = Solid(8, 8, 0, 0, 0, 255);
        DitherPixels.DitherApply(black, 8, 8, 32, Params(DitherPixels.Bayer8));
        for (var i = 0; i < 64; i++) At(black, i, 0, 0, 0, 255);
    }

    [Fact]
    public void ANonOriginalOrderedDitherUsesTheDarkAndLightColours()
    {
        var pixels = Solid(8, 8, 128, 128, 128, 255);
        var parameters = Params(DitherPixels.Bayer8);
        parameters.Dark = new byte[] { 0, 0, 40 };
        parameters.Light = new byte[] { 255, 200, 0 };
        DitherPixels.DitherApply(pixels, 8, 8, 32, parameters);
        for (var i = 0; i < 64; i++)
        {
            var on = pixels[i * 4 + 1] == 200;
            Assert.True(on ? pixels[i * 4] == 255 && pixels[i * 4 + 2] == 0 : pixels[i * 4] == 0 && pixels[i * 4 + 2] == 40,
                $"pixel {i} is not one of the two colours: ({pixels[i * 4]},{pixels[i * 4 + 1]},{pixels[i * 4 + 2]})");
        }
    }

    [Fact]
    public void DitherDotsLeavesTheDotCentreAndReplacesTheCorners()
    {
        var pixels = Solid(8, 8, 255, 255, 255, 255);
        DitherPixels.DitherDots(pixels, 8, 8, 32, 8, new byte[] { 0, 0, 0 });

        // The corner is a full block away from the dot's middle, so it takes the gap colour alone.
        At(pixels, 0, 0, 0, 0, 255);
        // The middle is inside the dot, which is left untouched.
        At(pixels, 4 * 8 + 4, 255, 255, 255, 255);
        At(pixels, 3 * 8 + 3, 255, 255, 255, 255);
        // An edge pixel sits on the smoothed rim: radius 3.36, distance √12.5, cover 0.3245 × 255 → 83.
        At(pixels, 4 * 8 + 0, 83, 83, 83, 255, 1);
    }

    [Fact]
    public void DitherDotsBlendsTowardTheGapColour()
    {
        var pixels = new byte[] { 100, 150, 200, 255 };
        DitherPixels.DitherDots(pixels, 1, 1, 4, 8, new byte[] { 10, 20, 30 });
        // (0,0) is fully outside the dot, so it is the gap colour straight.
        At(pixels, 0, 10, 20, 30, 255);
    }

    [Fact]
    public void DitherDotsDoesNothingForABlockSmallerThanTwo()
    {
        var pixels = Solid(4, 4, 100, 100, 100, 255);
        var before = (byte[])pixels.Clone();
        DitherPixels.DitherDots(pixels, 4, 4, 16, 1, new byte[] { 0, 0, 0 });
        Assert.Equal(before, pixels);
    }
}
