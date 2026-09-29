using Compositor.Core.Pixels;

namespace Compositor.Core.Tests;

/// <summary>
/// The Gaussian the filters blur with. Past a sigma of eight the kernel is not the exact one but four box
/// passes that stand in for it — a radius of 250 costs minutes when every pixel is a tap, and the preview
/// runs on the thread that draws the window — so these tests hold the stand-in to the real thing: the same
/// ramp out of the same edge, the same half-weight fade at the border of a field, and exactly the kernel
/// below the crossover.
/// <para>
/// The reference is written out the plain way, a tap a pixel in double precision and no care for cost, so it
/// is a specification of the Gaussian rather than a second copy of the code under test. A flat field is the
/// one case where the two axes can be worked out apart — a separable kernel applied to a field with no
/// gradient — and that is what makes a sigma of 250 affordable to compare against.
/// </para>
/// </summary>
public class GaussianBlurTests
{
    /// <summary>The exact Gaussian's weights, one tap per pixel and three standard deviations wide.</summary>
    private static double[] Weights(double sigma, out int half)
    {
        half = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var weights = new double[half * 2 + 1];
        var total = 0.0;
        for (var tap = -half; tap <= half; tap++)
        {
            weights[tap + half] = Math.Exp(-(tap * (double)tap) / (2 * sigma * sigma));
            total += weights[tap + half];
        }
        for (var index = 0; index < weights.Length; index++) weights[index] /= total;
        return weights;
    }

