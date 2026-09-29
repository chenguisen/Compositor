using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Text being typed on the canvas: the words so far, the layer they are drawn on, and what that layer was
/// before. The layer is made when the first character arrives and drawn again on every letter after it, so
/// what is on the canvas is always what will be saved; the whole session is one change to the document, and
/// letting it go puts the layer back the way it was.
/// </summary>
public sealed class TextSession
{
    private readonly LayerTextStyle? _original;
    private readonly SKPoint _origin;
    private LayerTextStyle _style;

    private TextSession(LayerTextStyle style, LayerTextStyle? original, Guid? layer, SKPoint origin)
    {
        _style = style;
        _original = original;
        _origin = origin;
        LayerID = layer;
    }

    /// <summary>The layer the words are on, once they have one: a session with no characters has no layer.</summary>
    public Guid? LayerID { get; private set; }

    /// <summary>What has been typed so far.</summary>
    public string Content => _style.Content;

    /// <summary>The style the words are being drawn with, as they stand.</summary>
    public LayerTextStyle Style => _style;

    /// <summary>Text that starts where it is clicked. Nothing is added to the document until a character is.</summary>
    public static TextSession New(LayerTextStyle style, SKPoint origin) =>
        new(Copy(style), null, null, origin);

    /// <summary>
    /// More words on a layer that is already text, from where the layer is. A copy of its style is taken, so
    /// that letting the session go can put the original back.
    /// </summary>
    public static TextSession Editing(ImageLayer layer)
    {
        if (layer.Text is null) throw new ArgumentException("That layer is not text.", nameof(layer));
        var style = Copy(layer.Text.Style);
        return new TextSession(style, Copy(layer.Text.Style), layer.ID,
            new SKPoint((float)layer.Transform.X, (float)layer.Transform.Y));
    }

    /// <summary>
    /// Adds what was typed — a character, or a newline — and draws the layer again. False when the text
    /// cannot be drawn at all, which leaves the session as it was.
    /// </summary>
    public bool Type(CanvasDocument document, string typed)
    {
        var next = Copy(_style);
        next.Content += typed;
        var before = _style;
        _style = next;
        if (Draw(document)) return true;
        _style = before;
        return false;
    }

    /// <summary>The delete key: the last character goes, which for a surrogate pair is both of its halves.</summary>
    public bool Backspace(CanvasDocument document)
    {
        if (_style.Content.Length == 0) return false;
        var length = char.IsLowSurrogate(_style.Content[^1]) && _style.Content.Length >= 2
            && char.IsHighSurrogate(_style.Content[^2]) ? 2 : 1;
        var next = Copy(_style);
        next.Content = next.Content[..^length];
        var before = _style;
        _style = next;
        if (Draw(document)) return true;
        _style = before;
        return false;
    }

    /// <summary>
    /// Keeps the words and answers with the layer that stayed: null when there was nothing worth keeping, in
    /// which case the session lets go of what it started.
    /// </summary>
    public Guid? Commit(CanvasDocument document)
    {
        if (LayerID is { } id && !string.IsNullOrWhiteSpace(_style.Content)) return id;
        Cancel(document);
        return null;
    }

    /// <summary>
    /// Lets the words go: a layer that was only being started is taken away, and one that was already there
    /// gets its old words back. False when there is nothing to put back.
    /// </summary>
    public bool Cancel(CanvasDocument document)
    {
        if (LayerID is not { } id) return false;
        if (_original is null) return LayerEdits.Delete(document, id);
        return TextEdits.SetStyle(document, id, _original);
    }

    /// <summary>The rectangle the text occupies on the document, whether it has a layer yet or not.</summary>
    public SKRectI Box(CanvasDocument document)
    {
        if (LayerID is { } id && document.Layers.FirstOrDefault(layer => layer.ID == id) is { } layer)
        {
            return SKRectI.Create((int)layer.Transform.X, (int)layer.Transform.Y,
                Math.Max(1, (int)layer.Transform.Width), Math.Max(1, (int)layer.Transform.Height));
        }
        var (width, height) = TextEdits.BoxSize(_style);
        return SKRectI.Create((int)_origin.X, (int)_origin.Y, width, height);
    }

    /// <summary>Whether a document point is inside the text, so a click there stays in the session.</summary>
    public bool Contains(CanvasDocument document, SKPoint point) =>
        Box(document).Contains(new SKPointI((int)Math.Floor(point.X), (int)Math.Floor(point.Y)));

    /// <summary>The caret, in document pixels: a thin box where the text ends, as tall as the type.</summary>
    public SKRect Caret(CanvasDocument document)
    {
        var box = Box(document);
        var caret = TextEdits.Caret(_style);
        var size = (float)_style.FontSize;
        return SKRect.Create(box.Left + caret.X, box.Top + caret.Y - size * 0.8f, 2, size);
    }

    /// <summary>Draws the words: a new layer on the first character, and the same layer drawn again after.</summary>
    private bool Draw(CanvasDocument document)
    {
        if (LayerID is { } id && document.Layers.Any(layer => layer.ID == id))
        {
            return TextEdits.SetStyle(document, id, _style);
        }
        if (_style.Content.Length == 0) return true;
        LayerID = TextEdits.Add(document, _style, _origin);
        return LayerID is not null;
    }

    /// <summary>A copy, so that editing a session does not change the layer it came from.</summary>
    private static LayerTextStyle Copy(LayerTextStyle style) => new()
    {
        Content = style.Content,
        FontName = style.FontName,
        FontSize = style.FontSize,
        Red = style.Red,
        Green = style.Green,
        Blue = style.Blue,
        Alignment = style.Alignment,
        Tracking = style.Tracking,
        Leading = style.Leading,
        BoxSize = style.BoxSize,
        ColorRuns = style.ColorRuns,
        FontRuns = style.FontRuns,
    };
}
