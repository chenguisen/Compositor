namespace Compositor.Core.Pixels;

/// <summary>Dither settings, mirroring the <c>DitherParams</c> struct in DitherPixels.h.</summary>
public sealed class DitherParams
{
    /// <summary>One of the <see cref="DitherPixels"/> style constants.</summary>
    public int Style;

    /// <summary>Tones per channel for diffusion and ordered styles, 2–8. Two is pure 1-bit.</summary>
    public int Levels;

    /// <summary>How much of each pixel's error diffusion passes on, 0–1.</summary>
    public float Diffusion;

    /// <summary>−1…1: darker (more ink) or lighter, and flatter or punchier, before dithering.</summary>
    public float Density;

    public float Contrast;

    /// <summary>Halftone and glyph cells, in pixels, and the halftone screen's angle in radians.</summary>
    public int Cell;

    public float Angle;

    /// <summary>Halftone dots, patterns and glyphs mark the light tones on the dark color instead of the dark on the light.</summary>
    public bool LightOnDark;

    /// <summary>false: the result is made of <see cref="Dark"/> and <see cref="Light"/> (straight sRGB). true: it keeps the image's own colors.</summary>
    public bool OriginalColors;

    /// <summary>Dark and light colors, straight sRGB.</summary>
    public byte[] Dark = new byte[3];

    public byte[] Light = new byte[3];

    /// <summary>Glyphs: <see cref="GlyphCount"/> coverage maps of <see cref="GlyphWidth"/> × <see cref="GlyphHeight"/> bytes
    /// (255 is fully inked), from least inked to most.</summary>
    public int GlyphWidth;

    public int GlyphHeight;

    public byte[]? Glyphs;

    /// <summary>Each glyph's mean coverage (0–1).</summary>
    public float[]? GlyphCoverage;

    public int GlyphCount;
}

/// <summary>Port of <c>Rendering/DitherPixels.c</c>.</summary>
public static class DitherPixels
{
    // The dither styles, in the order the Filter panel lists them.
    public const int Atkinson = 0;
    public const int FloydSteinberg = 1;
    public const int Bayer2 = 2;
    public const int Bayer4 = 3;
    public const int Bayer8 = 4;
    public const int Dots = 5;
    public const int Lines = 6;
    public const int Diamonds = 7;
    public const int Patterns = 8;
    public const int Glyphs = 9;

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // Density darkens (positive) or lightens as a gamma, so black and white stay put; contrast pivots on mid gray.
    private static float AdjustTone(float v, float gamma, float contrast)
    {
        v = MathF.Pow(Clamp01(v), gamma);
        return Clamp01((v - 0.5f) * contrast + 0.5f);
    }

    private readonly struct Kernel
    {
        public readonly int[] Dx;
        public readonly int[] Dy;
        public readonly int[] Weight;
        public readonly float Divisor;

        public Kernel(int[] dx, int[] dy, int[] weight, float divisor)
        {
            Dx = dx;
            Dy = dy;
            Weight = weight;
            Divisor = divisor;
        }
    }

    private static readonly Kernel AtkinsonKernel =
        new(new[] { 1, 2, -1, 0, 1, 0 }, new[] { 0, 0, 1, 1, 1, 2 }, new[] { 1, 1, 1, 1, 1, 1 }, 8f);

    private static readonly Kernel FloydKernel =
        new(new[] { 1, -1, 0, 1 }, new[] { 0, 1, 1, 1 }, new[] { 7, 3, 5, 1 }, 16f);

    // Atkinson passes on only six eighths of the error, which is what gives the Mac's crisp, contrasty look.
    private static Kernel KernelFor(int style) => style == Atkinson ? AtkinsonKernel : FloydKernel;

    private static float Quantize(float v, int levels)
    {
        float steps = levels - 1;
        return MathF.Round(Clamp01(v) * steps, MidpointRounding.AwayFromZero) / steps;
    }

    // Diffuses each plane in serpentine order, so the error's drift doesn't streak to one side.
    private static void Diffuse(Span<float> plane, ReadOnlySpan<byte> alpha, int width, int height,
                                Kernel kernel, int levels, float diffusion)
    {
        for (int y = 0; y < height; ++y)
        {
            bool reverse = (y & 1) != 0;
            for (int i = 0; i < width; ++i)
            {
                int x = reverse ? width - 1 - i : i;
                int at = y * width + x;
                if (alpha[at] == 0) continue;
                float old = plane[at], q = Quantize(old, levels);
                plane[at] = q;
                float error = (old - q) * diffusion / kernel.Divisor;
                for (int t = 0; t < kernel.Dx.Length; ++t)
                {
                    int nx = x + (reverse ? -kernel.Dx[t] : kernel.Dx[t]);
                    int ny = y + kernel.Dy[t];
                    if (nx < 0 || nx >= width || ny >= height) continue;
                    plane[ny * width + nx] += error * kernel.Weight[t];
                }
            }
        }
    }

