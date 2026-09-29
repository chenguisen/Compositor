using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>A straight sRGB color stored with an adjustment, 0–1 per channel.</summary>
public sealed class AdjustmentColor
{
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }

    public static AdjustmentColor From(double red, double green, double blue) => new() { Red = red, Green = green, Blue = blue };

    public bool IsValid => Color.IsValid(Red, Green, Blue);
}

/// <summary>Shared range rules for the adjustment settings.</summary>
internal static class Color
{
    public static bool IsValid(double red, double green, double blue) =>
        IsUnit(red) && IsUnit(green) && IsUnit(blue);

    public static bool IsUnit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>One color range's shift in Photoshop's Hue/Saturation.</summary>
public sealed class RangeAdjustment
{
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Lightness { get; set; }

    public bool IsIdentity => Hue == 0 && Saturation == 0 && Lightness == 0;
}

/// <summary>A hue band in degrees, wrapping at 360.</summary>
public sealed class HueBand
{
    public double FalloffStart { get; set; }
    public double RangeStart { get; set; }
    public double RangeEnd { get; set; }
    public double FalloffEnd { get; set; }

    public double[] Handles => [FalloffStart, RangeStart, RangeEnd, FalloffEnd];

    public static HueBand Default(ColorRange range) => range switch
    {
        ColorRange.Master => new HueBand { FalloffStart = 0, RangeStart = 0, RangeEnd = 360, FalloffEnd = 360 },
        ColorRange.Reds => new HueBand { FalloffStart = 315, RangeStart = 345, RangeEnd = 15, FalloffEnd = 45 },
        ColorRange.Yellows => new HueBand { FalloffStart = 15, RangeStart = 45, RangeEnd = 75, FalloffEnd = 105 },
        ColorRange.Greens => new HueBand { FalloffStart = 75, RangeStart = 105, RangeEnd = 135, FalloffEnd = 165 },
        ColorRange.Cyans => new HueBand { FalloffStart = 135, RangeStart = 165, RangeEnd = 195, FalloffEnd = 225 },
        ColorRange.Blues => new HueBand { FalloffStart = 195, RangeStart = 225, RangeEnd = 255, FalloffEnd = 285 },
        ColorRange.Magentas => new HueBand { FalloffStart = 255, RangeStart = 285, RangeEnd = 315, FalloffEnd = 345 },
        _ => new HueBand(),
    };

    public static List<ColorRange> Ranges { get; } =
        [ColorRange.Master, ColorRange.Reds, ColorRange.Yellows, ColorRange.Greens, ColorRange.Cyans, ColorRange.Blues, ColorRange.Magentas];
}

/// <summary>
/// Hue is −180…180 (0…360 when colorizing), Saturation −100…100, Lightness −100…100. Each color range
/// keeps its own values; Master applies everywhere.
///
/// Both dictionaries are `[ColorRange: Value]`, which Swift encodes as an array of alternating keys and
/// values: its ColorRange does not conform to CodingKeyRepresentable, so the keyed-container path is not
/// taken. See <see cref="EnumKeyedValues{TKey,TValue}"/>.
/// </summary>
public sealed class HueSaturationSettings
{
    public ColorRange Range { get; set; } = ColorRange.Master;
    public bool Colorize { get; set; }
    public bool InvertRange { get; set; }

    [JsonConverter(typeof(EnumKeyedValuesConverter<ColorRange, RangeAdjustment>))]
    public EnumKeyedValues<ColorRange, RangeAdjustment> Adjustments { get; set; } = new();

    [JsonConverter(typeof(EnumKeyedValuesConverter<ColorRange, HueBand>))]
    public EnumKeyedValues<ColorRange, HueBand> Bands { get; set; } = DefaultBands();