    /// <summary>
    /// The exact Gaussian: rows first, then columns, reading outside the raster as the edge pixel when
    /// <paramref name="zeroPadded"/> is false and as nothing at all when it is true.
    /// </summary>
    private static byte[] Reference(byte[] source, int width, int height, int channels, int stride, double sigma,
                                    bool zeroPadded)
    {
        var weights = Weights(sigma, out var half);
        var plane = new double[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var channel = 0; channel < channels; channel++)
                    plane[(y * width + x) * channels + channel] = source[y * stride + x * channels + channel];
            }
        }

        var rows = new double[plane.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sum = 0.0;
                    for (var tap = -half; tap <= half; tap++)
                    {
                        var column = x + tap;
                        if (zeroPadded)
                        {
                            if ((uint)column >= (uint)width) continue;
                        }
                        else
                        {
                            column = Math.Clamp(column, 0, width - 1);
                        }
                        sum += weights[tap + half] * plane[(y * width + column) * channels + channel];
                    }
                    rows[(y * width + x) * channels + channel] = sum;
                }
            }
        }

        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sum = 0.0;
                    for (var tap = -half; tap <= half; tap++)
                    {
                        var row = y + tap;
                        if (zeroPadded)
                        {
                            if ((uint)row >= (uint)height) continue;
                        }
                        else
                        {
                            row = Math.Clamp(row, 0, height - 1);
                        }
                        sum += weights[tap + half] * rows[(row * width + x) * channels + channel];
                    }
                    result[y * stride + x * channels + channel] =
                        (byte)Math.Clamp((int)Math.Round(sum, MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// What the exact Gaussian leaves of a flat field one pixel at a time: the share of the kernel's weight
    /// that lands inside the raster at that column, which the two axes contribute to apart because the field
    /// has no gradient to carry information between them.
    /// </summary>
    private static double[] Share(double sigma, int side)
    {
        var weights = Weights(sigma, out var half);
        var share = new double[side];
        for (var x = 0; x < side; x++)
        {
            var sum = 0.0;
            for (var tap = -half; tap <= half; tap++)
            {
                var column = x + tap;
                if ((uint)column < (uint)side) sum += weights[tap + half];
            }
            share[x] = sum;
        }
        return share;
    }

    /// <summary>
    /// A raster one pixel tall and eight sigma wide, opaque on its right half: one hard edge, far enough from
    /// the border that the border's own rule cannot reach the ramp. One pixel tall also means the vertical
    /// pass reads the same row again whichever rule it follows, so what is compared is the horizontal kernel.
    /// </summary>
    private static byte[] Edge(double sigma)
    {
        var width = ((int)(sigma * 8) | 1) + 2;
        var source = new byte[width];
        for (var x = width / 2; x < width; x++) source[x] = 255;
        return source;
    }

    private static int Worst(byte[] left, byte[] right)
    {
        var worst = 0;
        for (var index = 0; index < left.Length; index++) worst = Math.Max(worst, Math.Abs(left[index] - right[index]));
        return worst;
    }

    /// <summary>Whether the edge has softened into a ramp rather than stayed a step or been flattened out.</summary>
    private static void AssertSoftened(byte[] blurred, string what)
    {
        var lowest = blurred.Min();
        var highest = blurred.Max();
        var span = highest - lowest;
        Assert.True(span > 40, $"{what}: the edge did not soften, the raster spans {span}");
        Assert.True(
            blurred.Any(level => level > lowest + span / 4 && level < highest - span / 4),
            $"{what}: the edge is still hard");
    }

    [Theory]
    [InlineData(9.0)]
    [InlineData(25.0)]
    [InlineData(60.0)]
    [InlineData(250.0)]
    public void TheBoxesStandInForTheGaussianWithinFourLevels(double sigma)
    {
        var source = Edge(sigma);
        var blurred = (byte[])source.Clone();
        GaussianBlur.Clamped(blurred, source.Length, 1, 1, source.Length, sigma);
        var exact = Reference(source, source.Length, 1, 1, source.Length, sigma, zeroPadded: false);
        var worst = Worst(blurred, exact);
        Assert.True(worst <= 4, $"sigma {sigma}: {worst} levels from the Gaussian");
        AssertSoftened(blurred, $"sigma {sigma}");
    }

    [Theory]
    [InlineData(9.0)]
    [InlineData(20.0)]
    [InlineData(60.0)]
    [InlineData(250.0)]
    public void AFlatFieldFadesToHalfAtItsBorderAndAQuarterAtItsCorner(double sigma)
    {
        // Where the boxes do their damage if they are left to it: each box pass pads its own border and the
        // next one compounds the loss, so a zero-padded blur of a flat field used to leave its outermost row
        // at a seventh of what the Gaussian leaves — 73 against 130 at a sigma of twenty. Half the kernel's
        // weight lies past the border of a flat field and three quarters past a corner, whatever the blur is
        // made of. Six levels of 255 is the loosest the two come out anywhere: a corner, where both borders
        // fade at once and the two shoulders show their difference twice over. Four channels as well as one,
        // because a channel's blur reads the room the last one left behind.
        foreach (var channels in new[] { 1, 4 })
        {
            var side = (int)Math.Ceiling(sigma * 6) + 3;
            var source = new byte[side * side * channels];
            Array.Fill(source, (byte)255);
            GaussianBlur.ZeroPadded(source, side, side, channels, side * channels, sigma);

            var share = Share(sigma, side);
            var worst = 0;
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var expected = (byte)Math.Clamp(
                        (int)Math.Round(255 * share[x] * share[y], MidpointRounding.AwayFromZero), 0, 255);
                    for (var channel = 0; channel < channels; channel++)
                        worst = Math.Max(worst, Math.Abs(source[(y * side + x) * channels + channel] - expected));
                }
            }
            Assert.True(worst <= 6, $"sigma {sigma} in {channels} channels: {worst} levels from the Gaussian");

            // The plane is six sigma across, so its middle has all of the kernel and its border and corner the
            // halves and quarters a Gaussian fades to.
            var middle = side / 2;
            Assert.Equal(255, source[(middle * side + middle) * channels]);
            Assert.True(source[middle * side * channels] is > 100 and < 160,
                $"sigma {sigma}: the border is {source[middle * side * channels]}");
            Assert.True(source[0] is > 30 and < 90, $"sigma {sigma}: the corner is {source[0]}");
            // The channels are blurred alike, so none of them carries the last one's room along.
            for (var channel = 1; channel < channels; channel++)
            {
                Assert.Equal(source[0], source[channel]);
                Assert.Equal(source[middle * side * channels], source[middle * side * channels + channel]);
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(8.0)]
    public void TheNarrowBlursAreTheExactKernel(double sigma)
    {
        foreach (var zeroPadded in new[] { false, true })
        {
            var source = Edge(sigma);
            var blurred = (byte[])source.Clone();
            if (zeroPadded) GaussianBlur.ZeroPadded(blurred, source.Length, 1, 1, source.Length, sigma);
            else GaussianBlur.Clamped(blurred, source.Length, 1, 1, source.Length, sigma);
            var exact = Reference(source, source.Length, 1, 1, source.Length, sigma, zeroPadded);
            Assert.Equal(exact, blurred);
        }
    }

    [Fact]
    public void AFlatRasterSurvivesTheBlurWhole()
    {
        // A running total that loses a tap at the border, or counts one twice, shows up here as a level
        // missing from the middle of a flat picture.
        foreach (var sigma in new[] { 3.0, 30.0, 250.0 })
        {
            var source = new byte[400 * 3];
            Array.Fill(source, (byte)200);
            GaussianBlur.Clamped(source, 400, 3, 1, 400, sigma);
            Assert.All(source, level => Assert.Equal(200, level));
        }
    }

    [Fact]
    public void ABlurOfNoWidthChangesNothing()
    {
        var source = new byte[64];
        for (var index = 0; index < source.Length; index++) source[index] = (byte)(index * 3);
        var before = (byte[])source.Clone();
        GaussianBlur.Clamped(source, 64, 1, 1, 64, 0);
        GaussianBlur.ZeroPadded(source, 64, 1, 1, 64, 0);
        Assert.Equal(before, source);
    }
}
