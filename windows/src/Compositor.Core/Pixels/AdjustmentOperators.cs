using Compositor.Core.Format;

namespace Compositor.Core.Pixels;

/// <summary>The twelve adjustment kinds applied to a premultiplied RGBA8888 buffer.</summary>
public static class AdjustmentOperators
{
    /// <summary>
    /// Applies one adjustment in place. `strength` is the layer's opacity: 1 is the adjustment at full
    /// force, 0 leaves the pixels exactly as they were, and values between are the adjustment mixed back
    /// toward the original.
    /// </summary>
    /// <param name="originX">Where the buffer's first pixel sits in the document. Grain and noise are
    /// anchored there, so a piece of a canvas gets the same pattern as that part of the whole.</param>
    public static void Apply(LayerAdjustment adjustment, double strength,
                             Span<byte> rgba, int width, int height, int stride,
                             long originX = 0, long originY = 0)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || !(strength > 0)) return;
        if (strength >= 1)
        {
            ApplyFull(adjustment, rgba, width, height, stride, null, originX, originY);
            return;
        }
        // One copy of the buffer, serving both as the mix's source and as Motion Blur's unmodified input.
        byte[] original;
        try
        {
            original = new byte[height * stride];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        rgba[..original.Length].CopyTo(original);
        ApplyFull(adjustment, rgba, width, height, stride, original, originX, originY);
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int i = row; i < row + width * 4; i++)
            {
                double value = original[i] + (rgba[i] - original[i]) * strength;
                rgba[i] = (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0.0, 255.0);
            }
        }
    }

    private static void ApplyFull(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride,
                                  byte[]? source, long originX, long originY)
    {
        switch (adjustment.Kind)
        {
            case AdjustmentKind.HueSaturation:
                ApplyHueSaturation(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.Levels:
                ApplyLevels(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.Curves:
                ApplyCurves(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.Exposure:
                ApplyExposure(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.GradientMap:
                ApplyGradientMap(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.Grain:
                ApplyGrain(adjustment, rgba, width, height, stride, originX, originY);
                break;
            case AdjustmentKind.Invert:
                ApplyInvert(rgba, width, height, stride);
                break;
            case AdjustmentKind.BlackWhite:
                ApplyBlackWhite(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.ColorBalance:
                ApplyColorBalance(adjustment, rgba, width, height, stride);
                break;
            case AdjustmentKind.GaussianBlur:
                ApplyGaussianBlur(Clamp(adjustment.GaussianRadius, 0.1, 250), rgba, width, height, stride);
                break;
            case AdjustmentKind.MotionBlur:
                // The filter's own settings clamp: distance 1…2000, angle −90…90.
                ApplyMotionBlur(Clamp(adjustment.ResolvedMotionDistance, 1, 2000),
                                Clamp(adjustment.ResolvedMotionAngle, -90, 90), rgba, width, height, stride, source);
                break;
            case AdjustmentKind.AddNoise:
                ApplyAddNoise(adjustment, rgba, width, height, stride, originX, originY);
                break;
        }
    }

    private static double Clamp(double value, double low, double high) =>
        double.IsFinite(value) ? Math.Min(high, Math.Max(low, value)) : low;

    /// <summary>
    /// Runs a 256-entry-per-channel table over the buffer. <see cref="LevelsPixels.LevelsApply"/> walks a
    /// contiguous block, so a buffer whose rows are padded is done a row at a time.
    /// </summary>
    private static void ApplyLookup(Span<byte> rgba, int width, int height, int stride, float[] tables)
    {
        if (stride == width * 4)
        {
            LevelsPixels.LevelsApply(rgba, width * height, tables);
            return;
        }
        for (int y = 0; y < height; y++)
            LevelsPixels.LevelsApply(rgba.Slice(y * stride, width * 4), width, tables);
    }

    // ---------------------------------------------------------------- Hue/Saturation

    /// <summary>33 points per axis, the cube size the Mac build builds and applies.</summary>
    private const int CubeDimension = 33;

    private static void ApplyHueSaturation(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        float[] cube = BuildHsvCube(adjustment.ResolvedHSV);
        if (stride == width * 4)
        {
            LevelsPixels.CubeApply(rgba, width * height, cube, CubeDimension);
            return;
        }
        for (int y = 0; y < height; y++)
            LevelsPixels.CubeApply(rgba.Slice(y * stride, width * 4), width, cube, CubeDimension);
    }

    private static float[] BuildHsvCube(HueSaturationSettings settings)
    {
        double[] response = HueResponses(settings);
        var cube = new float[CubeDimension * CubeDimension * CubeDimension * 4];
        double step = CubeDimension - 1;
        int index = 0;
        for (int blue = 0; blue < CubeDimension; blue++)
        {
            for (int green = 0; green < CubeDimension; green++)
            {
                for (int red = 0; red < CubeDimension; red++)
                {
                    var (r, g, b) = Adjust(red / step, green / step, blue / step, settings, response);
                    cube[index] = (float)r;
                    cube[index + 1] = (float)g;
                    cube[index + 2] = (float)b;
                    cube[index + 3] = 1;
                    index += 4;
                }
            }
        }
        return cube;
    }

    /// <summary>
    /// Every range's shift, sampled once per degree: the cube would otherwise re-evaluate all seven ranges
    /// for each of its ~36k entries. Three values per degree — hue shift, saturation and lightness.
    /// </summary>
    private static double[] HueResponses(HueSaturationSettings settings)
    {
        var response = new double[361 * 3];
        foreach (var entry in settings.Adjustments.Entries)
        {
            if (entry.Value is not { } adjustment || adjustment.IsIdentity) continue;
            for (int degree = 0; degree <= 360; degree++)
            {
                double weight = RangeWeight(settings, entry.Key, degree);
                if (weight <= 0) continue;
                int i = degree * 3;
                response[i] += adjustment.Hue * weight;
                response[i + 1] += adjustment.Saturation * weight;
                response[i + 2] += adjustment.Lightness * weight;
            }
        }
        return response;
    }

    /// <summary>Master applies everywhere; the other ranges apply through their band, inverted for the selected one.</summary>
    private static double RangeWeight(HueSaturationSettings settings, ColorRange range, double hue)
    {
        if (range == ColorRange.Master) return 1;
        double weight = BandWeight(settings.Bands.Find(range) ?? HueBand.Default(range), hue);
        return settings.InvertRange && range == settings.Range ? 1 - weight : weight;
    }

    /// <summary>Full strength inside the range, ramping through each shoulder, nothing outside; measured forward, so it wraps.</summary>
    private static double BandWeight(HueBand band, double hue)
    {
        double span = Forward(band.FalloffStart, band.FalloffEnd);
        if (!(span > 0)) return 1;
        double position = Forward(band.FalloffStart, hue);
        if (position > span) return 0;
        double rampIn = Forward(band.FalloffStart, band.RangeStart);
        double plateauEnd = Forward(band.FalloffStart, band.RangeEnd);
        if (position < rampIn) return rampIn > 0 ? position / rampIn : 1;
        if (position <= plateauEnd) return 1;
        double rampOut = span - plateauEnd;
        return rampOut > 0 ? (span - position) / rampOut : 1;
    }

    /// <summary>Degrees from <paramref name="from"/> forward to <paramref name="to"/>, always 0…360.</summary>
    private static double Forward(double from, double to)
    {
        double delta = (to - from) % 360;
        return delta < 0 ? delta + 360 : delta;
    }

    private static (double Red, double Green, double Blue) Adjust(double red, double green, double blue,
                                                                  HueSaturationSettings settings, double[] response)
    {
        var (hue, saturation, lightness) = ToHsl(red, green, blue);
        var current = settings.Current;
        double lightnessAmount;
        if (settings.Colorize)
        {
            hue = current.Hue % 360;
            saturation = Math.Min(1, Math.Max(0, current.Saturation / 100));
            lightnessAmount = current.Lightness / 100;
        }
        else
        {
            int index = Math.Clamp((int)Math.Round(hue, MidpointRounding.AwayFromZero), 0, 360) * 3;
            lightnessAmount = response[index + 2] / 100;
            hue = (hue + response[index]) % 360;
            if (hue < 0) hue += 360;
            saturation = AdjustedSaturation(saturation, response[index + 1]);
        }
        // Lightness pulls toward white above 0 and toward black below, reaching either at ±100.
        double amount = Math.Min(1, Math.Max(-1, lightnessAmount));
        lightness = amount >= 0 ? lightness + (1 - lightness) * amount : lightness * (1 + amount);
        return ToRgb(hue, saturation, Math.Min(1, Math.Max(0, lightness)));
    }

    /// <summary>
    /// Saturation below 0 scales toward gray, above 0 divides by what is left, so +50 doubles it and +100
    /// takes any color all the way. Multiplicative both ways, so neutral grays stay neutral.
    /// </summary>
    private static double AdjustedSaturation(double saturation, double amount)
    {
        amount = Math.Min(1, Math.Max(-1, amount / 100));
        if (!(amount > 0)) return Math.Max(0, saturation * (1 + amount));
        return amount >= 1 ? (saturation > 0 ? 1 : 0) : Math.Min(1, saturation / (1 - amount));
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(double red, double green, double blue)
    {
        double high = Math.Max(red, Math.Max(green, blue));
        double low = Math.Min(red, Math.Min(green, blue));
        double lightness = (high + low) / 2;
        double delta = high - low;
        if (!(delta > 0)) return (0, 0, lightness);
        double saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (high == red) hue = (green - blue) / delta;
        else if (high == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return (hue, Math.Min(1, saturation), lightness);
    }

    private static (double Red, double Green, double Blue) ToRgb(double hue, double saturation, double lightness)
    {
        if (!(saturation > 0)) return (lightness, lightness, lightness);
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hue / 60;
        double second = chroma * (1 - Math.Abs(sector % 2 - 1));
        double baseValue = lightness - chroma / 2;
        double red, green, blue;
        switch ((int)sector)
        {
            case 0: (red, green, blue) = (chroma, second, 0); break;
            case 1: (red, green, blue) = (second, chroma, 0); break;
            case 2: (red, green, blue) = (0, chroma, second); break;
            case 3: (red, green, blue) = (0, second, chroma); break;
            case 4: (red, green, blue) = (second, 0, chroma); break;
            default: (red, green, blue) = (chroma, 0, second); break;
        }
        return (Math.Min(1, Math.Max(0, red + baseValue)),
                Math.Min(1, Math.Max(0, green + baseValue)),
                Math.Min(1, Math.Max(0, blue + baseValue)));
    }

    // ---------------------------------------------------------------- Levels, Curves, Exposure

    private static void ApplyLevels(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        var ranges = adjustment.Levels.Ranges;
        if (ranges is not { Count: 4 }) return;
        // Identity settings are returned untouched, and each channel's curve runs the composite one last.
        if (ranges.All(IsDefaultRange)) return;
        var tables = new float[768];
        var composite = ranges[0].Normalized();
        for (int channel = 1; channel <= 3; channel++)
        {
            var range = ranges[channel].Normalized();
            for (int i = 0; i < 256; i++)
                tables[(channel - 1) * 256 + i] = (float)ApplyRange(composite, ApplyRange(range, i / 255.0));
        }
        ApplyLookup(rgba, width, height, stride, tables);
    }

    private static bool IsDefaultRange(LevelsChannelRange range)
    {
        var s = range.Normalized();
        return s.Black == 0 && s.Gamma == 1 && s.White == 255 && s.OutputBlack == 0 && s.OutputWhite == 255;
    }

    private static double ApplyRange(LevelsChannelRange range, double value)
    {
        double input = Math.Min(1, Math.Max(0, (value * 255 - range.Black) / (range.White - range.Black)));
        return (range.OutputBlack + Math.Pow(input, 1 / range.Gamma) * (range.OutputWhite - range.OutputBlack)) / 255;
    }

    private static void ApplyCurves(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        var curves = adjustment.Curves;
        if (!curves.IsValid) return;
        var tables = new float[768];
        for (int channel = 1; channel <= 3; channel++)
        {
            var points = curves.Channels[channel];
            for (int i = 0; i < 256; i++)
                tables[(channel - 1) * 256 + i] = (float)(CurveValue(points, CurveValue(curves.Channels[0], i)) / 255);
        }
        ApplyLookup(rgba, width, height, stride, tables);
    }

    /// <summary>
    /// Shape-preserving cubic Hermite interpolation: the tangent at a handle is the harmonic mean of its two
    /// neighbor slopes, or zero where they disagree in sign, so the curve cannot overshoot the handles.
    /// </summary>
    private static double CurveValue(List<CurvePoint> points, double x)
    {
        int last = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].X <= x) last = i;
        }
        int segment = Math.Min(points.Count - 2, Math.Max(0, last));
        double h = points[segment + 1].X - points[segment].X;
        double t = Math.Min(1, Math.Max(0, (x - points[segment].X) / h));
        double y = (2 * t * t * t - 3 * t * t + 1) * points[segment].Y
                 + (t * t * t - 2 * t * t + t) * h * Slope(points, segment)
                 + (-2 * t * t * t + 3 * t * t) * points[segment + 1].Y
                 + (t * t * t - t * t) * h * Slope(points, segment + 1);
        return Math.Min(255, Math.Max(0, y));
    }

    private static double Slope(List<CurvePoint> points, int index)
    {
        if (index == 0) return Delta(points, 0);
        if (index == points.Count - 1) return Delta(points, points.Count - 2);
        double before = Delta(points, index - 1), after = Delta(points, index);
        if (before * after <= 0) return 0;
        return 2 / (1 / before + 1 / after);
    }

    private static double Delta(List<CurvePoint> points, int index) =>
        (points[index + 1].Y - points[index].Y) / (points[index + 1].X - points[index].X);

    private static void ApplyExposure(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        var settings = adjustment.Exposure;
        if (!settings.IsValid) return;
        var tables = new float[768];
        double scale = Math.Pow(2, settings.Exposure);
        for (int i = 0; i < 256; i++)
        {
            double encoded = i / 255.0;
            double linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * scale + settings.Offset), 1 / settings.Gamma);
            double output = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            var value = (float)Math.Min(1, Math.Max(0, output));
            tables[i] = value;
            tables[256 + i] = value;
            tables[512 + i] = value;
        }
        ApplyLookup(rgba, width, height, stride, tables);
    }

    // ---------------------------------------------------------------- Gradient Map, Grain, Invert

    private static void ApplyGradientMap(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        var settings = adjustment.GradientMap;
        if (!settings.IsValid) return;
        var first = settings.Reversed ? settings.Highlights : settings.Shadows;
        var last = settings.Reversed ? settings.Shadows : settings.Highlights;
        var table = new byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            table[i * 3] = GradientChannel(first.Red, last.Red, t);
            table[i * 3 + 1] = GradientChannel(first.Green, last.Green, t);
            table[i * 3 + 2] = GradientChannel(first.Blue, last.Blue, t);
        }
        AdjustPixels.GradientMap(rgba, width, height, stride, table);
    }

    private static byte GradientChannel(double from, double to, double t)
    {
        double value = from + (to - from) * t;
        double scaled = Math.Round(value * 255, MidpointRounding.AwayFromZero);
        return (byte)Math.Min(255, Math.Max(0, scaled));
    }

    private static void ApplyGrain(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride,
                                   long originX, long originY)
    {
        var settings = adjustment.Grain;
        if (!settings.IsValid) return;
        // One unit per pixel, anchored where this piece sits in the document.
        AdjustPixels.Grain(rgba, width, height, stride, settings.Amount, settings.Size, settings.Roughness,
                           settings.Seed, originX, originY, 1);
    }

    private static void ApplyInvert(Span<byte> rgba, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                int alpha = rgba[p + 3];
                if (alpha == 0) continue;
                // Premultiplied: each color becomes alpha − color, so transparency is kept.
                rgba[p] = (byte)Math.Max(0, alpha - rgba[p]);
                rgba[p + 1] = (byte)Math.Max(0, alpha - rgba[p + 1]);
                rgba[p + 2] = (byte)Math.Max(0, alpha - rgba[p + 2]);
            }
        }
    }

    // ---------------------------------------------------------------- Black & White, Color Balance

    private static void ApplyBlackWhite(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        var settings = adjustment.BlackWhite;
        if (!settings.IsValid) return;
        // The kernel's order: red, yellow, green, cyan, blue, magenta.
        float[] weights =
        [
            (float)(settings.Reds / 100), (float)(settings.Yellows / 100), (float)(settings.Greens / 100),
            (float)(settings.Cyans / 100), (float)(settings.Blues / 100), (float)(settings.Magentas / 100),
        ];
        AdjustPixels.BlackWhite(rgba, width, height, stride, weights, settings.Tint, settings.TintHue,
                                settings.TintSaturation / 100);
    }

    private static void ApplyColorBalance(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride)
    {
        var settings = adjustment.ColorBalance;
        if (!settings.IsValid || IsIdentity(settings)) return;
        float[] shadows = [(float)(settings.ShadowCyanRed / 100), (float)(settings.ShadowMagentaGreen / 100),
                           (float)(settings.ShadowYellowBlue / 100)];
        float[] midtones = [(float)(settings.MidCyanRed / 100), (float)(settings.MidMagentaGreen / 100),
                            (float)(settings.MidYellowBlue / 100)];
        float[] highlights = [(float)(settings.HighlightCyanRed / 100), (float)(settings.HighlightMagentaGreen / 100),
                              (float)(settings.HighlightYellowBlue / 100)];
        AdjustPixels.ColorBalance(rgba, width, height, stride, shadows, midtones, highlights, settings.PreserveLuminosity);
    }

    private static bool IsIdentity(ColorBalanceSettings settings) =>
        settings.ShadowCyanRed == 0 && settings.ShadowMagentaGreen == 0 && settings.ShadowYellowBlue == 0
        && settings.MidCyanRed == 0 && settings.MidMagentaGreen == 0 && settings.MidYellowBlue == 0
        && settings.HighlightCyanRed == 0 && settings.HighlightMagentaGreen == 0 && settings.HighlightYellowBlue == 0;

    // ---------------------------------------------------------------- Blurs

    /// <summary>
    /// Gaussian blur at <paramref name="radius"/> pixels of standard deviation, the radius Core Image takes.
    /// The Mac build pads the layer out first and blurs the padded grid, so pixels outside are transparent
    /// black rather than an edge smear; the same is done here, inside whatever buffer the caller supplies —
    /// give it <c>radius * 3 + 2</c> pixels of margin (LayerAdjustment.samplingMargin) to keep the fade off
    /// the visible area. One float plane holds a channel between the two passes.
    /// </summary>
    private static void ApplyGaussianBlur(double radius, Span<byte> rgba, int width, int height, int stride)
    {
        float sigma = (float)radius;
        float[] weights = GaussianWeights(sigma, out int half);
        float[] plane;
        float[] row;
        try
        {
            plane = new float[width * height];
            row = new float[width];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        for (int channel = 0; channel < 4; channel++)
        {
            for (int y = 0; y < height; y++)
            {
                int source = y * stride + channel;
                int target = y * width;
                for (int x = 0; x < width; x++) plane[target + x] = rgba[source + x * 4];
            }
            for (int y = 0; y < height; y++)
            {
                int line = y * width;
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    if (x >= half && x < width - half)
                    {
                        for (int k = -half; k <= half; k++) sum += weights[k + half] * plane[line + x + k];
                    }
                    else
                    {
                        for (int k = -half; k <= half; k++)
                        {
                            int sx = x + k;
                            if ((uint)sx < (uint)width) sum += weights[k + half] * plane[line + sx];
                        }
                    }
                    row[x] = sum;
                }
                row.AsSpan(0, width).CopyTo(plane.AsSpan(line, width));
            }
            // Columns in blocks: the rows the taps read stay in cache instead of being walked one column at a time.
            const int Block = 16;
            for (int x0 = 0; x0 < width; x0 += Block)
            {
                int x1 = Math.Min(width, x0 + Block);
                for (int y = 0; y < height; y++)
                {
                    bool inside = y >= half && y < height - half;
                    for (int x = x0; x < x1; x++)
                    {
                        float sum = 0;
                        if (inside)
                        {
                            int index = (y - half) * width + x;
                            for (int k = -half; k <= half; k++, index += width)
                                sum += weights[k + half] * plane[index];
                        }
                        else
                        {
                            for (int k = -half; k <= half; k++)
                            {
                                int sy = y + k;
                                if ((uint)sy < (uint)height) sum += weights[k + half] * plane[sy * width + x];
                            }
                        }
                        rgba[y * stride + x * 4 + channel] =
                            (byte)MathF.Min(255f, MathF.Max(0f, MathF.Round(sum, MidpointRounding.AwayFromZero)));
                    }
                }
            }
        }
    }

    /// <summary>
    /// A Gaussian kernel one tap per pixel, three standard deviations wide, normalized. Sampling a Gaussian
    /// kernel coarsely would fold fine detail back in — a checkerboard sits unattenuated under an even tap
    /// stride whatever the spread — so every pixel is a tap and a large radius costs its width.
    /// </summary>
    private static float[] GaussianWeights(float sigma, out int half)
    {
        half = Math.Max(1, (int)MathF.Ceiling(3 * sigma));
        var weights = new float[half * 2 + 1];
        float total = 0;
        for (int k = -half; k <= half; k++)
        {
            float weight = MathF.Exp(-(k * k) / (2 * sigma * sigma));
            weights[k + half] = weight;
            total += weight;
        }
        for (int i = 0; i < weights.Length; i++) weights[i] /= total;
        return weights;
    }

    /// <summary>
    /// Motion Blur: a line convolution along <paramref name="angle"/> degrees counterclockwise from
    /// horizontal, over <paramref name="distance"/> pixels. Core Image's radius is not the streak's length
    /// but its spread: an even streak of length d spreads d/√12, so that radius gives a Gaussian of the same
    /// spread, which is what this kernel is. Outside the buffer is transparent black, as for the Gaussian.
    /// </summary>
    private static void ApplyMotionBlur(double distance, double angle, Span<byte> rgba, int width, int height,
                                        int stride, byte[]? source)
    {
        float sigma = (float)(distance / Math.Sqrt(12.0));
        float[] weights = GaussianWeights(sigma, out int half);
        byte[] image;
        try
        {
            image = source ?? new byte[height * stride];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        if (source is null) rgba[..image.Length].CopyTo(image);
        ReadOnlySpan<byte> from = image;
        // Rows run downward, so a counterclockwise angle in Core Image's y-up space points this way here.
        double radians = angle * Math.PI / 180;
        float dx = (float)Math.Cos(radians), dy = (float)-Math.Sin(radians);
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                float red = 0, green = 0, blue = 0, alpha = 0;
                for (int k = -half; k <= half; k++)
                {
                    float weight = weights[k + half];
                    float sx = x + dx * k, sy = y + dy * k;
                    int left = (int)MathF.Floor(sx), top = (int)MathF.Floor(sy);
                    if (left >= 0 && top >= 0 && left + 1 < width && top + 1 < height)
                    {
                        // One neighborhood fetch for all four channels, the same arithmetic as Sample.
                        float fx = sx - left, fy = sy - top;
                        int p00 = top * stride + left * 4, p10 = p00 + 4;
                        int p01 = p00 + stride, p11 = p01 + 4;
                        red += weight * Blend(from[p00], from[p10], from[p01], from[p11], fx, fy);
                        green += weight * Blend(from[p00 + 1], from[p10 + 1], from[p01 + 1], from[p11 + 1], fx, fy);
                        blue += weight * Blend(from[p00 + 2], from[p10 + 2], from[p01 + 2], from[p11 + 2], fx, fy);
                        alpha += weight * Blend(from[p00 + 3], from[p10 + 3], from[p01 + 3], from[p11 + 3], fx, fy);
                        continue;
                    }
                    red += weight * Sample(from, width, height, stride, sx, sy, 0);
                    green += weight * Sample(from, width, height, stride, sx, sy, 1);
                    blue += weight * Sample(from, width, height, stride, sx, sy, 2);
                    alpha += weight * Sample(from, width, height, stride, sx, sy, 3);
                }
                int p = row + x * 4;
                rgba[p] = (byte)MathF.Min(255f, MathF.Max(0f, MathF.Round(red, MidpointRounding.AwayFromZero)));
                rgba[p + 1] = (byte)MathF.Min(255f, MathF.Max(0f, MathF.Round(green, MidpointRounding.AwayFromZero)));
                rgba[p + 2] = (byte)MathF.Min(255f, MathF.Max(0f, MathF.Round(blue, MidpointRounding.AwayFromZero)));
                rgba[p + 3] = (byte)MathF.Min(255f, MathF.Max(0f, MathF.Round(alpha, MidpointRounding.AwayFromZero)));
            }
        }
    }

    /// <summary>Bilinear sample of one channel at a fractional pixel position, zero outside the buffer.</summary>
    private static float Sample(ReadOnlySpan<byte> source, int width, int height, int stride,
                                float x, float y, int channel)
    {
        int left = (int)MathF.Floor(x), top = (int)MathF.Floor(y);
        float fx = x - left, fy = y - top;
        return Blend(Fetch(source, width, height, stride, left, top, channel),
                     Fetch(source, width, height, stride, left + 1, top, channel),
                     Fetch(source, width, height, stride, left, top + 1, channel),
                     Fetch(source, width, height, stride, left + 1, top + 1, channel), fx, fy);
    }

    private static float Blend(float topLeft, float topRight, float bottomLeft, float bottomRight, float fx, float fy)
    {
        float upper = topLeft + (topRight - topLeft) * fx;
        float lower = bottomLeft + (bottomRight - bottomLeft) * fx;
        return upper + (lower - upper) * fy;
    }

    private static float Fetch(ReadOnlySpan<byte> source, int width, int height, int stride, int x, int y, int channel)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return 0;
        return source[y * stride + x * 4 + channel];
    }

    // ---------------------------------------------------------------- Add Noise

    private static void ApplyAddNoise(LayerAdjustment adjustment, Span<byte> rgba, int width, int height, int stride,
                                      long originX, long originY)
    {
        NoisePixels.NoiseAddAt(rgba, width, height, stride,
                               (float)Clamp(adjustment.ResolvedNoiseAmount, 0.1, 400),
                               adjustment.ResolvedNoiseGaussian, adjustment.ResolvedNoiseMonochromatic,
                               adjustment.ResolvedNoiseSeed, originX, originY);
    }
}
