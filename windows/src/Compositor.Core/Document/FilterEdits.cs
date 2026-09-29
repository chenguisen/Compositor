using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>Which of the filters that share one settings bag is being applied.</summary>
public enum FilterKind
{
    /// <summary>Softens everything by a Gaussian, fading at the layer's edge rather than stopping at it.</summary>
    GaussianBlur,

    /// <summary>Speckles the picture with random brightness, as film grain or sensor noise.</summary>
    AddNoise,

    /// <summary>Darkens or lightens the edges of the frame, leaving the middle alone.</summary>
    Vignette,

    /// <summary>Local contrast: the fine detail against a blurred copy of itself.</summary>
    TonalContrast,

    /// <summary>Radial distortion: either direction straightens a lens.</summary>
    LensCorrection,
}

/// <summary>
/// The filters that are not Camera Raw, as one bag of amounts — the Mac build keeps them in one struct too,
/// because only the numbers belonging to the chosen filter are ever read.
/// <para>
/// A class rather than a struct: several of these defaults are not zero (the vignette's shape, the tonal
/// amounts), and a record struct zeroed by <c>new</c> would turn "the filter's own defaults" into "nothing".
/// </para>
/// </summary>
public sealed class FilterSettings
{
    /// <summary>How far the distortion is pushed at ±100, as a share of the corner's distance.</summary>
    public const double LensStrength = 0.35;

    // Gaussian blur
    /// <summary>Standard deviation in layer pixels, 0.1 to 250.</summary>
    public double BlurRadius { get; set; } = 1;

    // Add noise
    /// <summary>Photoshop's percentage, 0.1 to 400: uniform noise spans ±this share of half the range.</summary>
    public double NoiseAmount { get; set; } = 10;
    /// <summary>Gaussian (more speckled, a standard deviation of two thirds of that) rather than uniform.</summary>
    public bool NoiseGaussian { get; set; }
    /// <summary>The same amount on every channel, so the brightness changes and the colour does not.</summary>
    public bool NoiseMonochromatic { get; set; }

    // Vignette
    /// <summary>0 to 100: how far the edge colour is blended in.</summary>
    public double VignetteAmount { get; set; } = 35;
    /// <summary>What the edges are taken towards.</summary>
    public double VignetteRed { get; set; }
    public double VignetteGreen { get; set; }
    public double VignetteBlue { get; set; }
    public double VignetteMidpoint { get; set; } = 50;
    /// <summary>−100 to 100: 100 is a circle, lower is a rectangle.</summary>
    public double VignetteRoundness { get; set; } = 100;
    public double VignetteFeather { get; set; } = 60;
    /// <summary>0 to 100: how much bright pixels are protected while the edges darken.</summary>
    public double VignetteHighlights { get; set; } = 25;

    // Tonal contrast
    /// <summary>0 to 100: how much of the local detail is added back.</summary>
    public double TonalAmount { get; set; } = 50;
    /// <summary>The detail radius in layer pixels.</summary>
    public double TonalRadius { get; set; } = 16;
    public double TonalShadows { get; set; } = 40;
    public double TonalMidtones { get; set; } = 60;
    public double TonalHighlights { get; set; } = 30;

    // Lens correction
    public double Distortion { get; set; }

    /// <summary>Whether the amounts this filter reads are ones it may use.</summary>
    public bool IsValid(FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur => Within(BlurRadius, 0.1, 250),
        FilterKind.AddNoise => Within(NoiseAmount, 0.1, 400),
        FilterKind.Vignette =>
            Within(VignetteAmount, 0, 100) && Within(VignetteMidpoint, 0, 100) && Within(VignetteRoundness, -100, 100)
            && Within(VignetteFeather, 0, 100) && Within(VignetteHighlights, 0, 100)
            && Within(VignetteRed, 0, 1) && Within(VignetteGreen, 0, 1) && Within(VignetteBlue, 0, 1),
        FilterKind.TonalContrast =>
            Within(TonalAmount, 0, 100) && Within(TonalRadius, 1, 100)
            && Within(TonalShadows, -100, 100) && Within(TonalMidtones, -100, 100) && Within(TonalHighlights, -100, 100),
        _ => Within(Distortion, -100, 100),
    };

    /// <summary>Whether this filter would change anything at all.</summary>
    public bool DoesAnything(FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur => BlurRadius > 0,
        FilterKind.AddNoise => NoiseAmount > 0,
        FilterKind.Vignette => VignetteAmount > 0,
        FilterKind.TonalContrast =>
            TonalAmount > 0 && (TonalShadows != 0 || TonalMidtones != 0 || TonalHighlights != 0),
        _ => Distortion != 0,
    };

    private static bool Within(double value, double least, double most) =>
        double.IsFinite(value) && value >= least && value <= most;
}

