namespace Compositor.Desktop;

/// <summary>
/// Which pointer tool is in hand. The rail draws one button per tool and the Tools menu has a row for each, so
/// the two stay in step by both being built from this list.
/// </summary>
internal enum Tool
{
    Pan,
    Move,
    Marquee,
    Ellipse,
    Lasso,
    Polygon,
    Wand,
    Brush,
    Clone,
    Blur,
    Liquify,
    Smudge,
    Heal,
    Eyedropper,
    Type,
    Crop,
    Shape,
    Gradient,
}

/// <summary>Which of the brush's amounts was asked for, by the Tools menu or the options bar.</summary>
internal enum BrushSetting
{
    Size,
    Hardness,
    Opacity,
    Colour,
}