    /// <summary>Every range's band, as Photoshop's starting point, which the Swift initializer fills in.</summary>
    public static EnumKeyedValues<ColorRange, HueBand> DefaultBands()
    {
        var bands = new EnumKeyedValues<ColorRange, HueBand>();
        foreach (var range in HueBand.Ranges) bands.Set(range, HueBand.Default(range));
        return bands;
    }

    /// <summary>What the sliders read: the selected range's values.</summary>
    public RangeAdjustment Current => Adjustments.Find(Range) ?? new RangeAdjustment();

    public HueBand Band => Bands.Find(Range) ?? HueBand.Default(Range);

    /// <summary>The settings an adjustment carries before `hsvSettings` existed, or after it is dropped.</summary>
    public static HueSaturationSettings FromLegacy(double hue, double saturation, double lightness, bool colorize)
    {
        var settings = new HueSaturationSettings { Colorize = colorize };
        settings.Set(hue, saturation, lightness);
        return settings;
    }

    public void Set(double hue, double saturation, double lightness) =>
        Adjustments.Set(Range, new RangeAdjustment { Hue = hue, Saturation = saturation, Lightness = lightness });
}

public sealed class LevelsChannelRange
{
    public double Black { get; set; }
    public double Gamma { get; set; } = 1;
    public double White { get; set; } = 255;
    public double OutputBlack { get; set; }
    public double OutputWhite { get; set; } = 255;

    /// <summary>Keeps every value inside its allowed range; a non-finite value falls back to the default.</summary>
    public LevelsChannelRange Normalized()
    {
        var black = Clamp(Black, 0, 254, 0);
        var white = Clamp(White, black + 1, 255, 255);
        return new LevelsChannelRange
        {
            Black = black,
            White = white,
            Gamma = Clamp(Gamma, 0.1, 9.99, 1),
            OutputBlack = Clamp(OutputBlack, 0, 255, 0),
            OutputWhite = Clamp(OutputWhite, 0, 255, 255),
        };
    }

    public bool IsNormalized { get { var n = Normalized(); return Equals(n); } }

    private static double Clamp(double value, double low, double high, double fallback) =>
        double.IsFinite(value) ? Math.Min(high, Math.Max(low, value)) : fallback;

    public override bool Equals(object? other) => other is LevelsChannelRange range
        && Black == range.Black && Gamma == range.Gamma && White == range.White
        && OutputBlack == range.OutputBlack && OutputWhite == range.OutputWhite;

    public override int GetHashCode() => HashCode.Combine(Black, Gamma, White, OutputBlack, OutputWhite);
}

public sealed class LevelsSettings
{
    public LevelsChannel Channel { get; set; } = LevelsChannel.Rgb;
    public List<LevelsChannelRange> Ranges { get; set; } = DefaultRanges();

    public static List<LevelsChannelRange> DefaultRanges() =>
        [new(), new(), new(), new()];
}

public sealed class CurvePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class CurvesSettings
{
    public LevelsChannel Channel { get; set; } = LevelsChannel.Rgb;
    public List<List<CurvePoint>> Channels { get; set; } = DefaultChannels();

    public static List<List<CurvePoint>> DefaultChannels() =>
        Enumerable.Range(0, 4).Select(_ => new List<CurvePoint> { new() { X = 0, Y = 0 }, new() { X = 255, Y = 255 } }).ToList();

    public bool IsValid =>
        Channels is { Count: 4 } && Channels.All(points =>
            points is { Count: >= 2 and <= 32 } && points[0].X == 0 && points[^1].X == 255
            && points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)
                && point.X is >= 0 and <= 255 && point.Y is >= 0 and <= 255)
            && points.Zip(points.Skip(1)).All(pair => pair.First.X < pair.Second.X));
}

/// <summary>Photoshop's Exposure: stops scale linear light, offset shifts it, gamma bends the result.</summary>
public sealed class ExposureSettings
{
    public double Exposure { get; set; }
    public double Offset { get; set; }
    public double Gamma { get; set; } = 1;

