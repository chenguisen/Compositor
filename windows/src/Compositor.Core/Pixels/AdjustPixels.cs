namespace Compositor.Core.Pixels;

/// <summary>
/// Port of <c>Rendering/AdjustPixels.c</c>: the Image &gt; Adjustments kernels plus the Camera Raw
/// pipeline. Buffers are premultiplied RGBA, four bytes per pixel, unless stated otherwise.
/// </summary>
public static class AdjustPixels
{
    /// <summary>Euclidean distance, standing in for C's <c>hypot</c> over the ranges used here.</summary>
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>
    /// Gradient Map: each pixel's luminance picks a color from <paramref name="table"/> (256 × 3 straight
    /// sRGB bytes, darkest first). Alpha is kept and fully transparent pixels are left alone.
    /// </summary>
    public static void GradientMap(Span<byte> rgba, int width, int height, int stride, ReadOnlySpan<byte> table)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                uint a = rgba[p + 3];
                if (a == 0) continue;
                uint r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                if (a < 255)
                {
                    r = (r * 255u + a / 2) / a;
                    g = (g * 255u + a / 2) / a;
                    b = (b * 255u + a / 2) / a;
                    if (r > 255) r = 255;
                    if (g > 255) g = 255;
                    if (b > 255) b = 255;
                }
                uint level = (2126u * r + 7152u * g + 722u * b + 5000u) / 10000u;
                int color = (int)(level > 255 ? 255 : level) * 3;
                rgba[p] = (byte)((table[color] * a + 127u) / 255u);
                rgba[p + 1] = (byte)((table[color + 1] * a + 127u) / 255u);
                rgba[p + 2] = (byte)((table[color + 2] * a + 127u) / 255u);
            }
        }
    }

    private static uint Mix32(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }
    }

    // A value in −1…1 for an integer lattice point, fixed by the point and the seed. Two uniform halves
    // summed give a triangular spread, closer to film grain than flat noise.
    private static float Lattice(long ix, long iy, uint seed)
    {
        unchecked
        {
            uint h = Mix32((uint)ix * 0x9E3779B1U ^ Mix32((uint)iy * 0x85EBCA77U ^ seed));
            return (h & 0xFFFFU) / 65535.0f + (h >> 16) / 65535.0f - 1.0f;
        }
    }

    // Smooth seeded noise whose features follow `scale` document pixels. Keeping both the broad and
    // detailed patterns relative to the requested grain size makes Size remain visible at any Roughness.
    private static float GrainField(double u, double v, double scale, uint seed)
    {
        double cellX = Math.Floor(u / scale), cellY = Math.Floor(v / scale);
        float tx = (float)(u / scale - cellX), ty = (float)(v / scale - cellY);
        tx = tx * tx * (3.0f - 2.0f * tx);
        ty = ty * ty * (3.0f - 2.0f * ty);
        long ix = (long)cellX, iy = (long)cellY;
        float n00 = Lattice(ix, iy, seed), n10 = Lattice(ix + 1, iy, seed);
        float n01 = Lattice(ix, iy + 1, seed), n11 = Lattice(ix + 1, iy + 1, seed);
        float top = n00 + (n10 - n00) * tx, bottom = n01 + (n11 - n01) * tx;
        // Blending neighboring lattice values narrows the spread; restore approximately its original range.
        return (top + (bottom - top) * ty) * 1.6f;
    }

    private static float Clamp255(float value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>
    /// Film grain: the same brightness change on all three channels, strongest in the midtones.
    /// <paramref name="amount"/> is 0–100, <paramref name="size"/> the grain's scale in document units, and
    /// <paramref name="roughness"/> (0–100) adds smaller irregular particles whose scale remains relative to
    /// <paramref name="size"/>. Pixel (x, y) sits at (originX + (x + 0.5) × unitsPerPixel, originY +
    /// (y + 0.5) × unitsPerPixel), and its grain depends only on that position and the seed, so a piece of an
    /// image gets the same grain as that part of the whole.
    /// </summary>
    public static void Grain(Span<byte> rgba, int width, int height, int stride, double amount, double size,
                             double roughness, uint seed, double originX, double originY, double unitsPerPixel)
    {
        if (!(amount > 0) || !(unitsPerPixel > 0)) return;
        if (!(size > 0)) size = 1;
        float strength = (float)(amount > 100 ? 1.0 : amount / 100.0) * 0.35f * 255.0f;
        float rough = (float)(roughness < 0 ? 0.0 : roughness > 100 ? 1.0 : roughness / 100.0);
        uint fineSeed = Mix32(seed ^ 0xA511E9B3U);
        // Roughness adds smaller, less regular particles, as in Photoshop, but their size remains
        // proportional to the Size control instead of collapsing to fixed one-pixel noise.
        double detailSize = Math.Max(0.5, size * 0.35);
        for (int y = 0; y < height; y++)
        {
            double v = originY + (y + 0.5) * unitsPerPixel;
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                uint a = rgba[p + 3];
                if (a == 0) continue;
                double u = originX + (x + 0.5) * unitsPerPixel;
                float smooth = GrainField(u, v, size, seed);
                float fine = GrainField(u, v, detailSize, fineSeed);
                float noise = smooth + (fine - smooth) * rough;
                float unpremultiply = a == 255 ? 1.0f : 255.0f / a;
                float r = rgba[p] * unpremultiply, g = rgba[p + 1] * unpremultiply, b = rgba[p + 2] * unpremultiply;
                float level = (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255.0f;
                if (level > 1) level = 1;
                // Film grain shows most in the midtones.
                float delta = noise * strength * (0.4f + 2.4f * level * (1.0f - level));
                float coverage = a / 255.0f;
                rgba[p] = (byte)(Clamp255(r + delta) * coverage + 0.5f);
                rgba[p + 1] = (byte)(Clamp255(g + delta) * coverage + 0.5f);
                rgba[p + 2] = (byte)(Clamp255(b + delta) * coverage + 0.5f);
            }
        }
    }

    /// <summary>
    /// After resampling with a filter that rings (Lanczos), premultiplied RGBA colors can exceed their
    /// alpha; this clamps each channel back to its pixel's alpha. <paramref name="count"/> is the number
    /// of pixels.
    /// </summary>
    public static void RgbaClampPremultiplied(Span<byte> rgba, int count)
    {
        for (int i = 0; i < count; i++, rgba = rgba.Slice(4))
        {
            byte a = rgba[3];
            if (rgba[0] > a) rgba[0] = a;
            if (rgba[1] > a) rgba[1] = a;
            if (rgba[2] > a) rgba[2] = a;
        }
    }

    /// <summary>
    /// Black &amp; White, the way Photoshop's is: a color is split into the gray it contains, the secondary
    /// (cyan/magenta/yellow) between its two brightest channels, and the primary (red/green/blue) of its
    /// brightest, and each of those six ranges has its own weight. <paramref name="weights"/> is six floats
    /// in the order red, yellow, green, cyan, blue, magenta, as fractions (Photoshop's 40% is 0.4). With
    /// <paramref name="tint"/>, the result is colored at <paramref name="tintHue"/> degrees and
    /// <paramref name="tintSaturation"/> (0–1) while keeping that gray as its lightness.
    /// </summary>
    public static void BlackWhite(Span<byte> rgba, int width, int height, int stride, ReadOnlySpan<float> weights,
                                  bool tint, double tintHue, double tintSaturation)
    {
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                float alpha = rgba[p + 3];
                if (alpha == 0) continue;
                float r = rgba[p] * 255.0f / alpha, g = rgba[p + 1] * 255.0f / alpha, b = rgba[p + 2] * 255.0f / alpha;
                r = MathF.Min(255.0f, r) / 255.0f;
                g = MathF.Min(255.0f, g) / 255.0f;
                b = MathF.Min(255.0f, b) / 255.0f;
                float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
                float md = r + g + b - mx - mn;
                // weights: 0 red, 1 yellow, 2 green, 3 cyan, 4 blue, 5 magenta
                int primary, secondary;
                if (mx == r)
                {
                    primary = 0;
                    secondary = g >= b ? 1 : 5;
                }
                else if (mx == g)
                {
                    primary = 2;
                    secondary = r >= b ? 1 : 3;
                }
                else
                {
                    primary = 4;
                    secondary = g >= r ? 3 : 5;
                }
                float gray = mn + (md - mn) * weights[secondary] + (mx - md) * weights[primary];
                gray = MathF.Min(1.0f, MathF.Max(0.0f, gray));
                float outR = gray, outG = gray, outB = gray;
                if (tint && tintSaturation > 0)
                {
                    // The gray becomes the lightness of a color at the chosen hue.
                    double c = (1.0 - Math.Abs(2.0 * gray - 1.0)) * tintSaturation;
                    double hp = tintHue % 360.0 / 60.0;
                    double xx = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
                    double r1 = 0, g1 = 0, b1 = 0;
                    if (hp < 1)
                    {
                        r1 = c;
                        g1 = xx;
                    }
                    else if (hp < 2)
                    {
                        r1 = xx;
                        g1 = c;
                    }
                    else if (hp < 3)
                    {
                        g1 = c;
                        b1 = xx;
                    }
                    else if (hp < 4)
                    {
                        g1 = xx;
                        b1 = c;
                    }
                    else if (hp < 5)
                    {
                        r1 = xx;
                        b1 = c;
                    }
                    else
                    {
                        r1 = c;
                        b1 = xx;
                    }
                    double m = gray - c / 2.0;
                    outR = (float)Math.Min(1.0, Math.Max(0.0, r1 + m));
                    outG = (float)Math.Min(1.0, Math.Max(0.0, g1 + m));
                    outB = (float)Math.Min(1.0, Math.Max(0.0, b1 + m));
                }
                rgba[p] = (byte)MathF.Min(alpha,
                    MathF.Max(0.0f, MathF.Round(outR * alpha, MidpointRounding.AwayFromZero)));
                rgba[p + 1] = (byte)MathF.Min(alpha,
                    MathF.Max(0.0f, MathF.Round(outG * alpha, MidpointRounding.AwayFromZero)));
                rgba[p + 2] = (byte)MathF.Min(alpha,
                    MathF.Max(0.0f, MathF.Round(outB * alpha, MidpointRounding.AwayFromZero)));
            }
        }
    }

    /// <summary>
    /// How much a tone belongs to the shadows, midtones and highlights: three overlapping curves that sum
    /// to about one across the range, so a shift fades in and out rather than banding at a threshold.
    /// </summary>
    private static void TonalWeights(float v, out float shadow, out float mid, out float highlight)
    {
        const float a = 0.25f, b = 0.333f, scale = 0.7f;
        float s = (v - b) / -a + 0.5f;
        float h = (v + b - 1.0f) / a + 0.5f;
        s = MathF.Min(1.0f, MathF.Max(0.0f, s));
        h = MathF.Min(1.0f, MathF.Max(0.0f, h));
        float m1 = MathF.Min(1.0f, MathF.Max(0.0f, (v - b) / a + 0.5f));
        float m2 = MathF.Min(1.0f, MathF.Max(0.0f, (v + b - 1.0f) / -a + 0.5f));
        shadow = s * scale;
        mid = m1 * m2 * scale;
        highlight = h * scale;
    }

    /// <summary>
    /// Color Balance. <paramref name="shadows"/>, <paramref name="midtones"/> and
    /// <paramref name="highlights"/> are each three floats — cyan/red, magenta/green, yellow/blue — from
    /// -1 to 1 (Photoshop's -100 to 100). Each pixel is shifted by however much it belongs to each tonal
    /// range, and with <paramref name="preserveLuminosity"/> its original brightness is put back afterwards,
    /// so only the color moves.
    /// </summary>
    public static void ColorBalance(Span<byte> rgba, int width, int height, int stride, ReadOnlySpan<float> shadows,
                                    ReadOnlySpan<float> midtones, ReadOnlySpan<float> highlights,
                                    bool preserveLuminosity)
    {
        var c = new float[3];
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                float alpha = rgba[p + 3];
                if (alpha == 0) continue;
                for (int i = 0; i < 3; ++i) c[i] = MathF.Min(255.0f, rgba[p + i] * 255.0f / alpha) / 255.0f;
                float before = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
                for (int i = 0; i < 3; ++i)
                {
                    TonalWeights(c[i], out float s, out float m, out float h);
                    c[i] += shadows[i] * s + midtones[i] * m + highlights[i] * h;
                    c[i] = MathF.Min(1.0f, MathF.Max(0.0f, c[i]));
                }
                if (preserveLuminosity)
                {
                    float after = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
                    if (after > 0.0001f)
                    {
                        float ratio = before / after;
                        for (int i = 0; i < 3; ++i) c[i] = MathF.Min(1.0f, MathF.Max(0.0f, c[i] * ratio));
                    }
                }
                for (int i = 0; i < 3; ++i)
                    rgba[p + i] = (byte)MathF.Min(alpha,
                        MathF.Max(0.0f, MathF.Round(c[i] * alpha, MidpointRounding.AwayFromZero)));
            }
        }
    }

    private static double CameraClamp(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static double SrgbToLinear(double encoded)
    {
        if (encoded <= 0.04045) return encoded / 12.92;
        return Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 1;
        if (linear <= 0.0031308) return linear * 12.92;
        return 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
    }

    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    // Moves r, g, b so their Rec. 709 luminance becomes `target`, keeping the hue. Pure black cannot
    // be scaled, so a lift paints neutral light of that luminance.
    private static void ScaleLuminance(ref double r, ref double g, ref double b, double target)
    {
        target = CameraClamp(target);
        double y = Rec709(r, g, b);
        if (Math.Abs(target - y) < 1e-8) return;
        if (y < 1e-8)
        {
            if (target > y)
            {
                r = target;
                g = target;
                b = target;
            }
            return;
        }
        double scale = target / y;
        r = CameraClamp(r * scale);
        g = CameraClamp(g * scale);
        b = CameraClamp(b * scale);
    }

    private static double ToneHighlights(double y, double amount)
    {
        double t = CameraClamp((y - 0.5) / 0.5);
        double weight = t * t;
        if (amount >= 0) return CameraClamp(y + amount * weight * (1.0 - y));
        return CameraClamp(y + amount * weight * (y - 0.5));
    }

    private static double ToneShadows(double y, double amount)
    {
        double t = CameraClamp((0.5 - y) / 0.5);
        double weight = t * t;
        if (amount >= 0) return CameraClamp(y + amount * weight * (0.5 - y));
        return CameraClamp(y + amount * weight * y);
    }

    // The top quarter is the white point: +1 maps 0.875 to 1, −1 pulls everything above 0.75 down to 0.75.
    private static double ToneWhites(double y, double amount)
    {
        if (y <= 0.75) return y;
        return CameraClamp(0.75 + (y - 0.75) * (1.0 + amount));
    }

    // The bottom quarter is the black point. Negative amounts crush toward 0; positive ones lift toward 0.25.
    private static double ToneBlacks(double y, double amount)
    {
        if (y >= 0.25) return y;
        return CameraClamp(0.25 + (y - 0.25) * (1.0 - amount));
    }

    private static void VibranceAndSaturation(ref double r, ref double g, ref double b, double vibrance, double saturation)
    {
        double lum = Rec709(r, g, b);
        double maxc = Math.Max(r, Math.Max(g, b));
        double minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        double sat = maxc <= 1e-8 ? 0 : chroma / maxc;
        double hue = 0;
        if (chroma > 1e-8)
        {
            if (r >= g && r >= b) hue = 60.0 * ((g - b) / chroma % 6.0);
            else if (g >= r && g >= b) hue = 60.0 * ((b - r) / chroma + 2.0);
            else hue = 60.0 * ((r - g) / chroma + 4.0);
            if (hue < 0) hue += 360.0;
        }
        double skin = 0;
        if (hue >= 10.0 && hue <= 50.0)
        {
            skin = hue <= 30.0 ? (hue - 10.0) / 20.0 : (50.0 - hue) / 20.0;
            skin *= CameraClamp((sat - 0.15) / 0.35);
        }
        double amount = vibrance * (1.0 - sat);
        if (vibrance > 0) amount *= 1.0 - 0.7 * skin;
        double factor = 1.0 + amount;
        r = CameraClamp(lum + (r - lum) * factor);
        g = CameraClamp(lum + (g - lum) * factor);
        b = CameraClamp(lum + (b - lum) * factor);
        lum = Rec709(r, g, b);
        factor = 1.0 + saturation;
        r = CameraClamp(lum + (r - lum) * factor);
        g = CameraClamp(lum + (g - lum) * factor);
        b = CameraClamp(lum + (b - lum) * factor);
    }

    private static void WritePremultiplied(Span<byte> p, int offset, double r, double g, double b, double alpha)
    {
        p[offset] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(r * alpha, MidpointRounding.AwayFromZero)));
        p[offset + 1] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(g * alpha, MidpointRounding.AwayFromZero)));
        p[offset + 2] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(b * alpha, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Camera Raw's Light and Color groups, in this order: white balance (the three channel gains),
    /// exposure in stops of linear light, contrast about mid gray, highlights, shadows, whites, blacks,
    /// vibrance, then saturation. Temperature and tint are relative, so the gains are computed by the
    /// caller. Amounts are Camera Raw's own ranges (exposure −5…5, the rest −100…100).
    /// <paramref name="clipping"/> 0 renders the grade; 1 replaces it with a highlight-clip view (clipped
    /// channels lit on black); 2 replaces it with a shadow-clip view (clipped channels dark on white).
    /// Alpha is kept.
    /// </summary>
    public static void CameraRaw(Span<byte> rgba, int width, int height, int stride,
                                 double redGain, double greenGain, double blueGain, double exposure, double contrast,
                                 double highlights, double shadows, double whites, double blacks,
                                 double vibrance, double saturation, int clipping)
    {
        double light = Math.Pow(2.0, exposure);
        double contrastScale = 1.0 + contrast / 100.0;
        double highlightAmount = highlights / 100.0;
        double shadowAmount = shadows / 100.0;
        double whiteAmount = whites / 100.0;
        double blackAmount = blacks / 100.0;
        double vibranceAmount = vibrance / 100.0;
        double saturationAmount = saturation / 100.0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(255.0, rgba[p] * 255.0 / alpha) / 255.0;
                double g = Math.Min(255.0, rgba[p + 1] * 255.0 / alpha) / 255.0;
                double b = Math.Min(255.0, rgba[p + 2] * 255.0 / alpha) / 255.0;
                r = CameraClamp(SrgbToLinear(r) * redGain * light);
                g = CameraClamp(SrgbToLinear(g) * greenGain * light);
                b = CameraClamp(SrgbToLinear(b) * blueGain * light);
                r = CameraClamp(0.5 + (LinearToSrgb(r) - 0.5) * contrastScale);
                g = CameraClamp(0.5 + (LinearToSrgb(g) - 0.5) * contrastScale);
                b = CameraClamp(0.5 + (LinearToSrgb(b) - 0.5) * contrastScale);
                ScaleLuminance(ref r, ref g, ref b, ToneHighlights(Rec709(r, g, b), highlightAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneShadows(Rec709(r, g, b), shadowAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneWhites(Rec709(r, g, b), whiteAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneBlacks(Rec709(r, g, b), blackAmount));
                VibranceAndSaturation(ref r, ref g, ref b, vibranceAmount, saturationAmount);
                if (clipping == 1)
                {
                    bool rc = r >= 254.5 / 255.0, gc = g >= 254.5 / 255.0, bc = b >= 254.5 / 255.0;
                    r = rc ? 1 : 0;
                    g = gc ? 1 : 0;
                    b = bc ? 1 : 0;
                }
                else if (clipping == 2)
                {
                    bool rc = r <= 0.5 / 255.0, gc = g <= 0.5 / 255.0, bc = b <= 0.5 / 255.0;
                    if (rc || gc || bc)
                    {
                        r = rc ? 0 : 1;
                        g = gc ? 0 : 1;
                        b = bc ? 0 : 1;
                    }
                    else
                    {
                        r = g = b = 1;
                    }
                }
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    private static int ClampedIndex(int index, int limit)
    {
        if (index < 0) return 0;
        if (index >= limit) return limit - 1;
        return index;
    }

    /// <summary>
    /// Edge-clamped box blur. <paramref name="destination"/> may not alias <paramref name="source"/>.
    /// Returns false when the temporary row buffer cannot be allocated.
    /// </summary>
    private static bool BoxBlurPlane(ReadOnlySpan<float> src, Span<float> dst, int width, int height, int radius)
    {
        int count = width * height;
        if (radius < 1)
        {
            src.Slice(0, count).CopyTo(dst);
            return true;
        }
        float[] temp;
        try
        {
            temp = new float[count];
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
        int window = radius * 2 + 1;
        for (int y = 0; y < height; ++y)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; ++k) sum += src[y * width + ClampedIndex(k, width)];
            for (int x = 0; x < width; ++x)
            {
                temp[y * width + x] = (float)(sum / window);
                sum += src[y * width + ClampedIndex(x + radius + 1, width)];
                sum -= src[y * width + ClampedIndex(x - radius, width)];
            }
        }
        for (int x = 0; x < width; ++x)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; ++k) sum += temp[ClampedIndex(k, height) * width + x];
            for (int y = 0; y < height; ++y)
            {
                dst[y * width + x] = (float)(sum / window);
                sum += temp[ClampedIndex(y + radius + 1, height) * width + x];
                sum -= temp[ClampedIndex(y - radius, height) * width + x];
            }
        }
        return true;
    }

    private static int EffectsRadius(double @base, double scale)
    {
        double radius = @base * (scale > 0 ? scale : 1);
        if (radius < 1) radius = 1;
        if (radius > 64) radius = 64;
        return (int)Math.Round(radius, MidpointRounding.AwayFromZero);
    }

    private static void EffectsDehaze(ref double r, ref double g, ref double b, double amount)
    {
        double d = amount / 100.0;
        double y = Rec709(r, g, b);
        double contrast = 1.0 + 0.8 * d;
        double pivot = 0.45 - 0.1 * (d > 0 ? d : 0);
        double y2 = CameraClamp(pivot + (y - 0.45) * contrast);
        if (d < 0) y2 = CameraClamp(y2 + -d * (1.0 - y2) * 0.45);
        else y2 = CameraClamp(y2 - d * Math.Max(0.0, 0.4 - y2));
        ScaleLuminance(ref r, ref g, ref b, y2);
        y2 = Rec709(r, g, b);
        double sat = 1.0 + 0.7 * d;
        r = CameraClamp(y2 + (r - y2) * sat);
        g = CameraClamp(y2 + (g - y2) * sat);
        b = CameraClamp(y2 + (b - y2) * sat);
    }

    /// <summary>The vignette's strength at a point of a frame (0 at its middle, 1 past its edges).</summary>
    private static double VignetteMaskAt(double px, double py, double width, double height,
                                         double midpoint, double roundness, double feather)
    {
        double nx = px / width * 2.0 - 1.0;
        double ny = py / height * 2.0 - 1.0;
        double square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        double circle = Hypot(nx, ny) / Math.Sqrt(2.0);
        double shape = (1.0 - roundness / 100.0) * 0.5;
        double dist = circle + (square - circle) * shape;
        double start = midpoint / 100.0 * 0.85;
        double soft = feather / 100.0;
        if (soft < 0.05) soft = 0.05;
        double t = (dist - start) / soft;
        t = CameraClamp(t);
        return t * t * (3.0 - 2.0 * t);
    }

    private static double VignetteMask(int x, int y, int width, int height,
                                       double midpoint, double roundness, double feather)
        => VignetteMaskAt(x + 0.5, y + 0.5, width, height, midpoint, roundness, feather);

    private static void EffectsVignette(ref double r, ref double g, ref double b, int x, int y, int width, int height,
                                        double amount, double midpoint, double roundness, double feather,
                                        double highlights, int style)
    {
        if (amount == 0 || width == 0 || height == 0) return;
        double mask = VignetteMask(x, y, width, height, midpoint, roundness, feather);
        double effect = amount / 100.0 * mask;
        // Highlight Priority eases a darkening vignette off bright pixels. The other styles do not.
        if (effect < 0 && style == 0)
        {
            double bright = CameraClamp((Rec709(r, g, b) - 0.45) / 0.55);
            effect *= 1.0 - highlights / 100.0 * bright;
        }
        if (effect < 0)
        {
            double factor = 1.0 + effect;
            r *= factor;
            g *= factor;
            b *= factor;
        }
        else if (effect > 0)
        {
            r = r + (1.0 - r) * effect;
            g = g + (1.0 - g) * effect;
            b = b + (1.0 - b) * effect;
        }
        if (style == 1 && mask > 0)
        {
            double lum = Rec709(r, g, b);
            double sat = 1.0 - 0.75 * mask * Math.Abs(amount / 100.0);
            r = CameraClamp(lum + (r - lum) * sat);
            g = CameraClamp(lum + (g - lum) * sat);
            b = CameraClamp(lum + (b - lum) * sat);
        }
    }

    /// <summary>
    /// Standalone Vignette: blends straight sRGB toward the selected edge color using Camera Raw's falloff
    /// shape and Highlight Priority. Preserves the source alpha and premultiplied storage. The vignette is
    /// shaped to the frame (in the image's pixels). With <paramref name="fillsClear"/> it paints transparent
    /// pixels too; without, it recolors only the pixels that are there.
    /// </summary>
    public static void ColoredVignette(Span<byte> rgba, int width, int height, int stride,
                                       double frameX, double frameY, double frameWidth, double frameHeight,
                                       bool fillsClear, double amount, double midpoint, double roundness,
                                       double feather, double highlights, double red, double green, double blue)
    {
        if (amount <= 0 || width == 0 || height == 0 || frameWidth <= 0 || frameHeight <= 0) return;
        double strength = CameraClamp(amount / 100.0);
        red = CameraClamp(red);
        green = CameraClamp(green);
        blue = CameraClamp(blue);
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                if (rgba[p + 3] == 0 && !fillsClear) continue;
                double mask = VignetteMaskAt(x + 0.5 - frameX, y + 0.5 - frameY, frameWidth, frameHeight,
                                             midpoint, roundness, feather);
                if (mask <= 0) continue;
                double alpha = rgba[p + 3] / 255.0;
                double r = 0, g = 0, b = 0, bright = 0;
                if (rgba[p + 3] != 0)
                {
                    r = Math.Min(1.0, rgba[p] / (double)rgba[p + 3]);
                    g = Math.Min(1.0, rgba[p + 1] / (double)rgba[p + 3]);
                    b = Math.Min(1.0, rgba[p + 2] / (double)rgba[p + 3]);
                    bright = CameraClamp((Rec709(r, g, b) - 0.45) / 0.55);
                }
                double effect = strength * mask * (1.0 - highlights / 100.0 * bright);
                if (!fillsClear)
                {
                    // Only the pixels that are there change color; their coverage stays as it was.
                    WritePremultiplied(rgba, p, r + (red - r) * effect, g + (green - g) * effect,
                                       b + (blue - b) * effect, rgba[p + 3]);
                    continue;
                }
                // The color painted over the pixel at `effect`: an opaque pixel moves toward it, a clear one takes it on.
                double outAlpha = alpha + effect * (1.0 - alpha);
                if (outAlpha <= 0) continue;
                r = (red * effect + r * alpha * (1.0 - effect)) / outAlpha;
                g = (green * effect + g * alpha * (1.0 - effect)) / outAlpha;
                b = (blue * effect + b * alpha * (1.0 - effect)) / outAlpha;
                rgba[p + 3] = (byte)Math.Min(255.0, Math.Round(outAlpha * 255.0, MidpointRounding.AwayFromZero));
                WritePremultiplied(rgba, p, r, g, b, rgba[p + 3]);
            }
        }
    }

    /// <summary>
    /// Blue over clipped shadows and red over clipped highlights, on top of the grade. Preview only.
    /// </summary>
    public static void CameraRawClipOverlay(Span<byte> rgba, int width, int height, int stride,
                                            bool shadows, bool highlights)
    {
        if (!shadows && !highlights) return;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha);
                double g = Math.Min(1.0, rgba[p + 1] / alpha);
                double b = Math.Min(1.0, rgba[p + 2] / alpha);
                if (shadows && (r <= 0.5 / 255.0 || g <= 0.5 / 255.0 || b <= 0.5 / 255.0))
                {
                    r *= 0.35;
                    g *= 0.35;
                    b = b * 0.35 + 0.65;
                }
                if (highlights && (r >= 254.5 / 255.0 || g >= 254.5 / 255.0 || b >= 254.5 / 255.0))
                {
                    r = r * 0.35 + 0.65;
                    g *= 0.35;
                    b *= 0.35;
                }
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    /// <summary>
    /// Camera Raw Effects after Light and Color. Texture is a fine local contrast, Clarity a broader one.
    /// Dehaze raises contrast and saturation when positive and lifts the shadows when negative. Glow, its
    /// range, spread and warmth do nothing until <paramref name="glow"/> is above zero: styles are
    /// 0 diffusion, 1 bloom, 2 halation. Vignette styles are 0 highlight priority, 1 color priority,
    /// 2 paint overlay; Highlights protects bright pixels only while the amount darkens.
    /// <paramref name="scale"/> is preview pixels per layer pixel, so the radii match a full-size render.
    /// Grain is applied separately. Alpha is kept.
    /// </summary>
    public static void CameraRawEffects(Span<byte> rgba, int width, int height, int stride,
                                        double texture, double clarity, double dehaze,
                                        double glow, int glowStyle, double glowRange, double glowSpread,
                                        double glowWarmth, double vignetteAmount, double vignetteMidpoint,
                                        double vignetteRoundness, double vignetteFeather, double vignetteHighlights,
                                        int vignetteStyle, double scale)
    {
        if (width == 0 || height == 0) return;
        if (texture == 0 && clarity == 0 && dehaze == 0 && !(glow > 0) && vignetteAmount == 0) return;
        int count = width * height;
        float[]? luma = null, fine = null, coarse = null, glowPlane = null;
        bool failed = false;
        if (texture != 0 || clarity != 0 || glow > 0)
        {
            try
            {
                luma = new float[count];
            }
            catch (OutOfMemoryException)
            {
                return;
            }
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0)
                    {
                        luma[y * width + x] = 0;
                        continue;
                    }
                    double r = Math.Min(1.0, rgba[p] / alpha);
                    double g = Math.Min(1.0, rgba[p + 1] / alpha);
                    double b = Math.Min(1.0, rgba[p + 2] / alpha);
                    luma[y * width + x] = (float)Rec709(r, g, b);
                }
            }
            if (texture != 0)
            {
                try
                {
                    fine = new float[count];
                }
                catch (OutOfMemoryException)
                {
                    fine = null;
                }
                if (fine == null || !BoxBlurPlane(luma, fine, width, height, EffectsRadius(1, scale))) failed = true;
            }
            if (!failed && clarity != 0)
            {
                try
                {
                    coarse = new float[count];
                }
                catch (OutOfMemoryException)
                {
                    coarse = null;
                }
                if (coarse == null || !BoxBlurPlane(luma, coarse, width, height, EffectsRadius(4, scale))) failed = true;
            }
            if (!failed && glow > 0)
            {
                double spread = glowSpread / 100.0;
                double @base = glowStyle == 1 ? 2.0 : 5.0;
                double widened = @base * (1.0 + spread);
                if (widened < 1) widened = 1;
                int glowRadius = EffectsRadius(widened, scale);
                float threshold = (float)(0.55 + 0.4 * (glowRange / 100.0));
                try
                {
                    glowPlane = new float[count];
                }
                catch (OutOfMemoryException)
                {
                    glowPlane = null;
                }
                float[]? source = null;
                try
                {
                    source = new float[count];
                }
                catch (OutOfMemoryException)
                {
                    source = null;
                }
                if (glowPlane == null || source == null) failed = true;
                else
                {
                    float denom = 1.0f - threshold;
                    if (denom < 0.05f) denom = 0.05f;
                    for (int i = 0; i < count; ++i)
                    {
                        float t = (luma[i] - threshold) / denom;
                        if (t < 0) t = 0;
                        if (t > 1) t = 1;
                        source[i] = t;
                    }
                    failed = !BoxBlurPlane(source, glowPlane, width, height, glowRadius);
                }
            }
        }
        if (failed) return;
        double warmth = glowWarmth / 100.0;
        double glowRed, glowGreen, glowBlue, glowGain;
        if (glowStyle == 2)
        {
            // Halation's fringe is red. Warmth pushes it further that way, rather than toward yellow or blue.
            glowRed = 1;
            glowGreen = 0.35 - 0.3 * warmth;
            glowBlue = 0.2 - 0.2 * warmth;
            glowGain = 1;
        }
        else
        {
            glowRed = 0.75 + 0.25 * warmth;
            glowGreen = 0.6 + 0.2 * warmth;
            glowBlue = 0.75 - 0.6 * warmth;
            glowGain = glowStyle == 1 ? 1.4 : 1;
        }
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                int index = y * width + x;
                double r = Math.Min(1.0, rgba[p] / alpha);
                double g = Math.Min(1.0, rgba[p + 1] / alpha);
                double b = Math.Min(1.0, rgba[p + 2] / alpha);
                if (fine != null || coarse != null)
                {
                    double tone = Rec709(r, g, b);
                    double detail = 0;
                    if (fine != null) detail += texture / 100.0 * (tone - fine[index]);
                    if (coarse != null) detail += clarity / 100.0 * (tone - coarse[index]);
                    if (detail != 0) ScaleLuminance(ref r, ref g, ref b, CameraClamp(tone + detail));
                }
                if (dehaze != 0) EffectsDehaze(ref r, ref g, ref b, dehaze);
                if (glowPlane != null && glow > 0)
                {
                    double add = glowPlane[index] * (glow / 100.0) * glowGain;
                    r = CameraClamp(r + add * glowRed);
                    g = CameraClamp(g + add * glowGreen);
                    b = CameraClamp(b + add * glowBlue);
                }
                EffectsVignette(ref r, ref g, ref b, x, y, width, height, vignetteAmount, vignetteMidpoint,
                                vignetteRoundness, vignetteFeather, vignetteHighlights, vignetteStyle);
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    private static double TonalSmooth(double low, double high, double value)
    {
        double t = CameraClamp((value - low) / (high - low));
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>
    /// Local luminance contrast with independent shadow, midtone, and highlight gains.
    /// <paramref name="blurred"/> is the same premultiplied RGBA image blurred at the chosen detail radius.
    /// </summary>
    public static void TonalContrast(Span<byte> rgba, ReadOnlySpan<byte> blurred, int width, int height,
                                     int stride, int blurredStride, double amount,
                                     double shadows, double midtones, double highlights)
    {
        if (amount <= 0 || (shadows == 0 && midtones == 0 && highlights == 0)) return;
        double strength = amount / 50.0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            int baseRow = y * blurredStride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                int baseOffset = baseRow + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0 || blurred[baseOffset + 3] == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha);
                double g = Math.Min(1.0, rgba[p + 1] / alpha);
                double b = Math.Min(1.0, rgba[p + 2] / alpha);
                double lum = Rec709(r, g, b);
                double baseLum = Rec709(Math.Min(1.0, blurred[baseOffset] / (double)blurred[baseOffset + 3]),
                                        Math.Min(1.0, blurred[baseOffset + 1] / (double)blurred[baseOffset + 3]),
                                        Math.Min(1.0, blurred[baseOffset + 2] / (double)blurred[baseOffset + 3]));
                double shadowWeight = 1.0 - TonalSmooth(0.15, 0.5, baseLum);
                double highlightWeight = TonalSmooth(0.5, 0.85, baseLum);
                double midtoneWeight = 1.0 - shadowWeight - highlightWeight;
                double weight = (shadows * shadowWeight + midtones * midtoneWeight + highlights * highlightWeight) / 100.0;
                double detail = lum - baseLum;
                double delta = 0.18 * Math.Tanh(detail * 6.0) * weight * strength * (4.0 * lum * (1.0 - lum));
                WritePremultiplied(rgba, p, CameraClamp(r + delta), CameraClamp(g + delta), CameraClamp(b + delta), alpha);
            }
        }
    }

    private static double LutAt(ReadOnlySpan<float> lut, double value)
    {
        double scaled = CameraClamp(value) * 255.0;
        int lo = (int)scaled;
        int hi = lo < 255 ? lo + 1 : 255;
        double t = scaled - lo;
        return lut[lo] + (lut[hi] - lut[lo]) * t;
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        l = (maxc + minc) * 0.5;
        double d = maxc - minc;
        if (d < 1e-6)
        {
            h = 0;
            s = 0;
            return;
        }
        s = d / (1.0 - Math.Abs(2.0 * l - 1.0));
        if (maxc == r) h = (g - b) / d % 6.0;
        else if (maxc == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        h /= 6.0;
        if (h < 0) h += 1;
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 1e-6)
        {
            r = g = b = l;
            return;
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3);
    }

    private static double CircularDistance(double a, double b)
    {
        double d = Math.Abs(a - b);
        return d > 0.5 ? 1 - d : d;
    }

    private static readonly double[] MixerCenters =
    {
        0, 30.0 / 360, 60.0 / 360, 120.0 / 360, 180.0 / 360, 240.0 / 360, 270.0 / 360, 300.0 / 360
    };

    private static double PointWeight(double h, double s, double l, ReadOnlySpan<float> point)
    {
        double hueHalf = point[6] > 0.01f ? point[6] : 0.01f;
        double satHalf = point[7] > 0.01f ? point[7] : 0.01f;
        double lumHalf = point[8] > 0.01f ? point[8] : 0.01f;
        double hueW = 1 - CircularDistance(h, point[0]) / hueHalf;
        double satW = 1 - Math.Abs(s - point[1]) / satHalf;
        double lumW = 1 - Math.Abs(l - point[2]) / lumHalf;
        if (hueW < 0 || satW < 0 || lumW < 0) return 0;
        return hueW * satW * lumW;
    }

    /// <summary>
    /// Curve, Color Mixer, and Color Grading after the basic grade. <paramref name="lumaLut"/> and the
    /// channel LUTs are 256 entries. <paramref name="mixer"/> is 24 floats: hue, saturation, luminance for
    /// eight families, −1…1. Each point color is 9 floats (hue, saturation, luminance, three shifts −1…1,
    /// three range half-widths). <paramref name="grade"/> is four wheels of hue turns, saturation 0…1, and
    /// luminance −1…1. <paramref name="visualize"/> darkens pixels outside that point color.
    /// </summary>
    public static void CameraRawCurveColor(Span<byte> rgba, int width, int height, int stride,
                                           ReadOnlySpan<float> lumaLut, ReadOnlySpan<float> redLut,
                                           ReadOnlySpan<float> greenLut, ReadOnlySpan<float> blueLut,
                                           double refineSaturation, ReadOnlySpan<float> mixer, int pointCount,
                                           ReadOnlySpan<float> points, ReadOnlySpan<float> grade,
                                           double blending, double balance, int visualize)
    {
        var weights = new double[4];
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                       b = Math.Min(1.0, rgba[p + 2] / alpha);
                double tone = Rec709(r, g, b);
                double mapped = LutAt(lumaLut, tone);
                ScaleLuminance(ref r, ref g, ref b, mapped);
                if (refineSaturation != 0 && tone > 1e-4)
                {
                    double factor = 1 + refineSaturation * (mapped / tone - 1);
                    double lum = Rec709(r, g, b);
                    r = CameraClamp(lum + (r - lum) * factor);
                    g = CameraClamp(lum + (g - lum) * factor);
                    b = CameraClamp(lum + (b - lum) * factor);
                }
                r = LutAt(redLut, r);
                g = LutAt(greenLut, g);
                b = LutAt(blueLut, b);
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                double sourceHue = h, sourceSat = s, sourceLum = l;
                double hueDelta = 0, satDelta = 0, lumDelta = 0, weightSum = 0;
                for (int i = 0; i < 8; ++i)
                {
                    double dist = CircularDistance(h, MixerCenters[i]);
                    double w = 1 - dist / (40.0 / 360);
                    if (w <= 0) continue;
                    hueDelta += mixer[i] * w * (30.0 / 360);
                    satDelta += mixer[8 + i] * w;
                    lumDelta += mixer[16 + i] * w * 0.25;
                    weightSum += w;
                }
                if (weightSum > 1)
                {
                    hueDelta /= weightSum;
                    satDelta /= weightSum;
                    lumDelta /= weightSum;
                }
                h += hueDelta;
                if (h < 0) h += 1;
                if (h >= 1) h -= 1;
                s = CameraClamp(s * (1 + satDelta));
                l = CameraClamp(l + lumDelta);
                for (int i = 0; i < pointCount; ++i)
                {
                    ReadOnlySpan<float> point = points.Slice(i * 9, 9);
                    double w = PointWeight(h, s, l, point);
                    if (w <= 0) continue;
                    h += point[3] * w * (30.0 / 360);
                    s = CameraClamp(s * (1 + point[4] * w));
                    l = CameraClamp(l + point[5] * w * 0.25);
                }
                if (h < 0) h += 1;
                if (h >= 1) h -= 1;
                HslToRgb(h, s, l, out r, out g, out b);
                // Balance moves the crossover between the shadow and highlight wheels. Toward highlights
                // it has to move down, so more of the picture counts as highlight and the shadow wheel
                // loses its hold; the other sign strengthened the shadow tint it was meant to weaken.
                double split = 0.5 - balance * 0.2;
                double reach = 0.12 + blending * 0.38;
                double shadowW = CameraClamp((split + reach - Rec709(r, g, b)) / Math.Max(0.05, reach * 2));
                double highlightW = CameraClamp((Rec709(r, g, b) - (split - reach)) / Math.Max(0.05, reach * 2));
                double midW = CameraClamp(1 - Math.Abs(Rec709(r, g, b) - split) / (0.35 + reach));
                double sum = shadowW + midW + highlightW;
                if (sum > 1e-4)
                {
                    shadowW /= sum;
                    midW /= sum;
                    highlightW /= sum;
                }
                weights[0] = shadowW;
                weights[1] = midW;
                weights[2] = highlightW;
                weights[3] = 1;
                for (int wheel = 0; wheel < 4; ++wheel)
                {
                    double wh = grade[wheel * 3], ws = grade[wheel * 3 + 1], wl = grade[wheel * 3 + 2];
                    double w = weights[wheel];
                    if (w <= 0 || (ws <= 0 && wl == 0)) continue;
                    HslToRgb(wh, 1, 0.5, out double cr, out double cg, out double cb);
                    r = CameraClamp(r + (cr - 0.5) * ws * w * 0.85);
                    g = CameraClamp(g + (cg - 0.5) * ws * w * 0.85);
                    b = CameraClamp(b + (cb - 0.5) * ws * w * 0.85);
                    if (wl != 0) ScaleLuminance(ref r, ref g, ref b, CameraClamp(Rec709(r, g, b) + wl * 0.25 * w));
                }
                if (visualize >= 0 && visualize < pointCount
                    && PointWeight(sourceHue, sourceSat, sourceLum, points.Slice(visualize * 9, 9)) <= 0.05)
                {
                    r *= 0.35;
                    g *= 0.35;
                    b *= 0.35;
                }
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    private static double DetailRadius(double slider, double scale)
    {
        double @base = 0.5 + slider / 100.0 * 2.5;
        double radius = @base * (scale > 0 ? scale : 1);
        if (radius < 0.5) radius = 0.5;
        if (radius > 64) radius = 64;
        return radius;
    }

    private static double PixelHueDeg(double r, double g, double b)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        if (chroma < 1e-6) return 0;
        double hue;
        if (maxc == r) hue = (g - b) / chroma % 6.0;
        else if (maxc == g) hue = (b - r) / chroma + 2.0;
        else hue = (r - g) / chroma + 4.0;
        hue = hue * 60.0;
        if (hue < 0) hue += 360.0;
        return hue;
    }

    private static bool HueInRange(double hue, double low, double high)
    {
        if (low <= high) return hue >= low && hue <= high;
        return hue >= low || hue <= high;
    }

    private static float SharpenEdgeAt(ReadOnlySpan<float> luma, int width, int height, int x, int y, int radius)
    {
        if (radius < 1) radius = 1;
        float center = luma[y * width + x];
        float sum = 0;
        int count = 0;
        for (int dy = -radius; dy <= radius; dy += radius)
        {
            for (int dx = -radius; dx <= radius; dx += radius)
            {
                if (dx == 0 && dy == 0) continue;
                int sx = x + dx, sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                sum += MathF.Abs(luma[sy * width + sx] - center);
                count++;
            }
        }
        return count != 0 ? sum / count : 0;
    }

    /// <summary>
    /// Preview only: white where sharpening would land, black where masking protects. Uses the current
    /// sharpen sliders.
    /// </summary>
    public static void CameraRawSharpenMaskOverlay(Span<byte> rgba, int width, int height, int stride,
                                                   double sharpenRadius, double sharpenDetail, double sharpenMasking,
                                                   double scale)
    {
        if (width == 0 || height == 0) return;
        int count = width * height;
        float[] luma;
        try
        {
            luma = new float[count];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0)
                {
                    luma[y * width + x] = 0;
                    continue;
                }
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                       b = Math.Min(1.0, rgba[p + 2] / alpha);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        }
        int radius = EffectsRadius(DetailRadius(sharpenRadius, scale), 1);
        double threshold = sharpenMasking / 100.0 * 0.35;
        double detailBoost = 0.5 + sharpenDetail / 100.0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                double mask = CameraClamp((edge * detailBoost - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
                byte gray = (byte)Math.Round(mask * alpha, MidpointRounding.AwayFromZero);
                rgba[p] = gray;
                rgba[p + 1] = gray;
                rgba[p + 2] = gray;
            }
        }
    }

    /// <summary>
    /// Manual noise reduction, then sharpening. <paramref name="scale"/> maps radius to preview pixels.
    /// Applied after the creative grade.
    /// </summary>
    public static void CameraRawDetail(Span<byte> rgba, int width, int height, int stride,
                                       double sharpenAmount, double sharpenRadius, double sharpenDetail,
                                       double sharpenMasking, double noiseLuminance, double noiseLuminanceDetail,
                                       double noiseLuminanceContrast, double noiseColor, double noiseColorDetail,
                                       double noiseColorSmoothness, double scale)
    {
        if (width == 0 || height == 0) return;
        if (sharpenAmount == 0 && noiseLuminance == 0 && noiseColor == 0) return;
        int count = width * height;
        float[] luma;
        float[] work;
        try
        {
            luma = new float[count];
            work = new float[count];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0)
                {
                    luma[y * width + x] = 0;
                    continue;
                }
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                       b = Math.Min(1.0, rgba[p + 2] / alpha);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        }
        if (noiseLuminance > 0)
        {
            int radius = EffectsRadius(1.0 + noiseLuminance / 50.0, scale);
            if (!BoxBlurPlane(luma, work, width, height, radius)) return;
            double strength = noiseLuminance / 100.0;
            double preserve = noiseLuminanceDetail / 100.0;
            double contrast = noiseLuminanceContrast / 100.0;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    int index = y * width + x;
                    float edge = SharpenEdgeAt(luma, width, height, x, y, 1);
                    double local = strength * (1.0 - preserve * Math.Min(1.0, edge * 6.0));
                    float blurred = work[index];
                    float target = (float)(luma[index] * (1.0 - local) + blurred * local);
                    if (contrast != 0) target = (float)(target + contrast * 0.25 * (luma[index] - blurred));
                    luma[index] = target;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                           b = Math.Min(1.0, rgba[p + 2] / alpha);
                    ScaleLuminance(ref r, ref g, ref b, target);
                    WritePremultiplied(rgba, p, r, g, b, alpha);
                }
            }
        }
        if (noiseColor > 0)
        {
            int radius = EffectsRadius(1.0 + noiseColorSmoothness / 40.0, scale);
            float[] chroma;
            float[] chromaBlur;
            try
            {
                chroma = new float[count];
                chromaBlur = new float[count];
            }
            catch (OutOfMemoryException)
            {
                return;
            }
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                           b = Math.Min(1.0, rgba[p + 2] / alpha);
                    RgbToHsl(r, g, b, out _, out double s, out _);
                    chroma[y * width + x] = (float)s;
                }
            }
            if (!BoxBlurPlane(chroma, chromaBlur, width, height, radius)) return;
            double strength = noiseColor / 100.0;
            double preserve = noiseColorDetail / 100.0;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    int index = y * width + x;
                    float edge = MathF.Abs(chroma[index] - chromaBlur[index]);
                    double local = strength * (1.0 - preserve * Math.Min(1.0, edge * 4.0));
                    float sat = chroma[index] * (float)(1.0 - local) + chromaBlur[index] * (float)local;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                           b = Math.Min(1.0, rgba[p + 2] / alpha);
                    RgbToHsl(r, g, b, out double h, out double s, out double l);
                    s = sat;
                    HslToRgb(h, s, l, out r, out g, out b);
                    WritePremultiplied(rgba, p, r, g, b, alpha);
                }
            }
        }
        if (sharpenAmount > 0)
        {
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                           b = Math.Min(1.0, rgba[p + 2] / alpha);
                    luma[y * width + x] = (float)Rec709(r, g, b);
                }
            }
            int radius = EffectsRadius(DetailRadius(sharpenRadius, scale), 1);
            if (!BoxBlurPlane(luma, work, width, height, radius)) return;
            double amount = sharpenAmount / 100.0;
            double detailMix = sharpenDetail / 100.0;
            double threshold = sharpenMasking / 100.0 * 0.35;
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                for (int x = 0; x < width; ++x)
                {
                    int p = row + x * 4;
                    double alpha = rgba[p + 3];
                    if (alpha == 0) continue;
                    int index = y * width + x;
                    float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                    double mask = CameraClamp((edge * (0.5 + detailMix) - threshold)
                                              / Math.Max(0.04, 0.35 - threshold * 0.5));
                    double high = luma[index] - work[index];
                    double sharpened = CameraClamp(luma[index] + high * amount * mask * (0.5 + detailMix));
                    double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                           b = Math.Min(1.0, rgba[p + 2] / alpha);
                    ScaleLuminance(ref r, ref g, ref b, sharpened);
                    WritePremultiplied(rgba, p, r, g, b, alpha);
                }
            }
        }
    }

    private static void OpticsDefringe(ref double r, ref double g, ref double b, double purpleAmount,
                                       double purpleLow, double purpleHigh, double greenAmount,
                                       double greenLow, double greenHigh)
    {
        double hue = PixelHueDeg(r, g, b);
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        if (chroma < 1e-6) return;
        double sat = chroma / maxc;
        double reduce = 0;
        if (purpleAmount > 0 && HueInRange(hue, purpleLow, purpleHigh)) reduce = Math.Max(reduce, purpleAmount / 100.0);
        if (greenAmount > 0 && HueInRange(hue, greenLow, greenHigh)) reduce = Math.Max(reduce, greenAmount / 100.0);
        if (reduce <= 0) return;
        double lum = Rec709(r, g, b);
        double factor = 1.0 - reduce * sat;
        r = CameraClamp(lum + (r - lum) * factor);
        g = CameraClamp(lum + (g - lum) * factor);
        b = CameraClamp(lum + (b - lum) * factor);
    }

    private static void OpticsChromatic(Span<byte> rgba, int width, int height, int stride, double strength)
    {
        if (strength <= 0) return;
        byte[] copy;
        try
        {
            copy = new byte[height * stride];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        for (int y = 0; y < height; ++y)
            rgba.Slice(y * stride, width * 4).CopyTo(copy.AsSpan(y * stride, width * 4));
        double cx = width * 0.5, cy = height * 0.5;
        double maxR = Hypot(cx, cy);
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            int srcRow = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double radial = Hypot(dx, dy) / maxR;
                double shift = strength * radial * radial * 2.5;
                int rx = (int)Math.Round(x - shift, MidpointRounding.AwayFromZero);
                int bx = (int)Math.Round(x + shift, MidpointRounding.AwayFromZero);
                int pr = srcRow + ClampedIndex(rx, width) * 4;
                int pb = srcRow + ClampedIndex(bx, width) * 4;
                double g = Math.Min(1.0, copy[srcRow + x * 4 + 1] / alpha);
                double r = Math.Min(1.0, copy[pr] / Math.Max(1.0, copy[pr + 3]));
                double b = Math.Min(1.0, copy[pb + 2] / Math.Max(1.0, copy[pb + 3]));
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    private static void OpticsVignetteCorrect(ref double r, ref double g, ref double b, int x, int y,
                                              int width, int height, double amount, double midpoint)
    {
        if (amount == 0 || width == 0 || height == 0) return;
        double nx = (x + 0.5) / width * 2.0 - 1.0;
        double ny = (y + 0.5) / height * 2.0 - 1.0;
        double dist = Hypot(nx, ny) / Math.Sqrt(2.0);
        double start = midpoint / 100.0 * 0.85;
        double t = CameraClamp((dist - start) / 0.35);
        double mask = t * t * (3.0 - 2.0 * t);
        double lift = amount / 100.0 * mask;
        if (lift > 0)
        {
            r = CameraClamp(r + (1.0 - r) * lift);
            g = CameraClamp(g + (1.0 - g) * lift);
            b = CameraClamp(b + (1.0 - b) * lift);
        }
        else
        {
            double factor = 1.0 + lift;
            r *= factor;
            g *= factor;
            b *= factor;
        }
    }

    /// <summary>
    /// Chromatic aberration, lens distortion, defringe, and lens-vignetting correction.
    /// <paramref name="distortionK"/> matches <see cref="LensPixels.LensDistort"/>.
    /// </summary>
    public static void CameraRawOptics(Span<byte> rgba, int width, int height, int stride,
                                       bool removeChromatic, int lensProfile, double profileDistortion,
                                       double profileVignetting, double distortionK, double purpleAmount,
                                       double purpleHueLow, double purpleHueHigh, double greenAmount,
                                       double greenHueLow, double greenHueHigh, double vignetteAmount,
                                       double vignetteMidpoint, double scale)
    {
        if (width == 0 || height == 0) return;
        double profileVignette = lensProfile != 0 ? profileVignetting / 100.0 : 0;
        double vignette = vignetteAmount + profileVignette * 35.0;
        if (distortionK != 0)
        {
            int bytes = height * stride;
            byte[] copy;
            try
            {
                copy = new byte[bytes];
            }
            catch (OutOfMemoryException)
            {
                return;
            }
            rgba.Slice(0, bytes).CopyTo(copy);
            LensPixels.LensDistort(copy, rgba, width, height, stride, distortionK);
        }
        if (removeChromatic) OpticsChromatic(rgba, width, height, stride, 0.45);
        if (purpleAmount == 0 && greenAmount == 0 && vignette == 0) return;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                       b = Math.Min(1.0, rgba[p + 2] / alpha);
                OpticsDefringe(ref r, ref g, ref b, purpleAmount, purpleHueLow, purpleHueHigh,
                               greenAmount, greenHueLow, greenHueHigh);
                OpticsVignetteCorrect(ref r, ref g, ref b, x, y, width, height, vignette, vignetteMidpoint);
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }

    /// <summary>
    /// Camera calibration before the main grade. Primary hue and saturation shifts are −100…100; shadow
    /// tint is green/magenta.
    /// </summary>
    public static void CameraRawCalibration(Span<byte> rgba, int width, int height, int stride,
                                            double shadowTint, double redHue, double redSaturation,
                                            double greenHue, double greenSaturation, double blueHue,
                                            double blueSaturation, int processVersion)
    {
        if (width == 0 || height == 0) return;
        double versionScale = processVersion <= 1 ? 0.55 : processVersion == 2 ? 0.65 : processVersion == 3 ? 0.75
            : processVersion == 4 ? 0.85 : processVersion == 5 ? 0.92 : 1.0;
        double tint = shadowTint / 100.0 * versionScale;
        double rh = redHue / 100.0 * (15.0 / 360.0) * versionScale;
        double rs = redSaturation / 100.0 * 0.45 * versionScale;
        double gh = greenHue / 100.0 * (15.0 / 360.0) * versionScale;
        double gs = greenSaturation / 100.0 * 0.45 * versionScale;
        double bh = blueHue / 100.0 * (15.0 / 360.0) * versionScale;
        double bs = blueSaturation / 100.0 * 0.45 * versionScale;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int p = row + x * 4;
                double alpha = rgba[p + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, rgba[p] / alpha), g = Math.Min(1.0, rgba[p + 1] / alpha),
                       b = Math.Min(1.0, rgba[p + 2] / alpha);
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                if (l < 0.35 && tint != 0)
                {
                    h += tint * 0.06;
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
                if (maxc - minc > 1e-5)
                {
                    if (r >= g && r >= b)
                    {
                        h += rh;
                        s = CameraClamp(s * (1 + rs));
                    }
                    else if (g >= r && g >= b)
                    {
                        h += gh;
                        s = CameraClamp(s * (1 + gs));
                    }
                    else
                    {
                        h += bh;
                        s = CameraClamp(s * (1 + bs));
                    }
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                HslToRgb(h, s, l, out r, out g, out b);
                WritePremultiplied(rgba, p, r, g, b, alpha);
            }
        }
    }
}
