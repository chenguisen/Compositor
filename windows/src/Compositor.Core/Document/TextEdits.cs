using System.Collections.Concurrent;
using System.Globalization;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Laying out and drawing a text layer's pixels: the style is the truth and the PNG is what it looks like,
/// so an edit re-rasterises from the style rather than painting over the last picture.
/// <para>
/// Each letter is placed by hand, one at a time, which is what gives every letter its own face and colour
/// and lets the tracking be added exactly. The cost is that scripts whose glyphs change shape or order by
/// context — Arabic, Indic — do not shape the way Core Text shapes them in the Mac build; Latin, Greek,
/// Cyrillic and CJK, which have no such behaviour, come out the same.
/// </para>
/// </summary>
public static class TextEdits
{
    /// <summary>The room left around the text inside its layer, as the Mac build leaves it.</summary>
    public const double Padding = 12;

    /// <summary>The smallest a text layer may be, so an empty line still has somewhere to put a caret.</summary>
    public const int LeastSide = 16;

    private static readonly ConcurrentDictionary<string, SKTypeface> Faces = new();

    /// <summary>A text layer's name: its first words on one line, so a paragraph does not make the row tall.</summary>
    public static string LayerName(string content)
    {
        var flattened = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length == 0 ? "Text" : flattened[..Math.Min(40, flattened.Length)];
    }

    /// <summary>How big a text layer's pixels are: the paragraph's own box, or what point text measures.</summary>
    public static (int Width, int Height) BoxSize(LayerTextStyle style)
    {
        if (style.BoxSize is { } box)
        {
            return (Math.Max(LeastSide, (int)Math.Ceiling(box.Width)), Math.Max(LeastSide, (int)Math.Ceiling(box.Height)));
        }
        Layout(style, out var measuredWidth, out var measuredHeight);
        var width = Math.Max(LeastSide, (int)Math.Ceiling(measuredWidth + Padding * 2 + style.FontSize * 0.1));
        var height = Math.Max(LeastSide, (int)Math.Ceiling(Math.Max(measuredHeight, Math.Ceiling(style.LineHeight)) + Padding * 2));
        return (width, height);
    }

