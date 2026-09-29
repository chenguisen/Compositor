using SkiaSharp;

namespace Compositor.Core.Pixels;

/// <summary>
/// The 24 Photoshop blend modes the app supports, in the order the blend menu lists them
/// (Compositor/Document/LayerAppearance.swift).
/// </summary>
public enum BlendMode
{
    Normal,
    Darken, Multiply, ColorBurn, LinearBurn,
    Lighten, Screen, ColorDodge, LinearDodgeAdd,
    Overlay, SoftLight, HardLight, VividLight, LinearLight, PinLight, HardMix,
    Difference, Exclusion, Subtract, Divide,
    Hue, Saturation, Color, Luminosity,
}

/// <summary>
/// The Photoshop blend modes and the names, Skia mappings and hand-rolled formulas behind them.
/// <para>
/// Skia's <see cref="SKBlendMode"/> covers 16 of the 24. It has no equivalent for <see cref="BlendMode.LinearBurn"/>,
/// <see cref="BlendMode.LinearDodgeAdd"/>, <see cref="BlendMode.VividLight"/>, <see cref="BlendMode.LinearLight"/>,
/// <see cref="BlendMode.PinLight"/>, <see cref="BlendMode.HardMix"/>, <see cref="BlendMode.Subtract"/> or
/// <see cref="BlendMode.Divide"/>, so those eight are computed here on RGBA pixels by
/// <see cref="Composite"/> and <see cref="CompositePixel"/>.
/// </para>
/// </summary>
public static class BlendModes
{
    /// <summary>Every mode, in the blend menu's order.</summary>
    public static readonly BlendMode[] All =
    {
        BlendMode.Normal,
        BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn,
        BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodgeAdd,
        BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight,
        BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix,
        BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide,
        BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity,
    };

    // Indexed by BlendMode; the enum's declaration order is the menu's order.
    private static readonly string[] ModeNames =
    {
        "Normal",
        "Darken", "Multiply", "Color Burn", "Linear Burn",
        "Lighten", "Screen", "Color Dodge", "Linear Dodge (Add)",
        "Overlay", "Soft Light", "Hard Light", "Vivid Light", "Linear Light", "Pin Light", "Hard Mix",
        "Difference", "Exclusion", "Subtract", "Divide",
        "Hue", "Saturation", "Color", "Luminosity",
    };

    /// <summary>The mode's Photoshop name, spelled the way the app's UI spells it.</summary>
    public static string Name(BlendMode mode) => ModeNames[(int)mode];

    public static bool TryParse(string name, out BlendMode mode)
    {
        int index = Array.IndexOf(ModeNames, name);
        mode = index >= 0 ? (BlendMode)index : BlendMode.Normal;
        return index >= 0;
    }

    /// <summary>
    /// Whether this mode has to be composited by <see cref="Composite"/> rather than drawn by Skia.
    /// </summary>
    public static bool IsHandRolled(BlendMode mode) => mode switch
    {
        BlendMode.LinearBurn or BlendMode.LinearDodgeAdd or BlendMode.VividLight or BlendMode.LinearLight
            or BlendMode.PinLight or BlendMode.HardMix or BlendMode.Subtract or BlendMode.Divide => true,
        _ => false,
    };

