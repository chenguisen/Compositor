using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Desktop;

/// <summary>
/// What the tool options bar holds: the settings the tool in hand reads. The window owns one of these and the
/// bar is a face on it — the bar writes a setting and asks for the change, the window reads the setting where
/// it already did. Keeping them in one object is what lets the bar be built once rather than rebuilt per tool.
/// </summary>
internal sealed class ToolOptions
{
    /// <summary>The brush family's settings: size, hardness, opacity, colour and the rest.</summary>
    public BrushSettings Brush = new();

    /// <summary>Whether a stroke goes on the layer's mask, and whether it paints or erases.</summary>
    public bool PaintOnMask;
    public bool Erase;


    /// <summary>The magic wand's own amounts, and whether it reads every visible layer.</summary>
    public WandOptions Wand = new();
    public bool WandAllLayers;

    /// <summary>How the Gradient tool paints.</summary>
    public GradientShape Gradient = GradientShape.Linear;
    public bool GradientToBackground;
    public bool GradientReversed;
    public (double Red, double Green, double Blue) GradientBackground = (1, 1, 1);

    /// <summary>Which shape the Shape tool draws, and its two sizes.</summary>
    public ShapeKind Shape = ShapeKind.Rectangle;
    public double ShapeCornerRadius;
    public double ShapeLineWidth = 4;
}
