using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// What an edit is allowed to touch: a rectangle of the document, or all of it. The Mac build holds a path
/// with a feather and a coverage mask; this holds the one shape the marquee tool draws so far, which is
/// exact and costs nothing to keep. Lasso, wand, feather, and adding to and subtracting from a selection
/// all need that coverage model and are not here.
/// </summary>
public sealed class DocumentSelection
{
    private DocumentSelection(SKRectI? rect) => Rect = rect;

    /// <summary>The selected rectangle, or null for the whole document.</summary>
    public SKRectI? Rect { get; }

    /// <summary>Nothing is selected, so an edit may touch the whole document.</summary>
    public static DocumentSelection All { get; } = new(null);

    /// <summary>A rectangle of the document, held to its bounds. An empty rectangle is no selection.</summary>
    public static DocumentSelection Rectangular(SKRectI rect, int width, int height)
    {
        var held = SKRectI.Intersect(rect, SKRectI.Create(0, 0, width, height));
        return held.Width <= 0 || held.Height <= 0 ? All : new DocumentSelection(held);
    }

    /// <summary>Whether a whole pixel is inside the selection.</summary>
    public bool Contains(int x, int y) =>
        Rect is not { } rect || (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom);
}