    /// <summary>
    /// The mode a format record names. Spelled out rather than cast: the two enumerations happen to be in
    /// the same order today, and a mode silently becoming a different one is not a bug worth risking.
    /// </summary>
    public static BlendMode From(Format.LayerBlendMode mode) => mode switch
    {
        Format.LayerBlendMode.Normal => BlendMode.Normal,
        Format.LayerBlendMode.Darken => BlendMode.Darken,
        Format.LayerBlendMode.Multiply => BlendMode.Multiply,
        Format.LayerBlendMode.ColorBurn => BlendMode.ColorBurn,
        Format.LayerBlendMode.LinearBurn => BlendMode.LinearBurn,
        Format.LayerBlendMode.Lighten => BlendMode.Lighten,
        Format.LayerBlendMode.Screen => BlendMode.Screen,
        Format.LayerBlendMode.ColorDodge => BlendMode.ColorDodge,
        Format.LayerBlendMode.LinearDodgeAdd => BlendMode.LinearDodgeAdd,
        Format.LayerBlendMode.Overlay => BlendMode.Overlay,
        Format.LayerBlendMode.SoftLight => BlendMode.SoftLight,
        Format.LayerBlendMode.HardLight => BlendMode.HardLight,
        Format.LayerBlendMode.VividLight => BlendMode.VividLight,
        Format.LayerBlendMode.LinearLight => BlendMode.LinearLight,
        Format.LayerBlendMode.PinLight => BlendMode.PinLight,
        Format.LayerBlendMode.HardMix => BlendMode.HardMix,
        Format.LayerBlendMode.Difference => BlendMode.Difference,
        Format.LayerBlendMode.Exclusion => BlendMode.Exclusion,
        Format.LayerBlendMode.Subtract => BlendMode.Subtract,
        Format.LayerBlendMode.Divide => BlendMode.Divide,
        Format.LayerBlendMode.Hue => BlendMode.Hue,
        Format.LayerBlendMode.Saturation => BlendMode.Saturation,
        Format.LayerBlendMode.Color => BlendMode.Color,
        _ => BlendMode.Luminosity,
    };

    /// <summary>
    /// The Skia blend mode that computes this one, or null for the eight that Skia has no equivalent for.
    /// Photoshop's Normal is source-over.
    /// </summary>
    public static SKBlendMode? Skia(BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => null,
    };

    /// <summary>
    /// Composites a tile of premultiplied RGBA in a hand-rolled mode. <paramref name="source"/> and
    /// <paramref name="backdrop"/> are whole tiles of the same size; the result is written to
    /// <paramref name="destination"/>, which may alias either.
    /// </summary>
    public static void Composite(BlendMode mode, ReadOnlySpan<byte> source, ReadOnlySpan<byte> backdrop,
                                 Span<byte> destination, int width, int height, int stride)
    {
        Composite(mode, source, stride, backdrop, stride, destination, stride, width, height);
    }

    /// <summary>
    /// Composites a tile in a hand-rolled mode where the source and the destination are rows of different
    /// widths — a layer smaller than the canvas it is placed on. The source span starts at the tile's own
    /// first pixel, so its stride is its own row width.
    /// </summary>
    public static void Composite(BlendMode mode, ReadOnlySpan<byte> source, int sourceStride,
                                 ReadOnlySpan<byte> backdrop, int backdropStride,
                                 Span<byte> destination, int destinationStride, int width, int height)
    {
        if (!IsHandRolled(mode)) throw new ArgumentException($"{Name(mode)} is drawn by Skia.", nameof(mode));
        for (int y = 0; y < height; ++y)
        {
            int s = y * sourceStride, b = y * backdropStride, d = y * destinationStride;
            for (int x = 0; x < width; ++x)
            {
                CompositePixel(mode, source.Slice(s + x * 4, 4), backdrop.Slice(b + x * 4, 4),
                               destination.Slice(d + x * 4, 4));
            }
        }
    }

