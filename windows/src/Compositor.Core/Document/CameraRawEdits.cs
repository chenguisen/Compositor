using Compositor.Core.Model;
using Compositor.Core.Pixels;
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

    // Detail: sharpening, and the noise the picture came with
    /// <summary>0 to 150: how much the edges are sharpened.</summary>
    public double SharpenAmount { get; set; }
    public double SharpenRadius { get; set; } = 10;
    public double SharpenDetail { get; set; } = 25;
    /// <summary>0 to 100: how little of the flat parts is sharpened, so noise is not sharpened too.</summary>
    public double SharpenMasking { get; set; }
    /// <summary>0 to 100: how much luminance noise is taken out.</summary>
    public double NoiseLuminance { get; set; }
    public double NoiseLuminanceDetail { get; set; } = 50;
    public double NoiseLuminanceContrast { get; set; }
    /// <summary>0 to 100: how much colour noise is taken out.</summary>
    public double NoiseColor { get; set; }
    public double NoiseColorDetail { get; set; } = 50;
    public double NoiseColorSmoothness { get; set; } = 50;

    // Optics: the lens's own faults
    /// <summary>Whether the fringes along high-contrast edges are taken out.</summary>
    public bool RemoveChromaticAberration { get; set; }
    /// <summary>Whether a lens profile is being applied.</summary>
    public bool EnableLensProfile { get; set; }
    /// <summary>The profile's distortion at full strength, 0 to 100.</summary>
    public double ProfileDistortion { get; set; } = 100;
    public double ProfileVignetting { get; set; } = 100;
    /// <summary>Manual distortion, −100 to 100: either direction straightens a lens.</summary>
    public double Distortion { get; set; }
    /// <summary>The purple and green fringes, how much to take out and the hues they sit between.</summary>
    public double PurpleAmount { get; set; }
    public double PurpleHueLow { get; set; } = 270;
    public double PurpleHueHigh { get; set; } = 310;
    public double GreenAmount { get; set; }
    public double GreenHueLow { get; set; } = 60;
    public double GreenHueHigh { get; set; } = 120;
    /// <summary>The l<ens's vignette, which is added to the effects group's own.</summary>
    public double OpticsVignetteAmount { get; set; }
    public double OpticsVignetteMidpoint { get; set; } = 50;

    // Calibration: the process version the sliders are read against
    /// <summary>1 to 6, as Photoshop numbers its process versions; six is the current one.</summary>
    public int ProcessVersion { get; set; } = 6;
    public double ShadowTint { get; set; }
    public double RedHue { get; set; }
    public double RedSaturation { get; set; }
    public double GreenHue { get; set; }
    public double GreenSaturation { get; set; }
    public double BlueHue { get; set; }
    public double BlueSaturation { get; set; }

    /// <summary>The distortion the optics group asks for, as the kernel wants it: a share of the corner's
    /// distance, the profile's own added when a profile is being applied.</summary>
    public double DistortionK =>
        (Distortion / 100 + (EnableLensProfile ? ProfileDistortion / 100 : 0)) * LensStrength;

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

    /// <summary>Whether the detail group asks for anything.</summary>
    public bool AdjustsDetail => SharpenAmount != 0 || NoiseLuminance != 0 || NoiseColor != 0;

    /// <summary>Whether the optics group asks for anything.</summary>
    public bool AdjustsOptics =>
        RemoveChromaticAberration || EnableLensProfile || Distortion != 0 || PurpleAmount != 0
        || GreenAmount != 0 || OpticsVignetteAmount != 0;

    /// <summary>Whether the calibration group asks for anything.</summary>
    public bool AdjustsCalibration =>
        ShadowTint != 0 || RedHue != 0 || RedSaturation != 0 || GreenHue != 0 || GreenSaturation != 0
        || BlueHue != 0 || BlueSaturation != 0;

    /// <summary>Nothing asked for, so there is nothing to do.</summary>
    public bool IsIdentity =>
        !AdjustsLight && !AdjustsColor && !AdjustsEffects && !AdjustsDetail && !AdjustsOptics && !AdjustsCalibration;

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
        && GlowStyle is >= 0 and <= 2 && VignetteStyle is >= 0 and <= 2
        && Within(SharpenAmount, 0, 150) && Within(SharpenRadius, 0.5, 100)
        && Within(SharpenDetail, 0, 100) && Within(SharpenMasking, 0, 100)
        && Within(NoiseLuminance, 0, 100) && Within(NoiseLuminanceDetail, 0, 100)
        && Within(NoiseLuminanceContrast, 0, 100) && Within(NoiseColor, 0, 100)
        && Within(NoiseColorDetail, 0, 100) && Within(NoiseColorSmoothness, 0, 100)
        && Within(ProfileDistortion, 0, 100) && Within(ProfileVignetting, 0, 100)
        && Within(Distortion, -100, 100) && Within(PurpleAmount, 0, 100)
        && Within(PurpleHueLow, 0, 360) && Within(PurpleHueHigh, 0, 360)
        && Within(GreenAmount, 0, 100) && Within(GreenHueLow, 0, 360) && Within(GreenHueHigh, 0, 360)
        && Within(OpticsVignetteAmount, -100, 100) && Within(OpticsVignetteMidpoint, 0, 100)
        && ProcessVersion is >= 1 and <= 6
        && Within(ShadowTint, -100, 100) && Within(RedHue, -100, 100) && Within(RedSaturation, -100, 100)
        && Within(GreenHue, -100, 100) && Within(GreenSaturation, -100, 100)
        && Within(BlueHue, -100, 100) && Within(BlueSaturation, -100, 100);

    /// <summary>The corner's distance a distortion of ±100 moves, as the Mac build's lens strength is.</summary>
    public const double LensStrength = 0.35;

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
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return false;
        if (!FilterSurface.Begin(layer, 0, out var work, out var placement)) return false;
        using var _ = work;
        using var was = document.Selection.Path is null ? null : FilterSurface.Copy(work);
        var pixels = work.GetPixelSpan();
        var width = work.Width;
        var height = work.Height;
        var stride = work.RowBytes;
        if (settings.AdjustsCalibration)
        {
            AdjustPixels.CameraRawCalibration(pixels, width, height, stride,
                settings.ShadowTint, settings.RedHue, settings.RedSaturation,
                settings.GreenHue, settings.GreenSaturation, settings.BlueHue, settings.BlueSaturation,
                settings.ProcessVersion);
        }
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
        if (settings.AdjustsOptics)
        {
            AdjustPixels.CameraRawOptics(pixels, width, height, stride,
                settings.RemoveChromaticAberration, settings.EnableLensProfile ? 1 : 0,
                settings.ProfileDistortion, settings.ProfileVignetting, settings.DistortionK,
                settings.PurpleAmount, settings.PurpleHueLow, settings.PurpleHueHigh,
                settings.GreenAmount, settings.GreenHueLow, settings.GreenHueHigh,
                settings.OpticsVignetteAmount, settings.OpticsVignetteMidpoint, 1);
        }
        if (settings.AdjustsDetail)
        {
            AdjustPixels.CameraRawDetail(pixels, width, height, stride,
                settings.SharpenAmount, settings.SharpenRadius, settings.SharpenDetail, settings.SharpenMasking,
                settings.NoiseLuminance, settings.NoiseLuminanceDetail, settings.NoiseLuminanceContrast,
                settings.NoiseColor, settings.NoiseColorDetail, settings.NoiseColorSmoothness, 1);
        }
        if (was is not null) FilterSurface.Keep(document, was, work, placement);
        FilterSurface.Finish(layer, work, placement);
        return true;
    }
}