/// <summary>
/// The filters that are not Camera Raw, run over a layer's own pixels and held to the selection, each as one
/// edit. The kernels were ported from the Mac build's C and are covered by the pixel tests; this is the
/// surface that reaches them.
/// </summary>
public static class FilterEdits
{
    /// <summary>
    /// How far past the layer's edge a spreading filter is given room, in layer pixels — the Mac build's
    /// margin, wide enough for the spread to have died out before it meets the buffer's border.
    /// </summary>
    public static int Margin(FilterKind kind, FilterSettings settings) => kind switch
    {
        FilterKind.GaussianBlur => (int)Math.Ceiling(settings.BlurRadius * 3 + 2),
        _ => 0,
    };

    /// <summary>
    /// Applies a filter to a layer's pixels. False when there is nothing to do, when the amounts or the layer
    /// cannot take it, or when the pixels would not fit in memory — in which case the layer is left as it was.
    /// <paramref name="seed"/> fixes Add Noise's pattern; zero takes a fresh one each time.
    /// </summary>
    public static bool Apply(CanvasDocument document, Guid layerID, FilterKind kind, FilterSettings settings, uint seed = 0)
    {
        if (!settings.IsValid(kind) || !settings.DoesAnything(kind)) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return false;
        // Where the mask sat before the layer's grid changes under it, which is what carries it across.
        var maskPlacement = layer.MaskTransform;
        if (!FilterSurface.Begin(layer, Margin(kind, settings), out var work, out var placement)) return false;
        using var _ = work;
        using var was = document.Selection.Path is null ? null : FilterSurface.Copy(work);
        var pixels = work.GetPixelSpan();
        var width = work.Width;
        var height = work.Height;
        var stride = work.RowBytes;
        switch (kind)
        {
            case FilterKind.GaussianBlur:
                // Not clamped: the room around the layer exists so the blur fades at its edge; the border of
                // the wider buffer is far enough away that reading it as the edge changes nothing.
                GaussianBlur.Clamped(pixels, width, height, 4, stride, settings.BlurRadius);
                break;
            case FilterKind.AddNoise:
                NoisePixels.NoiseAdd(pixels, width, height, stride, (float)settings.NoiseAmount,
                    settings.NoiseGaussian, settings.NoiseMonochromatic, seed != 0 ? seed : (uint)Random.Shared.Next(1, int.MaxValue));
                break;
            case FilterKind.Vignette:
                // The whole layer is the frame: this is not a crop, so nothing is outside it to paint.
                AdjustPixels.ColoredVignette(pixels, width, height, stride, 0, 0, width, height, fillsClear: false,
                    settings.VignetteAmount, settings.VignetteMidpoint, settings.VignetteRoundness,
                    settings.VignetteFeather, settings.VignetteHighlights,
                    settings.VignetteRed, settings.VignetteGreen, settings.VignetteBlue);
                break;
            case FilterKind.TonalContrast:
                using (var blurred = Blur(work, settings.TonalRadius))
                {
                    AdjustPixels.TonalContrast(pixels, blurred.GetPixelSpan(), width, height, stride, blurred.RowBytes,
                        settings.TonalAmount, settings.TonalShadows, settings.TonalMidtones, settings.TonalHighlights);
                }
                break;
            default:
                using (var source = FilterSurface.Copy(work))
                {
                    LensPixels.LensDistort(source.GetPixelSpan(), pixels, width, height, stride,
                        settings.Distortion / 100 * FilterSettings.LensStrength);
                }
                break;
        }
        if (was is not null) FilterSurface.Keep(document, was, work, placement);
        if (kind == FilterKind.GaussianBlur)
        {
            // The blurred layer spreads past its own edge: keep the pixels it now covers and drop the room
            // that stayed empty, so the layer does not carry an invisible border around with it.
            var (trimmed, placed) = LayerMerge.Trimmed(work, placement);
            using (trimmed) FilterSurface.Finish(layer, trimmed, placed);
            // A mask on the layer's old grid has to be drawn over the new one, or it would stretch with it.
            if (layer.Mask is { } held && FilterSurface.PlaceMask(layer, maskPlacement, placed) is { } carried)
            {
                var mask = LayerMask.AssetFrom(carried);
                mask.IsEnabled = held.IsEnabled;
                mask.IsLinked = held.IsLinked;
                layer.Mask = mask;
            }
            return true;
        }
        FilterSurface.Finish(layer, work, placement);
        return true;
    }

    /// <summary>The same pixels softened by <paramref name="radius"/>, which local contrast measures against.</summary>
    private static SKBitmap Blur(SKBitmap source, double radius)
    {
        var blurred = FilterSurface.Copy(source);
        // The same clamped blur the Mac build uses, so the edge of the picture is not treated as empty.
        GaussianBlur.Clamped(blurred.GetPixelSpan(), blurred.Width, blurred.Height, 4, blurred.RowBytes, radius);
        return blurred;
    }
}