    /// <summary>
    /// Composites one premultiplied RGBA source pixel over a premultiplied RGBA backdrop pixel in a
    /// hand-rolled mode.
    /// <para>
    /// The blend functions below all work on <em>straight (unpremultiplied)</em> channel values in 0–1;
    /// alpha is not part of the function. They are composited with the separable formula Skia itself uses
    /// for its own separable modes:
    /// </para>
    /// <code>
    /// αr = αs + αb·(1 − αs)
    /// αr·Cr = (1 − αs)·Db + (1 − αb)·Ds + αs·αb·B(Db/αb, Ds/αs)
    /// </code>
    /// <para>
    /// where Ds and Db are the premultiplied source and backdrop channels. The first two terms need no
    /// unpremultiplying, and the third vanishes whenever either alpha is zero, so the division only
    /// happens when both pixels are visible. Alpha is written last and never blended.
    /// </para>
    /// </summary>
    public static void CompositePixel(BlendMode mode, ReadOnlySpan<byte> source, ReadOnlySpan<byte> backdrop,
                                      Span<byte> destination)
    {
        float sa = source[3] / 255.0f;
        float da = backdrop[3] / 255.0f;
        float outAlpha = sa + da * (1.0f - sa);
        if (outAlpha <= 0.0f)
        {
            destination.Slice(0, 4).Clear();
            return;
        }
        for (int c = 0; c < 3; ++c)
        {
            float sc = source[c] / 255.0f;
            float dc = backdrop[c] / 255.0f;
            float blended = sa > 0.0f && da > 0.0f
                ? BlendChannel(mode, Clamp01(dc / da), Clamp01(sc / sa))
                : 0.0f;
            float value = (1.0f - sa) * dc + (1.0f - da) * sc + sa * da * blended;
            destination[c] = (byte)MathF.Round(Clamp01(value) * 255.0f, MidpointRounding.AwayFromZero);
        }
        destination[3] = (byte)MathF.Round(outAlpha * 255.0f, MidpointRounding.AwayFromZero);
    }

    private static float Clamp01(float v) => v < 0.0f ? 0.0f : v > 1.0f ? 1.0f : v;

    /// <summary>
    /// The separable blend function B(Cb, Cs) of the eight hand-rolled modes, on straight channel values
    /// in 0–1: <paramref name="cb"/> is the backdrop, <paramref name="cs"/> the source.
    /// <para>
    /// Documented per mode:
    /// Linear Burn is Cb + Cs − 1 clamped at 0; Linear Dodge (Add) is Cb + Cs clamped at 1. Both are
    /// clamped variants of addition, so the Photoshop amounts that would leave 0–1 are reached exactly.
    /// Vivid Light is Color Burn with 2·Cs below 0.5 and Color Dodge with 2·Cs − 1 at or above it, i.e.
    /// 1 − (1 − Cb)/(2·Cs) and Cb/(2 − 2·Cs), each clamped. Linear Light is Cb + 2·Cs − 1 clamped — the
    /// same expression whether the source half is burnt or dodged. Pin Light compares instead: the darkening
    /// half is min(Cb, 2·Cs) below 0.5 and the lightening half max(Cb, 2·Cs − 1) at or above it. Hard Mix is
    /// Vivid Light thresholded at 0.5, which reduces to 0 when Cb + Cs &lt; 1 and 1 otherwise. Subtract is
    /// Cb − Cs clamped at 0; Divide is Cb/Cs clamped at 1, and an all-black blend channel divides to white
    /// as it does in Photoshop.
    /// </para>
    /// </summary>
    private static float BlendChannel(BlendMode mode, float cb, float cs) => mode switch
    {
        BlendMode.LinearBurn => Clamp01(cb + cs - 1.0f),
        BlendMode.LinearDodgeAdd => Clamp01(cb + cs),
        BlendMode.VividLight => VividLight(cb, cs),
        BlendMode.LinearLight => Clamp01(cb + 2.0f * cs - 1.0f),
        BlendMode.PinLight => cs <= 0.5f ? MathF.Min(cb, 2.0f * cs) : MathF.Max(cb, 2.0f * cs - 1.0f),
        BlendMode.HardMix => cb + cs < 1.0f ? 0.0f : 1.0f,
        BlendMode.Subtract => Clamp01(cb - cs),
        BlendMode.Divide => cs <= 0.0f ? 1.0f : Clamp01(cb / cs),
        _ => 0.0f,
    };

    // Color Burn takes (1 − Cb)/Cs and Color Dodge Cb/(1 − Cs); Vivid Light runs whoever's turn it is at
    // double strength, which is where the 2·Cs and 2 − 2·Cs come from.
    private static float VividLight(float cb, float cs)
    {
        if (cs <= 0.0f) return 0.0f;
        if (cs < 0.5f) return Clamp01(1.0f - (1.0f - cb) / (2.0f * cs));
        if (cs >= 1.0f) return 1.0f;
        return Clamp01(cb / (2.0f - 2.0f * cs));
    }
}
