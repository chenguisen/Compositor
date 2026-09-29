using Compositor.Core.Format;

namespace Compositor.Core.IO.PSD;

/// <summary>
/// Photoshop's adjustment layer blocks, from Adobe's 2019 specification: `levl` (Levels), `curv` (Curves)
/// and `hue2`/`hue ` (Hue/Saturation). Every other adjustment kind is left for the report.
/// </summary>
internal static class PsdAdjustments
{
    public static LayerAdjustment? Parse(ReadOnlySpan<byte> data, Dictionary<string, (int Offset, int Length)> extra)
    {
        if (TrySlice(data, extra, "levl", out var levels)) return Levels(levels);
        if (TrySlice(data, extra, "curv", out var curves)) return Curves(curves);
        if (TrySlice(data, extra, "hue2", out var hue) || TrySlice(data, extra, "hue ", out hue)) return Hue(hue);
        return null;
    }

    private static bool TrySlice(ReadOnlySpan<byte> data, Dictionary<string, (int Offset, int Length)> extra, string key,
        out ReadOnlySpan<byte> slice)
    {
        if (extra.TryGetValue(key, out var block))
        {
            slice = data.Slice(block.Offset, block.Length);
            return true;
        }
        slice = default;
        return false;
    }

    /// <summary>
    /// A version, then records of input black, input white, output black, output white and gamma in
    /// hundredths (100 is 1.00), for RGB, then red, green and blue.
    /// </summary>
    private static LayerAdjustment? Levels(ReadOnlySpan<byte> data)
    {
        if (data.Length < 292) return null;
        var settings = new LevelsSettings();
        for (var channel = 0; channel < 4; channel++)
        {
            var at = 2 + channel * 10;
            settings.Ranges[channel] = new LevelsChannelRange
            {
                Black = U16(data, at),
                White = U16(data, at + 2),
                OutputBlack = U16(data, at + 4),
                OutputWhite = U16(data, at + 6),
                Gamma = U16(data, at + 8) / 100.0,
            }.Normalized();
        }
        return new LayerAdjustment { Kind = AdjustmentKind.Levels, Levels = settings };
    }

    private static LayerAdjustment? Curves(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return null;
        var offset = data[0] == 0 ? 1 : 0;
        if (offset + 2 > data.Length) return null;
        var version = U16(data, offset);
        offset += 2;
        if (version is not (1 or 4)) return null;
        if (offset + 2 > data.Length) return null;
        var count = U16(data, offset);
        offset += 2;
        var settings = new CurvesSettings();
        for (var channel = 0; channel < Math.Min(4, (int)count); channel++)
        {
            if (offset + 2 > data.Length) return null;
            var points = U16(data, offset);
            offset += 2;
            var curve = new List<CurvePoint>(points);
            for (var index = 0; index < points; index++)
            {
                if (offset + 4 > data.Length) return null;
                var output = U16(data, offset);
                var input = U16(data, offset + 2);
                offset += 4;
                curve.Add(new CurvePoint { X = Math.Clamp((int)input, 0, 255), Y = Math.Clamp((int)output, 0, 255) });
            }
            if (curve.Count < 2) continue;
            curve.Sort((left, right) => left.X.CompareTo(right.X));
            if (curve[0].X != 0) curve.Insert(0, new CurvePoint { X = 0, Y = curve[0].Y });
            if (curve[^1].X != 255) curve.Add(new CurvePoint { X = 255, Y = curve[^1].Y });
            settings.Channels[channel] = curve;
        }
        if (!settings.IsValid) return null;
        return new LayerAdjustment { Kind = AdjustmentKind.Curves, Curves = settings };
    }

    /// <summary>
    /// A version, the Colorize switch and a pad byte, the Colorize hue, saturation and lightness, the
    /// Master's, then for Reds through Magentas the band (where the range fades in, is full, and fades out,
    /// in degrees) and its hue, saturation and lightness.
    /// </summary>
    private static LayerAdjustment? Hue(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return null;
        var colorize = data[2] != 0;
        var settings = new HueSaturationSettings { Colorize = colorize };
        // Colorize has values of its own; the Master applies otherwise.
        settings.Adjustments.Set(ColorRange.Master, Values(data, colorize ? 4 : 10));
        if (!colorize)
        {
            // The Master's three values come first; the six color ranges follow it.
            var offset = 16;
            foreach (var range in HueBand.Ranges.Skip(1))
            {
                if (offset + 14 > data.Length) break;
                settings.Bands.Set(range, new HueBand
                {
                    FalloffStart = Degrees(data, offset),
                    RangeStart = Degrees(data, offset + 2),
                    RangeEnd = Degrees(data, offset + 4),
                    FalloffEnd = Degrees(data, offset + 6),
                });
                settings.Adjustments.Set(range, Values(data, offset + 8));
                offset += 14;
            }
        }
        var adjustment = new LayerAdjustment { Kind = AdjustmentKind.HueSaturation, HsvSettings = settings };
        // The Mac build takes these values on trust; the document rejects what it cannot hold.
        return adjustment.IsValid ? adjustment : null;
    }

    private static RangeAdjustment Values(ReadOnlySpan<byte> data, int at) => new()
    {
        Hue = I16(data, at),
        Saturation = I16(data, at + 2),
        Lightness = I16(data, at + 4),
    };

    /// <summary>An angle in degrees, wrapped into 0…360.</summary>
    private static double Degrees(ReadOnlySpan<byte> data, int at)
    {
        var value = I16(data, at) % 360.0;
        return value < 0 ? value + 360 : value;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at) => (ushort)(data[at] << 8 | data[at + 1]);

    private static short I16(ReadOnlySpan<byte> data, int at) => (short)U16(data, at);
}
