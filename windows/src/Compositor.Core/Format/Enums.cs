using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>Layer blending, spelled exactly as the manifest spells it.</summary>
public enum LayerBlendMode
{
    Normal,
    Darken,
    Multiply,
    [JsonStringEnumMemberName("Color Burn")] ColorBurn,
    [JsonStringEnumMemberName("Linear Burn")] LinearBurn,
    Lighten,
    Screen,
    [JsonStringEnumMemberName("Color Dodge")] ColorDodge,
    [JsonStringEnumMemberName("Linear Dodge (Add)")] LinearDodgeAdd,
    Overlay,
    [JsonStringEnumMemberName("Soft Light")] SoftLight,
    [JsonStringEnumMemberName("Hard Light")] HardLight,
    [JsonStringEnumMemberName("Vivid Light")] VividLight,
    [JsonStringEnumMemberName("Linear Light")] LinearLight,
    [JsonStringEnumMemberName("Pin Light")] PinLight,
    [JsonStringEnumMemberName("Hard Mix")] HardMix,
    Difference,
    Exclusion,
    Subtract,
    Divide,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

public enum LayerSampling
{
    Nearest,
    Smooth,
    [JsonStringEnumMemberName("High quality")] HighQuality,
}

/// <summary>Adjustment layer kinds; the last three sample neighboring pixels and need format version 9.</summary>
public enum AdjustmentKind
{
    [JsonStringEnumMemberName("Hue/Saturation")] HueSaturation,
    Levels,
    Curves,
    Exposure,
    [JsonStringEnumMemberName("Gradient Map")] GradientMap,
    Grain,
    [JsonStringEnumMemberName("Add Noise")] AddNoise,
    [JsonStringEnumMemberName("Gaussian Blur")] GaussianBlur,
    [JsonStringEnumMemberName("Motion Blur")] MotionBlur,
    Invert,
    [JsonStringEnumMemberName("Black & White")] BlackWhite,
    [JsonStringEnumMemberName("Color Balance")] ColorBalance,
}

public enum LevelsChannel
{
    [JsonStringEnumMemberName("RGB")] Rgb,
    Red,
    Green,
    Blue,
}

public enum ColorRange
{
    Master,
    Reds,
    Yellows,
    Greens,
    Cyans,
    Blues,
    Magentas,
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
}

/// <summary>Guide axis raw values are lowercase, unlike the rest of the format.</summary>
public enum GuideAxis
{
    [JsonStringEnumMemberName("horizontal")] Horizontal,
    [JsonStringEnumMemberName("vertical")] Vertical,
}