    private static readonly byte[] Bayer8Matrix =
    {
         0, 32,  8, 40,  2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    };

    private static readonly byte[] Bayer2Matrix = { 0, 2, 3, 1 };

    private static readonly byte[] Bayer4Matrix =
    {
        0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5
    };

    // The ordered threshold for a pixel, in [0, 1). Smaller Bayer matrices are the top-left corners of the
    // 8 × 8 one, rescaled, which is how the recursive construction nests them.
    private static float OrderedThreshold(int style, int x, int y)
    {
        switch (style)
        {
            case Bayer2:
                return (Bayer2Matrix[(y & 1) * 2 + (x & 1)] + 0.5f) / 4;
            case Bayer4:
                return (Bayer4Matrix[(y & 3) * 4 + (x & 3)] + 0.5f) / 16;
            default:
                return (Bayer8Matrix[(y & 7) * 8 + (x & 7)] + 0.5f) / 64;
        }
    }

    private static float Ordered(float v, float threshold, int levels)
    {
        float steps = levels - 1;
        float q = MathF.Floor(Clamp01(v) * steps + threshold);
        return (q > steps ? steps : q) / steps;
    }

    // How much of a halftone cell a point must be covered by before it's marked, for each screen shape. `u` and `v`
    // run from −0.5 to 0.5 across the cell; the shapes grow from its middle as coverage rises.
    private static float Spot(int style, float u, float v)
    {
        float au = MathF.Abs(u), av = MathF.Abs(v);
        switch (style)
        {
            case Dots: return 3.14159265f * (u * u + v * v);
            case Lines: return av * 2;
            default: return au + av;
        }
    }