    public bool IsValid => Exposure is >= -20 and <= 20 && Offset is >= -0.5 and <= 0.5 && Gamma is >= 0.01 and <= 9.99;
}

public sealed class GradientMapSettings
{
    public AdjustmentColor Shadows { get; set; } = AdjustmentColor.From(0, 0, 0);
    public AdjustmentColor Highlights { get; set; } = AdjustmentColor.From(1, 1, 1);
    public bool Reversed { get; set; }

    public bool IsValid => Shadows.IsValid && Highlights.IsValid;
}

/// <summary>Film grain: brightness noise, strongest in the midtones, fixed in document space by `seed`.</summary>
public sealed class GrainSettings
{
    public double Amount { get; set; } = 25;
    public double Size { get; set; } = 1.5;
    public double Roughness { get; set; } = 50;
    public uint Seed { get; set; }

    public bool IsValid => Amount is >= 0 and <= 100 && Size is >= 0.5 and <= 20 && Roughness is >= 0 and <= 100;
}

public sealed class BlackWhiteSettings
{
    public double Reds { get; set; } = 40;
    public double Yellows { get; set; } = 60;
    public double Greens { get; set; } = 40;
    public double Cyans { get; set; } = 60;
    public double Blues { get; set; } = 20;
    public double Magentas { get; set; } = 80;
    public bool Tint { get; set; }
    public double TintHue { get; set; } = 40;
    public double TintSaturation { get; set; } = 20;

    public bool IsValid =>
        new[] { Reds, Yellows, Greens, Cyans, Blues, Magentas }.All(value => double.IsFinite(value) && value is >= -200 and <= 300)
        && double.IsFinite(TintHue) && TintHue is >= 0 and <= 360
        && double.IsFinite(TintSaturation) && TintSaturation is >= 0 and <= 100;
}

public sealed class ColorBalanceSettings
{
    public double ShadowCyanRed { get; set; }
    public double ShadowMagentaGreen { get; set; }
    public double ShadowYellowBlue { get; set; }
    public double MidCyanRed { get; set; }
    public double MidMagentaGreen { get; set; }
    public double MidYellowBlue { get; set; }
    public double HighlightCyanRed { get; set; }
    public double HighlightMagentaGreen { get; set; }
    public double HighlightYellowBlue { get; set; }
    public bool PreserveLuminosity { get; set; } = true;

    public bool IsValid => new[]
    {
        ShadowCyanRed, ShadowMagentaGreen, ShadowYellowBlue,
        MidCyanRed, MidMagentaGreen, MidYellowBlue,
        HighlightCyanRed, HighlightMagentaGreen, HighlightYellowBlue,
    }.All(value => double.IsFinite(value) && value is >= -100 and <= 100);
}

/// <summary>
/// An adjustment layer's settings. Every kind's settings are carried, each optional and defaulting to an
/// identity adjustment, so changing kind never loses what another kind was set to.
/// </summary>
public sealed class LayerAdjustment
{
    public AdjustmentKind Kind { get; set; }
    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Lightness { get; set; }
    public bool Colorize { get; set; }

    /// <summary>Absent on projects saved before range-aware Hue/Saturation adjustments.</summary>
    public HueSaturationSettings? HsvSettings { get; set; }

    public LevelsSettings Levels { get; set; } = new();
    public CurvesSettings Curves { get; set; } = new();

    public ExposureSettings? ExposureSettings { get; set; }
    public GradientMapSettings? GradientMapSettings { get; set; }
    public GrainSettings? GrainSettings { get; set; }
    public BlackWhiteSettings? BlackWhiteSettings { get; set; }
    public ColorBalanceSettings? ColorBalanceSettings { get; set; }

    public double? BlurRadius { get; set; }
    public double? MotionAngle { get; set; }
    public double? MotionDistance { get; set; }
    public double? NoiseAmount { get; set; }
    public bool? NoiseGaussian { get; set; }
    public bool? NoiseMonochromatic { get; set; }
    public uint? NoiseSeed { get; set; }

