using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>What Auto Levels works out of the picture: a shared contrast stretch, one per channel, or that
/// with the midtones brought back to middle grey.</summary>
public enum LevelsAuto
{
    /// <summary>One interval across all three channels, so the picture's colours keep their relationship.</summary>
    Contrast,

    /// <summary>An interval for each channel, which takes a colour cast out.</summary>
    Color,

    /// <summary>As Colour, and each channel's midtone is brought to middle grey.</summary>
    Neutral,
}

/// <summary>
/// Image ▸ Auto Levels: the levels the picture itself asks for. The Mac build works them out from a histogram
/// of the pixels and shows them in the Levels panel; here they are worked out the same way and put straight on
/// the layer, through the same levels operator the panel's own amounts go through.
/// </summary>
public static class LevelsEdits
{
    /// <summary>The share of the pixels at each end that is treated as an outlier and left out of the range.</summary>
    private const double Clip = 0.001;

    /// <summary>
    /// The levels this picture asks for. <paramref name="premultiplied"/> is the layer as the kernels see it;
    /// fully transparent pixels are left out of the count, as they are for every other kernel.
    /// </summary>
    public static LevelsSettings Automatic(SKBitmap premultiplied, LevelsAuto mode)
    {
        var bins = new double[1024];
        LevelsPixels.LevelsHistogram(premultiplied.GetPixelSpan(), [], premultiplied.Width * premultiplied.Height,
            bins);
        var settings = new LevelsSettings();
        if (mode == LevelsAuto.Contrast)
        {
            // One interval for all three, so a colour is stretched by the same amount as the others.
            double? low = null, high = null;
            for (var channel = 1; channel <= 3; channel++)
            {
                if (Endpoints(bins, channel) is not { } limits) continue;
                low = low is { } least ? Math.Min(least, limits.Low) : limits.Low;
                high = high is { } most ? Math.Max(most, limits.High) : limits.High;
            }
            if (low is { } black && high is { } white && black < white)
            {
                settings.Ranges[0] = new LevelsChannelRange { Black = black, White = white };
            }
            return settings;
        }
        for (var channel = 1; channel <= 3; channel++)
        {
            if (Endpoints(bins, channel) is not { } limits) continue;
            var range = new LevelsChannelRange { Black = limits.Low, White = limits.High };
            if (mode == LevelsAuto.Neutral)
            {
                // Where the midtone now lands, and the gamma that brings it back to the middle.
                var total = 0.0;
                var mean = 0.0;
                for (var value = 0; value < 256; value++)
                {
                    total += bins[channel * 256 + value];
                    mean += Mapped(range, value / 255.0) * bins[channel * 256 + value];
                }
                if (total > 0)
                {
                    mean /= total;
                    if (mean is > 0 and < 1) range.Gamma = Math.Clamp(Math.Log(mean) / Math.Log(0.5), 0.1, 9.99);
                }
            }
            settings.Ranges[channel] = range;
        }
        return settings;
    }

    /// <summary>
    /// Puts the levels the picture asks for on a layer, held to the selection, as one edit. False when the
    /// layer cannot take it or when there is nothing to work out — a picture with no pixels of its own.
    /// </summary>
    public static bool Auto(CanvasDocument document, Guid layerID, LevelsAuto mode)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return false;
        if (!FilterSurface.Begin(layer, 0, out var work, out _)) return false;
        LevelsSettings settings;
        using (work) settings = Automatic(work, mode);
        var adjustment = new LayerAdjustment { Kind = AdjustmentKind.Levels, Levels = settings };
        if (!adjustment.IsValid) return false;
        // A picture with no range in it asks for nothing, and asking for nothing is not an edit: a flat
        // colour should not leave a step in the history that changes nothing.
        if (settings.Ranges.SequenceEqual(new LevelsSettings().Ranges)) return false;
        // The same path the panel's own levels take, so the selection and the finish are handled once.
        return FilterEdits.ApplyAdjustment(document, layerID, adjustment);
    }

    /// <summary>
    /// Where a channel's pixels start and end, leaving out the share at each end that is outliers: the black
    /// point below which almost nothing is, and the white point above which almost nothing is.
    /// </summary>
    private static (double Low, double High)? Endpoints(ReadOnlySpan<double> bins, int channel)
    {
        ReadOnlySpan<double> counts = bins.Slice(channel * 256, 256);
        var total = 0.0;
        foreach (var count in counts) total += count;
        if (total <= 0) return null;
        var low = 0;
        var running = 0.0;
        for (var value = 0; value < 256; value++)
        {
            running += counts[value];
            if (running > total * Clip)
            {
                low = value;
                break;
            }
        }
        var high = 255;
        running = 0;
        for (var value = 255; value >= 0; value--)
        {
            running += counts[value];
            if (running > total * Clip)
            {
                high = value;
                break;
            }
        }
        return low < high ? (low, high) : null;
    }

    /// <summary>
    /// What a range makes of one value, in the same three steps the levels kernel takes: the interval, the
    /// gamma, then the output interval. Written here so the midtone can be found without running the kernel.
    /// </summary>
    private static double Mapped(LevelsChannelRange range, double value)
    {
        var span = range.White - range.Black;
        var stretched = span > 0 ? Math.Clamp((value * 255 - range.Black) / span, 0, 1) : 0;
        var shaped = range.Gamma > 0 ? Math.Pow(stretched, 1 / range.Gamma) : stretched;
        return (range.OutputBlack + shaped * (range.OutputWhite - range.OutputBlack)) / 255.0;
    }
}
