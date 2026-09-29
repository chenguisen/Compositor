using SkiaSharp;

namespace Compositor.Core.IO.PSD;

/// <summary>A rectangle in Photoshop's top-left document coordinates.</summary>
internal readonly record struct PsdRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The part of this rectangle that falls inside a canvas of that size.</summary>
    public PsdRect Crop(int canvasWidth, int canvasHeight)
    {
        var left = Math.Min(canvasWidth, Math.Max(0, Left));
        var top = Math.Min(canvasHeight, Math.Max(0, Top));
        var right = Math.Max(left, Math.Min(canvasWidth, Right));
        var bottom = Math.Max(top, Math.Min(canvasHeight, Bottom));
        return new PsdRect(left, top, right, bottom);
    }
}

/// <summary>Where a sub-rectangle sits inside the rectangle it was cut from, as the channel coder needs.</summary>
internal readonly record struct PsdCrop(int X, int Y, int Width, int Height);

internal enum PsdLayerKind
{
    Raster,
    Group,
    Adjustment,
    Text,
    SmartObject,
    Effects,
    Vector,
}

/// <summary>One layer record plus its decoded pixels.</summary>
internal sealed class PsdRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "Layer";
    public bool IsGroup { get; set; }
    public bool IsVisible { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public string BlendKey { get; set; } = "norm";
    public bool Clipping { get; set; }
    public bool CroppedToCanvas { get; set; }
    public PsdLayerKind Kind { get; set; } = PsdLayerKind.Raster;

    /// <summary>Parsed adjustment settings, when the layer's blocks map onto one.</summary>
    public Format.LayerAdjustment? Adjustment { get; set; }

    public PsdRect Bounds { get; set; }
    public SKBitmap? Image { get; set; }
    public SKBitmap? Mask { get; set; }
    public PsdRect MaskBounds { get; set; }

    /// <summary>Where the mask sits on the document, and the value everywhere outside it.</summary>
    public byte MaskDefault { get; set; } = 255;

    public bool MaskEnabled { get; set; } = true;
    public bool MaskLinked { get; set; } = true;
}

/// <summary>A parsed Photoshop file: its canvas, and its layers bottom to top with folders among them.</summary>
internal sealed class PsdDocument
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Resolution { get; set; } = 72;
    public List<PsdRecord> Layers { get; } = [];
}

/// <summary>Photoshop's blend keys onto Compositor's blend modes.</summary>
internal static class PsdBlendMode
{
    public static Format.LayerBlendMode? From(string key) => key switch
    {
        "norm" => Format.LayerBlendMode.Normal,
        "mul " => Format.LayerBlendMode.Multiply,
        "scrn" => Format.LayerBlendMode.Screen,
        "over" => Format.LayerBlendMode.Overlay,
        "sLit" => Format.LayerBlendMode.SoftLight,
        "dark" => Format.LayerBlendMode.Darken,
        "lite" => Format.LayerBlendMode.Lighten,
        "diff" => Format.LayerBlendMode.Difference,
        "div " => Format.LayerBlendMode.ColorDodge,
        "idiv" => Format.LayerBlendMode.ColorBurn,
        "hue " => Format.LayerBlendMode.Hue,
        "sat " => Format.LayerBlendMode.Saturation,
        "colr" => Format.LayerBlendMode.Color,
        "lum " => Format.LayerBlendMode.Luminosity,
        "lbrn" => Format.LayerBlendMode.LinearBurn,
        // Photoshop's "Linear Dodge" is Compositor's "Linear Dodge (Add)".
        "lddg" => Format.LayerBlendMode.LinearDodgeAdd,
        "hLit" => Format.LayerBlendMode.HardLight,
        "vLit" => Format.LayerBlendMode.VividLight,
        "lLit" => Format.LayerBlendMode.LinearLight,
        "pLit" => Format.LayerBlendMode.PinLight,
        "hMix" => Format.LayerBlendMode.HardMix,
        "smud" => Format.LayerBlendMode.Exclusion,
        "fsub" => Format.LayerBlendMode.Subtract,
        "fdiv" => Format.LayerBlendMode.Divide,
        // Dissolve, Darker Color and Lighter Color are deliberately absent: Compositor has no equivalent,
        // so they fall through to Normal and say so in the conversion report.
        _ => null,
    };

    /// <summary>How a key reads in the report: unfilled keys are padded with spaces.</summary>
    public static string Display(string key) => key.TrimEnd(' ');
}
