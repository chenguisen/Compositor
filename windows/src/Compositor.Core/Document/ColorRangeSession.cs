using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Select ▸ Colour Range while its panel is open, as the Mac build keeps it: the colours picked off the
/// picture, how near a colour counts, whether the selection is turned over — and the selection itself, built
/// from those on the document again every time one of them changes. The panel that drives this lives in the
/// window; what a selection ends up being lives here, where a test can hold it to the Mac's own.
/// </summary>
public sealed class ColorRangeSession : IDisposable
{
    /// <summary>What a pick does with the colour it found: starts the range over from it, joins it, or leaves it out.</summary>
    public enum Picking
    {
        Replace,
        Add,
        Remove,
    }

    /// <summary>The panel's preview fits in this, in points, which is the Mac sheet's own size.</summary>
    public const int MaskWidth = 292;
    public const int MaskHeight = 200;

    /// <summary>The picture the colours are matched against: the canvas as it is shown, taken once.</summary>
    private readonly SKBitmap _sample;

    private readonly List<SKColor> _include = [];
    private readonly List<SKColor> _exclude = [];

    private ColorRangeSession(SKBitmap sample) => _sample = sample;

    /// <summary>What the next click on the picture does with the colour under it.</summary>
    public Picking Mode { get; set; } = Picking.Replace;

    /// <summary>How far a colour may be from the picked ones and still be selected, 0 to 200.</summary>
    public double Fuzziness { get; set; } = 40;

    /// <summary>Whether the selection is everything except those colours, as a green screen is.</summary>
    public bool Invert { get; set; }

    /// <summary>The colours picked so far, and the ones taken away.</summary>
    public IReadOnlyList<SKColor> Include => _include;

    public IReadOnlyList<SKColor> Exclude => _exclude;

    /// <summary>Whether any colour at all has been picked, which is what the panel's hint follows.</summary>
    public bool HasColours => _include.Count > 0;

    /// <summary>What the last rebuild made of it, or null when there was nothing to say.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Begins a colour range on the document's picture — the canvas as shown, as the Mac build's is, so a
    /// colour is matched wherever it appears and not only where it runs together. Null when there is nothing
    /// to sample.
    /// </summary>
    public static ColorRangeSession? Begin(CanvasDocument document)
    {
        if (document.Width <= 0 || document.Height <= 0) return null;
        return new ColorRangeSession(DocumentRenderer.Render(document));
    }

    /// <summary>
    /// A colour picked off the picture. The mode says whether it starts the range over, joins it, or is left
    /// out; the selection is then built again from every colour picked. False when there is no such colour in
    /// the picture — the panel says so rather than showing an empty selection as if it had worked.
    /// </summary>
    public bool Pick(CanvasDocument document, SKColor colour, Picking mode)
    {
        switch (mode)
        {
            case Picking.Replace:
                _include.Clear();
                _exclude.Clear();
                _include.Add(colour);
                break;
            case Picking.Add:
                _include.Add(colour);
                break;
            default:
                _exclude.Add(colour);
                break;
        }
        return Rebuild(document);
    }

    /// <summary>
    /// The selection from the colours picked so far, which is what the panel's own preview is drawn from as
    /// well. False when nothing in the picture is that colour, which leaves the selection as it was.
    /// </summary>
    public bool Rebuild(CanvasDocument document)
    {
        if (!HasColours)
        {
            Problem = null;
            return false;
        }
        var held = document.Selection;
        SelectionEdits.SelectColorRange(document, _sample, _include, _exclude, (int)Math.Round(Fuzziness),
            Invert, SelectionMode.Replace);
        // A Replace that matches nothing leaves no outline at all, which is this panel's refusal: it says so and
        // the selection that was there stays, rather than an empty one appearing as if the pick had worked. A
        // pick that lands on what is already selected is no refusal — the outline is there either way, and the
        // edit that reports "changed nothing" is not the same thing as a colour the picture does not hold.
        if (document.Selection.Path is null)
        {
            document.Selection = held;
            Problem = "Nothing in the picture is that color";
            return false;
        }
        Problem = null;
        return true;
    }

    /// <summary>
    /// The selection as the panel shows it: white where selected, black where not, shaped like the canvas and
    /// small enough for the panel. Drawn from the outline at that size rather than from a whole-document mask,
    /// so a large canvas costs no more than the panel does.
    /// </summary>
    public SKBitmap? Mask(CanvasDocument document)
    {
        // Nothing is shown until a colour has been picked, as the Mac's panel shows nothing: what the panel
        // draws is the range this session has picked, not whatever selection the document happens to hold.
        if (!HasColours) return null;
        if (document.Selection.Path is not { } path || document.Selection.IsEmpty) return null;
        if (document.Width <= 0 || document.Height <= 0) return null;
        var scale = Math.Min((float)MaskWidth / document.Width, (float)MaskHeight / document.Height);
        var width = Math.Max(1, (int)Math.Round(document.Width * scale));
        var height = Math.Max(1, (int)Math.Round(document.Height * scale));
        var mask = new SKBitmap(Bitmaps.MaskInfo(width, height));
        mask.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(mask))
        {
            canvas.Scale(scale);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawPath(path, paint);
        }
        return mask;
    }

    public void Dispose() => _sample.Dispose();
}
