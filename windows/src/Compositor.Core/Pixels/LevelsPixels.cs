namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/LevelsPixels.c</c>: Levels, Curves and the color-lookup cube.</summary>
public static class LevelsPixels
{
    /// <summary>
    /// Applies three 256-entry channel tables to premultiplied RGBA pixels. Each channel is unpremultiplied,
    /// interpolated between table entries and premultiplied again.
    /// </summary>
    public static void LevelsApply(Span<byte> pixels, int count, ReadOnlySpan<float> tables)
    {
        for (int i = 0; i < count; ++i)
        {
            int o = i * 4;
            float alpha = pixels[o + 3];
            if (alpha == 0) continue;
            for (int channel = 0; channel < 3; ++channel)
            {
                float x = MathF.Min(255f, pixels[o + channel] * 255.0f / alpha);
                int lo = (int)x;
                int hi = lo < 255 ? lo + 1 : 255;
                ReadOnlySpan<float> table = tables.Slice(channel * 256, 256);
                float result = table[lo] + (table[hi] - table[lo]) * (x - lo);
                pixels[o + channel] = (byte)MathF.Min(alpha,
                    MathF.Max(0f, MathF.Round(result * alpha, MidpointRounding.AwayFromZero)));
            }
        }
    }

    /// <summary>
    /// Accumulates a 1024-bin histogram (a luminance block then one block per channel) of the pixels'
    /// straight colors. A pixel's weight is its alpha times <paramref name="coverage"/>, when coverage is
    /// supplied; RGB is the mean of the three channel histograms, not a luminance histogram.
    /// </summary>
    public static void LevelsHistogram(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> coverage, int count,
                                       Span<double> bins)
    {
        for (int i = 0; i < count; ++i)
        {
            int o = i * 4;
            if (pixels[o + 3] == 0) continue;
            double weight = pixels[o + 3] / 255.0 * (coverage.Length > 0 ? coverage[i] / 255.0 : 1.0);
            for (int channel = 0; channel < 3; ++channel)
            {
                int value = (int)Math.Min(255.0,
                    Math.Round(pixels[o + channel] * 255.0 / pixels[o + 3], MidpointRounding.AwayFromZero));
                bins[(channel + 1) * 256 + value] += weight;
                bins[value] += weight / 3.0;
            }
        }
    }

    /// <summary>
    /// A color lookup through <paramref name="cube"/> (<c>dimension</c>³ RGBA entries, red varying fastest),
    /// blended between the eight nearest entries, on unpremultiplied colors; alpha is kept.
    /// </summary>
    public static void CubeApply(Span<byte> pixels, int count, ReadOnlySpan<float> cube, int dimension)
    {
        // The C code indexes below zero for a one-entry cube; that is undefined there and a throw here.
        if (dimension < 2) return;
        float positionScale = (dimension - 1) / 255.0f;
        int dy = dimension, dz = dimension * dimension;
        for (int i = 0; i < count; ++i)
        {
            int o = i * 4;
            float alpha = pixels[o + 3];
            if (alpha == 0) continue;
            float p0 = MathF.Min(255f, pixels[o] * 255.0f / alpha) * positionScale;
            float p1 = MathF.Min(255f, pixels[o + 1] * 255.0f / alpha) * positionScale;
            float p2 = MathF.Min(255f, pixels[o + 2] * 255.0f / alpha) * positionScale;
            int lo0 = (int)p0, lo1 = (int)p1, lo2 = (int)p2;
            if (lo0 > dimension - 2) lo0 = dimension - 2;
            if (lo1 > dimension - 2) lo1 = dimension - 2;
            if (lo2 > dimension - 2) lo2 = dimension - 2;
            float f0 = p0 - lo0, f1 = p1 - lo1, f2 = p2 - lo2;
            int baseIndex = (lo0 + lo1 * dy + lo2 * dz) * 4;
            const int sx = 4;
            int sy = dy * 4, sz = dz * 4;
            for (int channel = 0; channel < 3; ++channel)
            {
                int c = baseIndex + channel;
                float x00 = cube[c] + (cube[c + sx] - cube[c]) * f0;
                float x10 = cube[c + sy] + (cube[c + sy + sx] - cube[c + sy]) * f0;
                float x01 = cube[c + sz] + (cube[c + sz + sx] - cube[c + sz]) * f0;
                float x11 = cube[c + sz + sy] + (cube[c + sz + sy + sx] - cube[c + sz + sy]) * f0;
                float y0 = x00 + (x10 - x00) * f1;
                float y1 = x01 + (x11 - x01) * f1;
                float result = y0 + (y1 - y0) * f2;
                pixels[o + channel] = (byte)MathF.Min(alpha,
                    MathF.Max(0f, MathF.Round(result * alpha, MidpointRounding.AwayFromZero)));
            }
        }
    }
}
