using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// The Camera Raw filter's settings, as its groups present them. A filter is applied to a layer's pixels
/// there and then, so nothing here is saved with the project — the format has no room for it, as the Mac
/// build's has none.
/// <para>
/// A class rather than a struct: <c>new CameraRawSettings()</c> on a record struct would zero the fields
/// whose defaults are not zero (the vignette midpoint and feather, the grain size and roughness), which
/// would quietly change what "no adjustment" looks like.
/// </para>
/// </summary>
public sealed class CameraRawSettings
{
    /// <summary>Share of a full warm/cool swing applied to red and blue.</summary>
    public const double TemperatureGain = 0.35;

    /// <summary>Magenta/green swing shared by red and blue, and the opposite one on green.</summary>
    public const double TintRedBlue = 0.15;
    public const double TintGreen = 0.30;

    // Light
    /// <summary>Stops of linear light, −5 to 5.</summary>
    public double Exposure { get; set; }
    /// <summary>Each of these is −100 to 100.</summary>
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }

    // Color
    /// <summary>Relative cool to warm: positive is warmer.</summary>
    public double Temperature { get; set; }
    /// <summary>Green to magenta: positive is magenta.</summary>
    public double Tint { get; set; }
    public double Vibrance { get; set; }
    public double Saturation { get; set; }

    // Effects
    /// <summary>The finer band of local contrast; Clarity is the broader one.</summary>
    public double Texture { get; set; }
    public double Clarity { get; set; }
    public double Dehaze { get; set; }
    /// <summary>0 to 100: range, spread and warmth are idle while this is zero.</summary>
    public double Glow { get; set; }
    /// <summary>0 diffusion, 1 bloom, 2 halation.</summary>
    public int GlowStyle { get; set; }
    public double GlowRange { get; set; }
    public double GlowSpread { get; set; }
    public double GlowWarmth { get; set; }
    /// <summary>−100 to 100: negative darkens the edges, positive lightens them.</summary>
    public double VignetteAmount { get; set; }
    /// <summary>0 highlight priority, 1 color priority, 2 paint overlay.</summary>
    public int VignetteStyle { get; set; }
    public double VignetteMidpoint { get; set; } = 50;
    public double VignetteRoundness { get; set; }
    public double VignetteFeather { get; set; } = 50;
    /// <summary>Used only while the amount darkens, and only for highlight priority.</summary>
    public double VignetteHighlights { get; set; }
    /// <summary>0 to 100: zero adds no grain.</summary>
    public double GrainAmount { get; set; }
    public double GrainSize { get; set; } = 25;
    public double GrainRoughness { get; set; } = 50;

    /// <summary>Camera Raw's 0…100 size, in the pixel scale the grain kernel already uses.</summary>
    public double GrainKernelSize => 0.5 + GrainSize / 100 * 19.5;

    /// <summary>The channel multipliers temperature and tint ask for; neutral is 1, 1, 1.</summary>
    public (double Red, double Green, double Blue) Gains
    {
        get
        {
            var warm = Temperature / 100;
            var magenta = Tint / 100;
            return (1 + TemperatureGain * warm + TintRedBlue * magenta,
                1 - TintGreen * magenta,
                1 - TemperatureGain * warm + TintRedBlue * magenta);
        }
    }

    /// <summary>Whether the light group asks for anything.</summary>
    public bool AdjustsLight =>
        Exposure != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 || Whites != 0 || Blacks != 0;

    /// <summary>Whether the colour group asks for anything.</summary>
    public bool AdjustsColor =>
        Temperature != 0 || Tint != 0 || Vibrance != 0 || Saturation != 0;

    /// <summary>Whether the effects group asks for anything.</summary>
    public bool AdjustsEffects =>
        Texture != 0 || Clarity != 0 || Dehaze != 0 || Glow > 0 || VignetteAmount != 0 || GrainAmount > 0;

    /// <summary>Nothing asked for, so there is nothing to do.</summary>
    public bool IsIdentity => !AdjustsLight && !AdjustsColor && !AdjustsEffects;

    /// <summary>Every slider within the range its group allows.</summary>
    public bool IsValid =>
        Within(Exposure, -5, 5) && Within(Contrast, -100, 100) && Within(Highlights, -100, 100)
        && Within(Shadows, -100, 100) && Within(Whites, -100, 100) && Within(Blacks, -100, 100)
        && Within(Temperature, -100, 100) && Within(Tint, -100, 100)
        && Within(Vibrance, -100, 100) && Within(Saturation, -100, 100)
        && Within(Texture, -100, 100) && Within(Clarity, -100, 100) && Within(Dehaze, -100, 100)
        && Within(Glow, 0, 100) && Within(GlowRange, 0, 100) && Within(GlowSpread, 0, 100)
        && Within(GlowWarmth, -100, 100)
        && Within(VignetteAmount, -100, 100) && Within(VignetteMidpoint, 0, 100)
        && Within(VignetteRoundness, -100, 100) && Within(VignetteFeather, 0, 100)
        && Within(VignetteHighlights, -100, 100)
        && Within(GrainAmount, 0, 100) && Within(GrainSize, 0, 100) && Within(GrainRoughness, 0, 100)
        && GlowStyle is >= 0 and <= 2 && VignetteStyle is >= 0 and <= 2;

    private static bool Within(double value, double least, double most) =>
        double.IsFinite(value) && value >= least && value <= most;
}