    public HueSaturationSettings ResolvedHSV =>
        HsvSettings ?? HueSaturationSettings.FromLegacy(Hue, Saturation, Lightness, Colorize);

    public ExposureSettings Exposure => ExposureSettings ?? new ExposureSettings();
    public GradientMapSettings GradientMap => GradientMapSettings ?? new GradientMapSettings();
    public GrainSettings Grain => GrainSettings ?? new GrainSettings();
    public BlackWhiteSettings BlackWhite => BlackWhiteSettings ?? new BlackWhiteSettings();
    public ColorBalanceSettings ColorBalance => ColorBalanceSettings ?? new ColorBalanceSettings();

    public double GaussianRadius => BlurRadius ?? 10;
    public double ResolvedMotionAngle => MotionAngle ?? 0;
    public double ResolvedMotionDistance => MotionDistance ?? 10;
    public double ResolvedNoiseAmount => NoiseAmount ?? 10;
    public bool ResolvedNoiseGaussian => NoiseGaussian ?? false;
    public bool ResolvedNoiseMonochromatic => NoiseMonochromatic ?? false;
    public uint ResolvedNoiseSeed => NoiseSeed ?? 0;

    /// <summary>
    /// Document pixels of halo this adjustment needs: a blur reads pixel neighbours, so a partial redraw
    /// has to hand it that much more than the rectangle it is asked for. Zero for the kinds that only look
    /// at the pixel in front of them.
    /// </summary>
    public int SamplingMargin => Kind switch
    {
        AdjustmentKind.GaussianBlur => (int)Math.Ceiling(GaussianRadius * 3 + 2),
        AdjustmentKind.MotionBlur => (int)Math.Ceiling(ResolvedMotionDistance / 2 + 2),
        _ => 0,
    };

    /// <summary>The three kinds that sample neighboring pixels, which arrived in version 9.</summary>
    public bool NeedsVersion9 =>
        Kind is AdjustmentKind.GaussianBlur or AdjustmentKind.MotionBlur or AdjustmentKind.AddNoise;

    public bool IsValid
    {
        get
        {
            if (!(double.IsFinite(Hue) && Math.Abs(Hue) <= 360
                  && double.IsFinite(Saturation) && Math.Abs(Saturation) <= 100
                  && double.IsFinite(Lightness) && Math.Abs(Lightness) <= 100)) return false;

            var hsv = ResolvedHSV;
            if (!hsv.Adjustments.Values.All(adjustment =>
                    double.IsFinite(adjustment.Hue) && Math.Abs(adjustment.Hue) <= 360
                    && double.IsFinite(adjustment.Saturation) && Math.Abs(adjustment.Saturation) <= 100
                    && double.IsFinite(adjustment.Lightness) && Math.Abs(adjustment.Lightness) <= 100))
            {
                return false;
            }
            if (!hsv.Bands.Values.All(band => band.Handles.All(double.IsFinite))) return false;

            if (Levels.Ranges is not { Count: 4 } || !Levels.Ranges.All(range => range is not null && range.IsNormalized)) return false;
            if (!Curves.IsValid) return false;
            if (!Exposure.IsValid || !GradientMap.IsValid || !Grain.IsValid || !BlackWhite.IsValid || !ColorBalance.IsValid) return false;

            return double.IsFinite(GaussianRadius) && GaussianRadius is >= 0.1 and <= 250
                && double.IsFinite(ResolvedMotionAngle) && ResolvedMotionAngle is >= -90 and <= 90
                && double.IsFinite(ResolvedMotionDistance) && ResolvedMotionDistance is >= 1 and <= 2000
                && double.IsFinite(ResolvedNoiseAmount) && ResolvedNoiseAmount is >= 0.1 and <= 400;
        }
    }
}