    // Old Mac fill patterns, 8 × 8, one byte per row with the leftmost pixel in the top bit, from sparsest to fullest.
    private static readonly byte[][] PatternTable =
    {
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
        new byte[] { 0x80, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 },
        new byte[] { 0x88, 0x00, 0x22, 0x00, 0x88, 0x00, 0x22, 0x00 },
        new byte[] { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 },
        new byte[] { 0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22 },
        new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00 },
        new byte[] { 0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88 },
        new byte[] { 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00 },
        new byte[] { 0x88, 0x55, 0x22, 0x55, 0x88, 0x55, 0x22, 0x55 },
        new byte[] { 0xFF, 0x80, 0x80, 0x80, 0xFF, 0x08, 0x08, 0x08 },
        new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 },
        new byte[] { 0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81 },
        new byte[] { 0x77, 0xAA, 0xDD, 0xAA, 0x77, 0xAA, 0xDD, 0xAA },
        new byte[] { 0xEE, 0xDD, 0xBB, 0x77, 0xEE, 0xDD, 0xBB, 0x77 },
        new byte[] { 0x77, 0xFF, 0xDD, 0xFF, 0x77, 0xFF, 0xDD, 0xFF },
        new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0xF7, 0xFF, 0xFF, 0xFF },
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
    };

    private static void WritePixel(Span<byte> px, int offset, float r, float g, float b)
    {
        float a = px[offset + 3] / 255.0f;
        px[offset] = (byte)MathF.Round(Clamp01(r) * a * 255.0f, MidpointRounding.AwayFromZero);
        px[offset + 1] = (byte)MathF.Round(Clamp01(g) * a * 255.0f, MidpointRounding.AwayFromZero);
        px[offset + 2] = (byte)MathF.Round(Clamp01(b) * a * 255.0f, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Dithers premultiplied RGBA pixels in place. Alpha is kept and fully transparent pixels are left alone.
    /// Returns 1, or 0 if working memory couldn't be had.
    /// </summary>
    public static int DitherApply(Span<byte> rgba, int width, int height, int stride, DitherParams p)
    {
        int count = width * height;
        if (count == 0) return 1;
        int planes = p.OriginalColors ? 3 : 1;
        float[] tone;
        byte[] alpha;
        float[]? source;
        try
        {
            tone = new float[count * planes];
            alpha = new byte[count];
            source = p.OriginalColors ? new float[count * 3] : null;
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }

        float gamma = MathF.Pow(2.0f, p.Density * 1.5f);
        float contrast = p.Contrast >= 0 ? 1.0f / (1.0f - 0.95f * p.Contrast) : 1.0f + p.Contrast;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int px = row + x * 4;
                int at = y * width + x;
                alpha[at] = rgba[px + 3];
                float r = 0, g = 0, b = 0;
                if (rgba[px + 3] != 0)
                {
                    float scale = 1.0f / rgba[px + 3];
                    r = rgba[px] * scale;
                    g = rgba[px + 1] * scale;
                    b = rgba[px + 2] * scale;
                }
                if (p.OriginalColors)
                {
                    tone[at] = AdjustTone(r, gamma, contrast);
                    tone[count + at] = AdjustTone(g, gamma, contrast);
                    tone[2 * count + at] = AdjustTone(b, gamma, contrast);
                    source![at * 3] = r;
                    source[at * 3 + 1] = g;
                    source[at * 3 + 2] = b;
                }
                else
                {
                    tone[at] = AdjustTone(0.2126f * r + 0.7152f * g + 0.0722f * b, gamma, contrast);
                }
            }
        }

        float dark0 = p.Dark[0] / 255.0f, dark1 = p.Dark[1] / 255.0f, dark2 = p.Dark[2] / 255.0f;
        float light0 = p.Light[0] / 255.0f, light1 = p.Light[1] / 255.0f, light2 = p.Light[2] / 255.0f;
        int style = p.Style;
        int levels = p.Levels < 2 ? 2 : p.Levels > 16 ? 16 : p.Levels;

        if (style <= Bayer8)
        {
            // Diffusion and ordered dithering: each plane is quantized to `levels` tones, then mapped to colors.
            if (style <= FloydSteinberg)
            {
                Kernel kernel = KernelFor(style);
                for (int c = 0; c < planes; ++c)
                    Diffuse(tone.AsSpan(c * count, count), alpha, width, height, kernel, levels, p.Diffusion);
            }
            else
            {
                for (int c = 0; c < planes; ++c)
                {
                    Span<float> plane = tone.AsSpan(c * count, count);
                    for (int y = 0; y < height; ++y)
                        for (int x = 0; x < width; ++x)
                        {
                            int at = y * width + x;
                            if (alpha[at] != 0) plane[at] = Ordered(plane[at], OrderedThreshold(style, x, y), levels);
                        }
                }
            }
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;
                    if (p.OriginalColors)
                    {
                        WritePixel(rgba, row + x * 4, tone[at], tone[count + at], tone[2 * count + at]);
                    }
                    else
                    {
                        float t = tone[at];
                        WritePixel(rgba, row + x * 4, dark0 + (light0 - dark0) * t, dark1 + (light1 - dark1) * t,
                                   dark2 + (light2 - dark2) * t);
                    }
                }
            }
        }
        else
        {
            // Marks (halftone shapes, patterns, glyphs) cover as much of each spot as the tone calls for. On light, they
            // stand for darkness and are drawn in the dark color; light on dark, the reverse.
            float[] marks;
            if (p.OriginalColors)
            {
                try
                {
                    marks = new float[count];
                }
                catch (OutOfMemoryException)
                {
                    return 0;
                }
                for (int i = 0; i < count; ++i)
                    marks[i] = 0.2126f * tone[i] + 0.7152f * tone[count + i] + 0.0722f * tone[2 * count + i];
            }
            else
            {
                marks = tone;
            }
            int cell = p.Cell < 2 ? 2 : p.Cell;
            float cosA = MathF.Cos(p.Angle), sinA = MathF.Sin(p.Angle);
            float ink0 = p.LightOnDark ? light0 : dark0, ink1 = p.LightOnDark ? light1 : dark1,
                  ink2 = p.LightOnDark ? light2 : dark2;
            float paper0 = p.LightOnDark ? dark0 : light0, paper1 = p.LightOnDark ? dark1 : light1,
                  paper2 = p.LightOnDark ? dark2 : light2;
            // Glyphs: each cell shares one, picked from the cell's average tone, worked out once per cell.
            int gw = p.GlyphWidth < 1 ? 1 : p.GlyphWidth, gh = p.GlyphHeight < 1 ? 1 : p.GlyphHeight;
            int columns = (width + gw - 1) / gw, cellRows = (height + gh - 1) / gh;
            int[]? picked = null;
            ReadOnlySpan<byte> glyphs = p.Glyphs.AsSpan();
            ReadOnlySpan<float> glyphCoverage = p.GlyphCoverage.AsSpan();
            if (style == Glyphs && p.GlyphCount > 0)
            {
                try
                {
                    picked = new int[columns * cellRows];
                }
                catch (OutOfMemoryException)
                {
                    return 0;
                }
                for (int row = 0; row < cellRows; ++row)
                    for (int column = 0; column < columns; ++column)
                    {
                        float sum = 0;
                        int n = 0;
                        for (int yy = row * gh; yy < (row + 1) * gh && yy < height; ++yy)
                            for (int xx = column * gw; xx < (column + 1) * gw && xx < width; ++xx)
                            {
                                int i = yy * width + xx;
                                if (alpha[i] != 0)
                                {
                                    sum += marks[i];
                                    ++n;
                                }
                            }
                        float t = n != 0 ? sum / n : 1;
                        float wanted = (p.LightOnDark ? t : 1 - t) * glyphCoverage[p.GlyphCount - 1];
                        int best = 0;
                        float bestDistance = 2;
                        for (int g = 0; g < p.GlyphCount; ++g)
                        {
                            float d = MathF.Abs(glyphCoverage[g] - wanted);
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                best = g;
                            }
                        }
                        picked[row * columns + column] = best;
                    }
            }
            // Original colors: marks take the pixel's own color, on black (light on dark) or white.
            float paperOriginal = p.LightOnDark ? 0.0f : 1.0f;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;
                    float amount;
                    if (picked != null)
                    {
                        int glyph = picked[y / gh * columns + x / gw];
                        amount = glyphs[glyph * gw * gh + y % gh * gw + x % gw] / 255.0f;
                    }
                    else if (style == Patterns)
                    {
                        float t = marks[at];
                        float coverage = p.LightOnDark ? t : 1 - t;
                        int index = (int)MathF.Round(coverage * (PatternTable.Length - 1), MidpointRounding.AwayFromZero);
                        amount = (PatternTable[index][y & 7] >> (7 - (x & 7))) & 1;
                    }
                    else
                    {
                        float fx = x + 0.5f, fy = y + 0.5f;
                        float u = (fx * cosA + fy * sinA) / cell, v = (-fx * sinA + fy * cosA) / cell;
                        u -= MathF.Floor(u) + 0.5f;
                        v -= MathF.Floor(v) + 0.5f;
                        float t = marks[at];
                        amount = (p.LightOnDark ? t : 1 - t) > Spot(style, u, v) ? 1 : 0;
                    }
                    if (p.OriginalColors)
                    {
                        float s0 = source![at * 3], s1 = source[at * 3 + 1], s2 = source[at * 3 + 2];
                        WritePixel(rgba, row + x * 4, paperOriginal + (s0 - paperOriginal) * amount,
                                   paperOriginal + (s1 - paperOriginal) * amount,
                                   paperOriginal + (s2 - paperOriginal) * amount);
                    }
                    else
                    {
                        WritePixel(rgba, row + x * 4, paper0 + (ink0 - paper0) * amount,
                                   paper1 + (ink1 - paper1) * amount, paper2 + (ink2 - paper2) * amount);
                    }
                }
            }
        }
        return 1;
    }

    /// <summary>
    /// Turns each <paramref name="block"/> × <paramref name="block"/> square of premultiplied RGBA pixels into
    /// a round dot in its own color on <paramref name="gap"/> (straight sRGB), like the lit pixels of a
    /// dot-matrix screen. The dot's edge is smoothed and alpha is kept.
    /// </summary>
    public static void DitherDots(Span<byte> rgba, int width, int height, int stride, int block, ReadOnlySpan<byte> gap)
    {
        if (block < 2) return;
        float radius = block * 0.42f, middle = block / 2f;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            float dy = y % block + 0.5f - middle;
            for (int x = 0; x < width; ++x)
            {
                int px = row + x * 4;
                if (rgba[px + 3] == 0) continue;
                float dx = x % block + 0.5f - middle;
                float cover = Clamp01(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f);
                if (cover >= 1) continue;
                for (int c = 0; c < 3; ++c)
                    rgba[px + c] = (byte)MathF.Round(rgba[px + c] * cover + gap[c] * rgba[px + 3] / 255.0f * (1 - cover),
                                                     MidpointRounding.AwayFromZero);
            }
        }
    }
}