/// <summary>
/// The Camera Raw filter: the Light, Color and Effects stages run over a layer's own pixels, in the order
/// the Mac build runs them, held to the selection, as one edit.
/// </summary>
public static class CameraRawEdits
{
    /// <summary>
    /// Applies the filter to a layer's pixels. False when there is nothing to do, when the settings or the
    /// layer cannot take it, or when the pixels would not fit in memory — in which case the layer is left
    /// exactly as it was.
    /// </summary>
    public static bool Apply(CanvasDocument document, Guid layerID, CameraRawSettings settings, uint seed = 0)
    {
        if (settings.IsIdentity || !settings.IsValid) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        if (layer.IsGroup) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }

        // The kernels read premultiplied pixels — they divide the colour back out themselves — so the layer
        // is drawn into the same kind of buffer the compositor works in, and the result is turned back into
        // the straight-alpha pixels a layer is held in.
        using var work = DocumentRenderer.Allocate(width, height);
        using (var canvas = new SKCanvas(work))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }

        var pixels = work.GetPixelSpan();
        var stride = work.RowBytes;
        if (settings.AdjustsLight || settings.AdjustsColor)
        {
            var (red, green, blue) = settings.Gains;
            AdjustPixels.CameraRaw(pixels, width, height, stride, red, green, blue,
                settings.Exposure, settings.Contrast, settings.Highlights, settings.Shadows,
                settings.Whites, settings.Blacks, settings.Vibrance, settings.Saturation, 0);
        }
        if (settings.AdjustsEffects)
        {
            // A full-size apply: one preview pixel per layer pixel.
            AdjustPixels.CameraRawEffects(pixels, width, height, stride,
                settings.Texture, settings.Clarity, settings.Dehaze,
                settings.Glow, settings.GlowStyle, settings.GlowRange, settings.GlowSpread, settings.GlowWarmth,
                settings.VignetteAmount, settings.VignetteMidpoint, settings.VignetteRoundness,
                settings.VignetteFeather, settings.VignetteHighlights, settings.VignetteStyle, 1);
            if (settings.GrainAmount > 0)
            {
                // The pattern is anchored to the layer's own origin, so it does not move if the layer does.
                AdjustPixels.Grain(pixels, width, height, stride, settings.GrainAmount, settings.GrainKernelSize,
                    settings.GrainRoughness, seed != 0 ? seed : (uint)Random.Shared.Next(1, int.MaxValue), 0, 0, 1);
            }
        }

        var result = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(result))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(work, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        // A filter is held to the selection, as every other edit is: outside it the pixels are what they were.
        ApplySelection(document, asset.Image, result, layer.Transform);
        layer.Asset = ImportedImage.Create(result, asset.Name);
        return true;
    }

    /// <summary>
    /// Mixes the filtered pixels over the original where the selection says so, which is how a filter inside
    /// a selection leaves everything else alone. Nothing to mix when there is no selection at all.
    /// </summary>
    private static void ApplySelection(CanvasDocument document, SKBitmap original, SKBitmap filtered,
        Model.LayerTransform placement)
    {
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        if (document.Selection.Path is null) return;
        SKBitmap? coverage;
        try
        {
            coverage = document.Selection.Coverage(region);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (coverage is null) return;
        using var _ = coverage;
        var toDocument = BrushEdits.PixelToDocument(placement, filtered.Width, filtered.Height);
        var clip = coverage.GetPixelSpan();
        var was = original.GetPixelSpan();
        var now = filtered.GetPixelSpan();
        var stride = filtered.RowBytes;
        for (var y = 0; y < filtered.Height; y++)
        {
            for (var x = 0; x < filtered.Width; x++)
            {
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                var column = (int)Math.Floor(at.X) - region.Left;
                var row = (int)Math.Floor(at.Y) - region.Top;
                var amount = column < 0 || row < 0 || column >= region.Width || row >= region.Height
                    ? 0.0
                    : clip[row * coverage.RowBytes + column] / 255.0;
                if (amount >= 1) continue;
                var index = y * stride + x * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    now[index + channel] = (byte)Math.Clamp(
                        Math.Round(was[index + channel] + (now[index + channel] - was[index + channel]) * amount), 0, 255);
                }
            }
        }
    }
}