    /// <summary>The text drawn: straight alpha sRGB, the format every layer's pixels are held in.</summary>
    public static SKBitmap? Image(LayerTextStyle style)
    {
        if (!style.IsValid) return null;
        var (width, height) = BoxSize(style);
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return null;
        }
        var pieces = Layout(style, out _, out _);
        var image = new SKBitmap(Bitmaps.ColorInfo(width, height));
        image.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var piece in pieces)
        {
            paint.Color = piece.Colour;
            var font = new SKFont(piece.Typeface, (float)style.FontSize);
            canvas.DrawText(piece.Text, piece.X, piece.Y, SKTextAlign.Left, font, paint);
        }
        return image;
    }

    /// <summary>
    /// A new text layer at <paramref name="origin"/>, holding the text and the style that drew it. The
    /// pixels and the style share one bitmap, which is what makes the layer still a live text layer.
    /// </summary>
    public static Guid? Add(CanvasDocument document, LayerTextStyle style, SKPoint origin)
    {
        if (document.Layers.Count >= LayerPlacement.MaxLayers) return null;
        if (Image(style) is not { } image) return null;
        var name = LayerName(style.Content);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(image, name),
            new Model.LayerTransform(origin.X, origin.Y, image.Width, image.Height), name)
        {
            ParentID = null,
            Text = new LayerText(style, image),
        };
        document.Layers.Add(layer);
        return layer.ID;
    }

    /// <summary>Changes a text layer's style and draws it again, which is how editing live text works.</summary>
    public static bool SetStyle(CanvasDocument document, Guid layerID, LayerTextStyle style)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return false;
        if (Image(style) is not { } image) return false;
        // The box may have changed with the text, so the layer is re-placed about the middle it had.
        var centreX = layer.Transform.CenterX;
        var centreY = layer.Transform.CenterY;
        var keepsItsPlace = layer.Text is not null;
        var name = LayerName(style.Content);
        layer.Asset = ImportedImage.Create(image, name);
        layer.Text = new LayerText(style, image);
        layer.Name = name;
        layer.Transform = keepsItsPlace
            ? layer.Transform with
            {
                Width = image.Width,
                Height = image.Height,
                X = centreX - image.Width / 2.0,
                Y = centreY - image.Height / 2.0,
            }
            : new Model.LayerTransform(layer.Transform.X, layer.Transform.Y, image.Width, image.Height);
        return true;
    }

    /// <summary>
    /// Where the caret sits when the text ends: after the last character of the last line, and on the first
    /// line's baseline when there is no text yet. Relative to the layer's own top left, in layer pixels.
    /// </summary>
    public static SKPoint Caret(LayerTextStyle style)
    {
        var pieces = Layout(style, out _, out _);
        if (pieces.Count == 0) return new SKPoint((float)Padding, (float)(Padding + Baseline(style)));
        var last = pieces[^1];
        return new SKPoint(last.X + (float)Advance(last.Text, last.Typeface, style), last.Y);
    }

    /// <summary>How far below the top of a line its writing sits.</summary>
    private static double Baseline(LayerTextStyle style)
    {
        var metrics = Metrics(style.FontName, style.FontSize);
        var ascent = -metrics.Ascent;
        var descent = metrics.Descent;
        return (style.LineHeight - (ascent + descent)) / 2 + ascent;
    }

    /// <summary>What a text layer needs to be drawn: its face, its size and the leading between lines.</summary>
    public static SKFontMetrics Metrics(string fontName, double size) =>
        new SKFont(Typeface(fontName), (float)size).Metrics;

    /// <summary>The face to set text in, falling back to the system face as the Mac build falls back.</summary>
    public static SKTypeface Typeface(string? fontName) =>
        Faces.GetOrAdd(fontName ?? "", name => name.Length > 0
            ? SKTypeface.FromFamilyName(name) ?? SKTypeface.Default
            : SKTypeface.Default);

    /// <summary>One character to draw, with the face and colour it is set in and where it goes.</summary>
    private readonly record struct Piece(string Text, SKTypeface Typeface, SKColor Colour, float X, float Y);

    /// <summary>
    /// The text laid out: every character with its place, and how big the whole paragraph came out. Lines
    /// break where the content says so, and, in a paragraph box, wherever the next word would not fit.
    /// </summary>
    private static List<Piece> Layout(LayerTextStyle style, out double measuredWidth, out double measuredHeight)
    {
        var pieces = new List<Piece>();
        var content = style.Content ?? "";
        var colours = Colours(style, content.Length);
        var fonts = Fonts(style, content.Length);
        var lineHeight = style.LineHeight;
        // Where the lines have to fit: the paragraph box less its padding, or no limit at all for point text.
        var limit = style.BoxSize is { } box ? Math.Max(1, box.Width - Padding * 2) : double.PositiveInfinity;
        var baseline = Baseline(style);

        measuredWidth = 0;
        var line = 0;
        foreach (var (start, length) in Lines(content))
        {
            foreach (var (from, to) in Wrap(content, start, length, style, fonts, colours, limit))
            {
                var width = 0.0;
                var index = from;
                var first = pieces.Count;
                while (index < to)
                {
                    var text = NextElement(content, index, to, out var taken);
                    var typeface = fonts[index];
                    pieces.Add(new Piece(text, typeface, colours[index], (float)width,
                        (float)(Padding + line * lineHeight + baseline)));
                    width += Advance(text, typeface, style);
                    index += taken;
                }
                // The line is aligned inside the paragraph's box, which point text does not have: its box
                // is what it measures, so there is nothing to shift within.
                var offset = AlignmentOffset(style.Alignment, limit, width);
                for (var at = first; at < pieces.Count; at++)
                {
                    pieces[at] = pieces[at] with { X = pieces[at].X + (float)(Padding + offset) };
                }
                measuredWidth = Math.Max(measuredWidth, width);
                line++;
            }
        }
        measuredHeight = line * lineHeight;
        return pieces;
    }

    /// <summary>How far a line starts from the left of its box: nothing, or what centring or righting leaves.</summary>
    private static double AlignmentOffset(TextAlignment alignment, double container, double width)
    {
        if (double.IsPositiveInfinity(container)) return 0;
        return alignment switch
        {
            TextAlignment.Center => (container - width) / 2,
            TextAlignment.Right => container - width,
            _ => 0,
        };
    }

    /// <summary>The advance of one character: what it measures, plus the tracking, as Core Text adds it.</summary>
    private static double Advance(string text, SKTypeface typeface, LayerTextStyle style)
    {
        var font = new SKFont(typeface, (float)style.FontSize);
        return font.MeasureText(text) + style.Tracking;
    }

    /// <summary>The lines of the content, as (start, length) into it. A break is a newline, in any of its forms.</summary>
    private static IEnumerable<(int Start, int Length)> Lines(string content)
    {
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character is not ('\n' or '\r' or '\u000b' or '\u000c' or '\u0085' or '\u2028' or '\u2029')) continue;
            yield return (start, index - start);
            // A carriage return followed by a line feed is one break, not two.
            if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
            start = index + 1;
        }
        yield return (start, content.Length - start);
    }

    /// <summary>
    /// The spans of one line that fit: the whole line for point text, or as many words as the box holds. A
    /// word wider than the box is left on a line of its own rather than being split.
    /// </summary>
    private static IEnumerable<(int From, int To)> Wrap(string content, int start, int length,
        LayerTextStyle style, List<SKTypeface> fonts, List<SKColor> colours, double limit)
    {
        if (double.IsPositiveInfinity(limit) || length == 0)
        {
            yield return (start, start + length);
            yield break;
        }
        var from = start;
        var at = start;
        while (at < start + length)
        {
            // Take a word and the spaces that follow it, so the break falls between words.
            var wordEnd = at;
            while (wordEnd < start + length && char.IsWhiteSpace(content[wordEnd])) wordEnd++;
            while (wordEnd < start + length && !char.IsWhiteSpace(content[wordEnd])) wordEnd++;
            var end = wordEnd;
            while (end < start + length && content[end] is not ('\n' or '\r')) end++;
            var width = Width(content, from, end, style, fonts);
            if (width > limit && from < at)
            {
                // This word does not fit after what is already on the line: break before it.
                yield return (from, at);
                from = at;
                at = wordEnd;
                continue;
            }
            at = wordEnd;
        }
        yield return (from, start + length);
    }

    private static double Width(string content, int from, int to, LayerTextStyle style, List<SKTypeface> fonts)
    {
        var width = 0.0;
        var index = from;
        while (index < to)
        {
            var text = NextElement(content, index, to, out var taken);
            width += Advance(text, fonts[index], style);
            index += taken;
        }
        return width;
    }

    /// <summary>One character out of the content: a whole surrogate pair where there is one.</summary>
    private static string NextElement(string content, int index, int limit, out int taken)
    {
        var length = char.IsHighSurrogate(content[index]) && index + 1 < limit && char.IsLowSurrogate(content[index + 1]) ? 2 : 1;
        taken = length;
        return content.Substring(index, length);
    }

    /// <summary>Each character's colour: the style's, unless a colour run covers it.</summary>
    private static List<SKColor> Colours(LayerTextStyle style, int length)
    {
        var colour = ToColour(style.Red, style.Green, style.Blue);
        var colours = new List<SKColor>(length);
        for (var index = 0; index < length; index++) colours.Add(colour);
        foreach (var run in style.ColorRuns ?? [])
        {
            if (!Covers(run.Location, run.Length, length)) continue;
            var set = ToColour(run.Red, run.Green, run.Blue);
            for (var index = run.Location; index < run.Location + run.Length; index++) colours[index] = set;
        }
        return colours;
    }

    /// <summary>Each character's face: the style's, unless a font run covers it.</summary>
    private static List<SKTypeface> Fonts(LayerTextStyle style, int length)
    {
        var font = Typeface(style.FontName);
        var fonts = new List<SKTypeface>(length);
        for (var index = 0; index < length; index++) fonts.Add(font);
        foreach (var run in style.FontRuns ?? [])
        {
            if (!Covers(run.Location, run.Length, length)) continue;
            var set = Typeface(run.FontName);
            for (var index = run.Location; index < run.Location + run.Length; index++) fonts[index] = set;
        }
        return fonts;
    }

    private static bool Covers(int location, int length, int total) =>
        length > 0 && location >= 0 && location <= total - length;

    private static SKColor ToColour(double red, double green, double blue) => new(
        (byte)Math.Clamp(Math.Round(red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(blue * 255), 0, 255));
}
