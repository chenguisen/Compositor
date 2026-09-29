using Compositor.Core.Pixels;

namespace Compositor.Core.Tests;

/// <summary>
/// The motion blur's kernel. A streak is a line of samples across the picture, so running it out costs its
/// length times the pixels; past a spread of one it is instead sheared until the line runs along the frame's
/// rows, walked with a running total and sheared back. These tests hold that to the kernel it stands in for —
/// a tap every pixel along the line, read bilinearly, which is what Core Image's <c>CIMotionBlur</c> does —
/// and check the streak's own shape, so a shear with the wrong slope cannot pass by being small.
/// </summary>
public class MotionPixelsTests
{
    /// <summary>
    /// The kernel the fast path stands in for: a tap every pixel along the line out to three standard
    /// deviations, weighted by the Gaussian and read bilinearly, anything outside the picture as nothing.
    /// Written out plainly, so it is a specification rather than a second copy of the code under test.
    /// </summary>
    private static byte[] Reference(byte[] source, int width, int height, double sigma, double radians)
    {
        var dx = Math.Cos(radians);
        var dy = -Math.Sin(radians);
        var count = (int)Math.Ceiling(sigma * 3);
        var weights = new double[count * 2 + 1];
        var total = 0.0;
        for (var tap = -count; tap <= count; tap++)
        {
            weights[tap + count] = Math.Exp(-(tap * (double)tap) / (2 * sigma * sigma));
            total += weights[tap + count];
        }
        for (var index = 0; index < weights.Length; index++) weights[index] /= total;

        var destination = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sums = new double[4];
                for (var tap = -count; tap <= count; tap++)
                {
                    var weight = weights[tap + count];
                    // A tap stands on the pixel's middle, half a pixel in from its index.
                    var sx = x + 0.5 + dx * tap;
                    var sy = y + 0.5 + dy * tap;
                    var left = (int)Math.Floor(sx - 0.5);
                    var top = (int)Math.Floor(sy - 0.5);
                    var fx = sx - 0.5 - left;
                    var fy = sy - 0.5 - top;
                    for (var row = 0; row < 2; row++)
                    {
                        var at = top + row;
                        if ((uint)at >= (uint)height) continue;
                        var wy = row != 0 ? fy : 1 - fy;
                        for (var column = 0; column < 2; column++)
                        {
                            var side = left + column;
                            if ((uint)side >= (uint)width) continue;
                            var share = weight * wy * (column != 0 ? fx : 1 - fx);
                            for (var channel = 0; channel < 4; channel++) sums[channel] += share * source[(at * width + side) * 4 + channel];
                        }
                    }
                }
                for (var channel = 0; channel < 4; channel++)
                {
                    destination[(y * width + x) * 4 + channel] =
                        (byte)Math.Clamp((int)Math.Round(sums[channel], MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
        return destination;
    }

    /// <summary>
    /// A picture to smear: a gradient, a block with edges at both angles, and a single bright dot, whose
    /// streaks are the kernel itself. Nothing in it runs along a line, because a pattern that does is sampled
    /// differently by the two kernels — the shear takes its samples on one coherent grid along the line and
    /// the tapped kernel wobbles a tap at a time across it — and that difference is about the sampling rather
    /// than about the streak. It is noted where the shear is, in Pixels/MotionPixels.cs.
    /// </summary>
    private static byte[] Picture(int side)
    {
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var at = (y * side + x) * 4;
                var lit = x > side / 2 && y > side / 3;
                pixels[at] = (byte)(x * 255 / side);
                pixels[at + 1] = (byte)(y * 255 / side);
                pixels[at + 2] = lit ? (byte)230 : (byte)40;
                pixels[at + 3] = 255;
            }
        }
        var dot = (side / 4 * side + side / 4) * 4;
        pixels[dot] = 255;
        pixels[dot + 1] = 255;
        pixels[dot + 2] = 255;
        return pixels;
    }

    private static int Worst(byte[] left, byte[] right)
    {
        var worst = 0;
        for (var index = 0; index < left.Length; index++) worst = Math.Max(worst, Math.Abs(left[index] - right[index]));
        return worst;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(135.0)]
    [InlineData(-60.0)]
    public void TheShearedStreakIsTheTappedOne(double degrees)
    {
        var side = 64;
        var source = Picture(side);
        var radians = degrees * Math.PI / 180;
        foreach (var distance in new[] { 8.0, 40.0, 160.0 })
        {
            var sigma = distance * MotionPixels.RadiusPerPixel;
            var fast = (byte[])source.Clone();
            MotionPixels.Streak(source, fast, side, side, side * 4, sigma, radians);
            var exact = Reference(source, side, side, sigma, radians);
            var worst = Worst(fast, exact);
            // A streak shorter than the crossover is the tapped kernel itself, so it agrees to the rounding of
            // two ways of writing the same sum; past it three boxes stand in, which is the same stand-in the
            // Gaussian blur uses and differs from it by the same few levels of 255 — eight on a single-pixel
            // dot, whose peak the boxes leave a little lower and spread a little wider than the bell does.
            var allowed = sigma <= 8 ? 1 : 8;
            Assert.True(worst <= allowed, $"{degrees}° at distance {distance}: {worst} levels from the tapped streak");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(90.0)]
    [InlineData(135.0)]
    public void TheStreakRunsAlongItsAngleAndIsNoWiderAcrossIt(double degrees)
    {
        // One dot, alone: the ink spreads along the angle and stays where it was across it. A shear taken the
        // wrong way round, or a row walked down the frame instead of along it, shows up here as a streak at
        // some other angle, which is what the comparison above can hide by being small everywhere. The raster
        // is wide enough to hold the whole streak, or the clipped tails would flatter a streak that is short.
        var sigma = 20.0;
        var side = 256;
        Assert.True(side / 2 > sigma * 3, "the raster is too small to hold the streak");
        var source = new byte[side * side * 4];
        var middle = side / 2;
        var dot = (middle * side + middle) * 4;
        source[dot] = 255;
        source[dot + 1] = 255;
        source[dot + 2] = 255;
        source[dot + 3] = 255;
        var blurred = new byte[source.Length];
        MotionPixels.Streak(source, blurred, side, side, side * 4, sigma, degrees * Math.PI / 180);

        var dx = Math.Cos(degrees * Math.PI / 180);
        var dy = -Math.Sin(degrees * Math.PI / 180);
        double mass = 0, cx = 0, cy = 0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                double weight = blurred[(y * side + x) * 4 + 3];
                if (weight <= 0) continue;
                mass += weight;
                cx += weight * x;
                cy += weight * y;
            }
        }
        cx /= mass;
        cy /= mass;
        double along = 0, across = 0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                double weight = blurred[(y * side + x) * 4 + 3];
                if (weight <= 0) continue;
                var u = (x - cx) * dx + (y - cy) * dy;
                var v = -(x - cx) * dy + (y - cy) * dx;
                along += weight * u * u;
                across += weight * v * v;
            }
        }
        along = Math.Sqrt(along / mass);
        across = Math.Sqrt(across / mass);
        // A Gaussian of this spread has a standard deviation of sigma along the line and of nothing across it.
        Assert.True(Math.Abs(cx - middle) < 2 && Math.Abs(cy - middle) < 2, $"the streak moved to ({cx}, {cy})");
        Assert.True(along > sigma * 0.9 && along < sigma * 1.15, $"the streak is {along} long, not {sigma}");
        Assert.True(across < 1.5, $"the streak is {across} wide across its angle");
    }

    [Fact]
    public void AStreakTooShortToShearIsStillRunOutHonestly()
    {
        // Below a spread of one the kernel is a handful of taps and the shear's own resampling would widen the
        // streak more than it is long, so the tapped kernel is kept for it.
        var side = 32;
        var source = Picture(side);
        var sigma = 0.8;
        var blurred = (byte[])source.Clone();
        MotionPixels.Streak(source, blurred, side, side, side * 4, sigma, 0.7);
        Assert.Equal(Reference(source, side, side, sigma, 0.7), blurred);
    }
}
