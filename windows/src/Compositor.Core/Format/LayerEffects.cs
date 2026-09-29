namespace Compositor.Core.Format;

/// <summary>Which of the effects a layer may draw around itself, as the Effects menu lists them.</summary>
public enum EffectKind
{
    Stroke,
    DropShadow,
    ColorOverlay,
    InnerShadow,
    OuterGlow,
    InnerGlow,
}

/// <summary>A line drawn around what the layer shows, outside its edge or inside it.</summary>
public sealed class StrokeEffect
{
    public const double MaxSize = 500;

    /// <summary>Missing in older projects means visible.</summary>
    public bool? Enabled { get; set; }

    public double Size { get; set; } = 4;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 1;
    public bool Inside { get; set; }

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Size) && Size is >= 0 and <= MaxSize
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && Color.IsValid(Red, Green, Blue);
}

/// <summary>The layer's shape repeated behind it, offset and softened.</summary>
public sealed class ShadowEffect
{
    public bool? Enabled { get; set; }

    /// <summary>
    /// Where the light comes from, in degrees counterclockwise from the right: 90 is from straight above,
    /// which drops the shadow straight down.
    /// </summary>
    public double Angle { get; set; } = 90;

    public double Distance { get; set; } = 20;
    public double Blur { get; set; } = 20;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 0.5;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Angle) && Angle is >= -360 and <= 360
        && double.IsFinite(Distance) && Distance is >= 0 and <= 5000
        && double.IsFinite(Blur) && Blur is >= 0 and <= 500
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && Color.IsValid(Red, Green, Blue);
}

/// <summary>A flat color over everything the layer shows.</summary>
public sealed class ColorOverlayEffect
{
    public bool? Enabled { get; set; }
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 1;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Opacity) && Opacity is >= 0 and <= 1 && Color.IsValid(Red, Green, Blue);
}

/// <summary>A shadow cast inside the layer's own edges, as though it were cut out of what is behind it.</summary>
public sealed class InnerShadowEffect
{
    public bool? Enabled { get; set; }
    public double Angle { get; set; } = 90;
    public double Distance { get; set; } = 10;
    public double Blur { get; set; } = 10;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 0.5;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Angle) && Angle is >= -360 and <= 360
        && double.IsFinite(Distance) && Distance is >= 0 and <= 5000
        && double.IsFinite(Blur) && Blur is >= 0 and <= 500
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && Color.IsValid(Red, Green, Blue);
}

/// <summary>A soft glow drawn omnidirectionally around the outside of what the layer shows.</summary>
public sealed class OuterGlowEffect
{
    public bool? Enabled { get; set; }
    public double Size { get; set; } = 20;
    public double Red { get; set; } = 1;
    public double Green { get; set; } = 1;
    public double Blue { get; set; } = 1;
    public double Opacity { get; set; } = 0.75;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Size) && Size is >= 0 and <= 500
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && Color.IsValid(Red, Green, Blue);
}

/// <summary>A glow cast inside the layer's own edges, emanating inward from its boundary.</summary>
public sealed class InnerGlowEffect
{
    public bool? Enabled { get; set; }
    public double Size { get; set; } = 10;
    public double Red { get; set; } = 1;
    public double Green { get; set; } = 1;
    public double Blue { get; set; } = 1;
    public double Opacity { get; set; } = 0.75;

    public bool IsEnabled => Enabled ?? true;

    public bool IsValid =>
        double.IsFinite(Size) && Size is >= 0 and <= 500
        && double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && Color.IsValid(Red, Green, Blue);
}

/// <summary>
/// What a layer draws around itself. Kept with the layer, so it follows every edit. A record omitting an
/// effect means the layer does not have it, so older readers see the effects they understand.
/// </summary>
public sealed class LayerEffects
{
    public StrokeEffect? Stroke { get; set; }
    public ShadowEffect? Shadow { get; set; }
    public ColorOverlayEffect? ColorOverlay { get; set; }
    public InnerShadowEffect? InnerShadow { get; set; }
    public OuterGlowEffect? OuterGlow { get; set; }
    public InnerGlowEffect? InnerGlow { get; set; }

    public bool IsEmpty =>
        Stroke is null && Shadow is null && ColorOverlay is null && InnerShadow is null && OuterGlow is null && InnerGlow is null;

    public bool IsValid =>
        (Stroke?.IsValid ?? true) && (Shadow?.IsValid ?? true) && (ColorOverlay?.IsValid ?? true)
        && (InnerShadow?.IsValid ?? true) && (OuterGlow?.IsValid ?? true) && (InnerGlow?.IsValid ?? true);
}
