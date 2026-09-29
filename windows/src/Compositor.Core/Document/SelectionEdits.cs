using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Choosing what later edits act on. A selection is part of the document, so undo and redo cover changing
/// it, though it is not saved to disk.
/// </summary>
public static class SelectionEdits
{
    /// <summary>
    /// Selects the whole canvas. It is not the same as having no selection: an edit may touch everything
    /// either way, but this one is a selection the tool shows, so a crop or a fill can be aimed at it.
    /// </summary>
    public static bool SelectAll(CanvasDocument document)
    {
        var whole = SKRectI.Create(0, 0, document.Width, document.Height);
        if (document.Selection.Rect == whole) return false;
        document.Selection = DocumentSelection.Rectangular(whole, document.Width, document.Height);
        return true;
    }

    /// <summary>Leaves nothing selected, so edits act on the whole document again.</summary>
    public static bool Deselect(CanvasDocument document)
    {
        if (document.Selection.Rect is null) return false;
        document.Selection = DocumentSelection.All;
        return true;
    }

    /// <summary>Selects a rectangle of the document, held to its bounds.</summary>
    public static bool Select(CanvasDocument document, SKRectI rect)
    {
        var selection = DocumentSelection.Rectangular(rect, document.Width, document.Height);
        if (Same(document.Selection, selection)) return false;
        document.Selection = selection;
        return true;
    }

    private static bool Same(DocumentSelection left, DocumentSelection right) => left.Rect == right.Rect;
}
