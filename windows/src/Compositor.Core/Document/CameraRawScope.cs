using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// What the Camera Raw panel's scope shows: one 256-bin histogram for each of red, green and blue, and a
/// vectorscope of how much of the picture sits at each hue and saturation. It is counted from the graded
/// pixels — the grade the panel is asking for, not the layer as it was — and before any overlay is painted on
/// them, which is what the Mac build counts.
/// </summary>
public sealed class CameraRawScope
{
    public const int Bins = 256;
    public const int ScopeSide = 64;
    /// <summary>Nothing longer than this on its longer side is counted. A scope is a shape, not a measurement,
    /// so a whole twenty-four megapixel layer would be a waste; the Mac build counts its own reduced preview
    /// for the same reason.</summary>
    public const int SampleLimit = 2048;

    public double[] Red { get; }
    public double[] Green { get; }
    public double[] Blue { get; }
    /// <summary>Density from the middle outward, red to the right and hue running evenly round. Index
    /// <c>y * ScopeSide + x</c>.</summary>
    public double[] Vectorscope { get; }

    /// <summary>The height the ribbons are drawn at: the tallest of the three, with an isolated spike capped, so
    /// that one large flat background cannot flatten everything else. Shared, so the three stay comparable.</summary>
    public double Peak { get; }

    /// <summary>The hottest cell of the vectorscope, or zero when there is nothing to draw.</summary>
    public double ScopePeak { get; }

    private CameraRawScope(double[] red, double[] green, double[] blue, double[] vectorscope)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Vectorscope = vectorscope;
        Peak = Math.Max(Scale(red), Math.Max(Scale(green), Scale(blue)));
        ScopePeak = vectorscope.Length == 0 ? 0 : vectorscope.Max();
    }

    /// <summary>
    /// Counts a premultiplied buffer — the form the grade leaves the pixels in — reducing it first when it is
    /// longer than <paramref name="sample"/> on its longer side. Fully transparent pixels are skipped, so a
    /// picture with nothing in it counts as nothing.
    /// </summary>
    public static CameraRawScope OfPremultiplied(SKBitmap pixels, int sample = SampleLimit)
    {
        var bins = new double[Bins * 4];
        var vectorscope = new double[ScopeSide * ScopeSide];
        if (pixels is null || pixels.Width <= 0 || pixels.Height <= 0)
        {
            return new CameraRawScope(bins[256..512], bins[512..768], bins[768..1024], vectorscope);
        }
        using var fitted = Bitmaps.Fitted(pixels, sample);
        var bytes = fitted.GetPixelSpan();
        var stride = fitted.RowBytes;
        var width = fitted.Width;
        var height = fitted.Height;
        LevelsPixels.LevelsHistogram(bytes, [], width * height, bins);
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var at = row + x * 4;
                double alpha = bytes[at + 3];
                if (alpha == 0) continue;
                var red = Math.Min(1, bytes[at] / alpha);
                var green = Math.Min(1, bytes[at + 1] / alpha);
                var blue = Math.Min(1, bytes[at + 2] / alpha);
                var most = Math.Max(red, Math.Max(green, blue));
                var least = Math.Min(red, Math.Min(green, blue));
                var chroma = most - least;
                // A grey pixel has no hue to place, and a black one no colour at all: neither belongs on a
                // scope whose two axes are hue and saturation.
                if (!(chroma > 1e-4) || !(most > 1e-4)) continue;
                var hue = most == red ? (green - blue) / chroma
                    : most == green ? 2 + (blue - red) / chroma
                    : 4 + (red - green) / chroma;
                hue /= 6;
                if (hue < 0) hue += 1;
                var angle = hue * 2 * Math.PI;
                var saturation = chroma / most;
                var column = Math.Min(ScopeSide - 1, Math.Max(0, (int)((0.5 + Math.Cos(angle) * saturation * 0.48) * ScopeSide)));
                var line = Math.Min(ScopeSide - 1, Math.Max(0, (int)((0.5 + Math.Sin(angle) * saturation * 0.48) * ScopeSide)));
                vectorscope[line * ScopeSide + column] += alpha / 255;
            }
        }
        return new CameraRawScope(bins[256..512], bins[512..768], bins[768..1024], vectorscope);
    }

    /// <summary>
    /// The height one ribbon is drawn at: the bins' own ratios are kept, but an isolated spike is capped so
    /// that one large solid background cannot flatten the useful part of the distribution. The Mac build keeps
    /// this with its Levels panel and uses it for both; the port draws no histogram there, so it lives here.
    /// </summary>
    private static double Scale(double[] bins)
    {
        var peak = 0.0;
        foreach (var value in bins)
        {
            if (double.IsFinite(value) && value > peak) peak = value;
        }
        if (!(peak > 0)) return 0;
        if (bins.Length < 3) return peak;
        var interior = new List<double>();
        for (var index = 1; index < bins.Length - 1; index++)
        {
            if (double.IsFinite(bins[index]) && bins[index] > 0) interior.Add(bins[index]);
        }
        if (interior.Count == 0) return peak;
        interior.Sort();
        var typical = interior[(int)((interior.Count - 1) * 0.95)];
        return Math.Min(peak, typical * 4);
    }
}
