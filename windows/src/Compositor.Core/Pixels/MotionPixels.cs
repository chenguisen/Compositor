namespace Compositor.Core.Pixels;

/// <summary>Port of the Mac build's motion blur, which Core Image runs as <c>CIMotionBlur</c>.</summary>
public static class MotionPixels
{
    /// <summary>
    /// The radius, in pixels, that Core Image gives a streak of length <paramref name="distance"/>: its
    /// taper is a Gaussian whose spread is about its radius, and an even streak of length d spreads d / √12,
    /// so this radius is made to match that spread.
    /// </summary>
    public static double RadiusPerPixel => 1 / Math.Sqrt(12);

    /// <summary>
    /// The most taps one pixel is sampled with. A wide streak is sampled every few pixels instead: at three
    /// standard deviations out the tap spacing is still a small fraction of the spread, so the smear looks
    /// the same, and a long streak over a large layer stays something one can wait for.
    /// </summary>
    private const int MostTaps = 512;

    /// <summary>
    /// Streaks premultiplied RGBA along an angle, <paramref name="radians"/> counterclockwise from
    /// horizontal as Photoshop measures it — which, in a grid whose rows run downwards, points along
    /// (cos, −sin). Each pixel is a Gaussian-weighted sum of the pixels along that line, spreading with
    /// <paramref name="sigma"/>; anything outside the source is nothing at all. Alpha is streaked too, so a
    /// cut-out edge smears the way the colour does.
    /// </summary>
    public static void Streak(ReadOnlySpan<byte> source, Span<byte> destination,
                              int width, int height, int stride, double sigma, double radians)
    {
        if (sigma <= 0 || width <= 0 || height <= 0 || source.Length < destination.Length) return;
        var dx = Math.Cos(radians);
        var dy = -Math.Sin(radians);
        var half = sigma * 3;
        var step = Math.Max(1.0, Math.Ceiling(half / MostTaps));
        var count = (int)Math.Ceiling(half / step);
        var weights = new float[count * 2 + 1];
        double total = 0;
        for (var tap = -count; tap <= count; tap++)
        {
            var weight = Math.Exp(-(tap * step) * (tap * step) / (2 * sigma * sigma));
            weights[tap + count] = (float)weight;
            total += weight;
        }
        for (var tap = 0; tap < weights.Length; tap++) weights[tap] = (float)(weights[tap] / total);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var tap = -count; tap <= count; tap++)
                {
                    var offset = tap * step;
                    var sum = weights[tap + count];
                    if (sum == 0) continue;
                    var sample = Bilinear(source, stride, width, height, x + dx * offset, y + dy * offset);
                    r += sum * sample.Red;
                    g += sum * sample.Green;
                    b += sum * sample.Blue;
                    a += sum * sample.Alpha;
                }
                var at = y * stride + x * 4;
                destination[at] = Clamp(r);
                destination[at + 1] = Clamp(g);
                destination[at + 2] = Clamp(b);
                destination[at + 3] = Clamp(a);
            }
        }
    }

    private static (double Red, double Green, double Blue, double Alpha) Bilinear(
        ReadOnlySpan<byte> source, int stride, int width, int height, double x, double y)
    {
        var x0 = (int)Math.Floor(x - 0.5);
        var y0 = (int)Math.Floor(y - 0.5);
        var fx = x - 0.5 - x0;
        var fy = y - 0.5 - y0;
        double r = 0, g = 0, b = 0, a = 0;
        for (var j = 0; j < 2; j++)
        {
            var row = y0 + j;
            if (row < 0 || row >= height) continue;
            var wy = j != 0 ? fy : 1 - fy;
            if (wy == 0) continue;
            for (var i = 0; i < 2; i++)
            {
                var column = x0 + i;
                if (column < 0 || column >= width) continue;
                var weight = wy * (i != 0 ? fx : 1 - fx);
                if (weight == 0) continue;
                var at = row * stride + column * 4;
                r += weight * source[at];
                g += weight * source[at + 1];
                b += weight * source[at + 2];
                a += weight * source[at + 3];
            }
        }
        return (r, g, b, a);
    }

    private static byte Clamp(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
