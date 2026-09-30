using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Compositor.Desktop;

/// <summary>
/// The colours the interface is drawn in, taken from the Mac build rather than invented: every one names the
/// Swift or AppKit value it comes from, so a change here can be checked against Compositor/*.swift. The Mac
/// build asks for the dark appearance (<c>.preferredColorScheme(.dark)</c> in ContentView), so this port does
/// too, and Fluent's own dark palette is then moved onto the Mac's greys and the Mac's blue where it differs.
/// </summary>
internal static class Skin
{
    // ---- What the Mac build writes down ----

    /// <summary><c>Color(white: 0.14)</c>: the editor's own background in ContentView.</summary>
    public static readonly Color Chrome = Color.FromRgb(0x24, 0x24, 0x24);

    /// <summary><c>NSColor(white: 0.105)</c>: the pasteboard drawn behind the picture in EditorCanvas.draw.</summary>
    public static readonly Color Pasteboard = Color.FromRgb(0x1B, 0x1B, 0x1B);

    /// <summary>The transparency checker: <c>NSColor(white: 0.30)</c> with <c>0.35</c> squares, 10 points each.</summary>
    public static readonly Color CheckerBase = Color.FromRgb(0x4D, 0x4D, 0x4D);
    public static readonly Color CheckerSquare = Color.FromRgb(0x59, 0x59, 0x59);
    /// <summary>How wide one checker square is, in points, as the Mac's canvas draws it.</summary>
    public const double CheckerSize = 10;

    /// <summary><c>NSColor.white.withAlphaComponent(0.13)</c>: the hairline around the picture.</summary>
    public static readonly Color PictureEdge = Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF);

    /// <summary><c>EditorSession.guideColor</c>: srgb(0, 1, 1) at 0.9, Photoshop's default guide colour.</summary>
    public static readonly Color Guide = Color.FromArgb(0xE6, 0x00, 0xFF, 0xFF);

    /// <summary>The layout grid's default look — Light Gray 0.7, majors at 45% and subdivisions at 28%.</summary>
    public static readonly Color Grid = Color.FromArgb(0x73, 0xB3, 0xB3, 0xB3);
    public static readonly Color GridFine = Color.FromArgb(0x47, 0xB3, 0xB3, 0xB3);

    /// <summary><c>NSColor(white: 0.55, alpha: 0.45)</c>: a line around every document pixel.</summary>
    public static readonly Color PixelGrid = Color.FromArgb(0x73, 0x8C, 0x8C, 0x8C);

    /// <summary><c>NSColor.black.withAlphaComponent(0.6)</c>: what a crop takes away.</summary>
    public static readonly Color CropDim = Color.FromArgb(0x99, 0x00, 0x00, 0x00);

    /// <summary>Ruler tick, label and the line that shuts the strip off, from CanvasRulers.</summary>
    public static readonly Color RulerFace = Color.FromRgb(0x33, 0x33, 0x33);
    public static readonly Color RulerTick = Color.FromRgb(0x9E, 0x9E, 0x9E);
    public static readonly Color RulerLabel = Color.FromRgb(0xC7, 0xC7, 0xC7);
    public static readonly Color RulerEdge = Color.FromRgb(0x14, 0x14, 0x14);

    // ---- The Mac's system colours, at the values its dark appearance resolves them to ----

    /// <summary><c>NSColor.controlAccentColor</c>, whose default is the system blue: dark-mode blue is #0A84FF.</summary>
    public static readonly Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);
    /// <summary>The shades a track, a thumb or a tick takes when the pointer is over it or it is held down.</summary>
    public static readonly Color AccentHover = Color.FromRgb(0x4C, 0xA0, 0xFF);
    public static readonly Color AccentPressed = Color.FromRgb(0x00, 0x60, 0xC9);

    /// <summary><c>NSColor.labelColor</c> in the dark appearance: white at 0.85.</summary>
    public static readonly Color Label = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
    /// <summary><c>NSColor.secondaryLabelColor</c>: white at 0.55, what `.secondary` resolves to.</summary>
    public static readonly Color Secondary = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF);

    // ---- How the app's own surfaces are painted with them ----

    public static readonly IBrush ChromeBrush = new SolidColorBrush(Chrome);
    public static readonly IBrush PasteboardBrush = new SolidColorBrush(Pasteboard);
    public static readonly IBrush LabelBrush = new SolidColorBrush(Label);
    public static readonly IBrush SecondaryBrush = new SolidColorBrush(Secondary);
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);

    /// <summary>The tab in front, and the ones behind it: `Color.white.opacity(0.12)` over the strip
    /// (`0.035` when it is not the one being looked at), with a border of 0.22 and 0.08.</summary>
    public static readonly IBrush TabFront = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush TabBack = new SolidColorBrush(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush TabFrontEdge = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush TabBackEdge = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    /// <summary>The line between the groups of a list, as AppKit draws <c>NSMenuItem.separator()</c> in the
    /// dark appearance: white at 0.15.</summary>
    public static readonly IBrush MenuRule = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    /// <summary>Pens for the canvas's own drawing.</summary>
    public static readonly IPen PictureEdgePen = new Pen(new SolidColorBrush(PictureEdge), 1);
    public static readonly IPen GuidePen = new Pen(new SolidColorBrush(Guide), 1);
    public static readonly IPen SnapPen = new Pen(new SolidColorBrush(Accent), 1);
    public static readonly IPen GridPen = new Pen(new SolidColorBrush(Grid), 1);
    public static readonly IPen GridFinePen = new Pen(new SolidColorBrush(GridFine), 1);
    public static readonly IPen PixelGridPen = new Pen(new SolidColorBrush(PixelGrid), 1);
    public static readonly IBrush CropDimBrush = new SolidColorBrush(CropDim);

    /// <summary>The transform box: the accent's line, with white handles the accent outlines, as the Mac's
    /// overlay draws them (a dark line behind it read as a grey halo around the box).</summary>
    public static readonly IPen TransformPen = new Pen(new SolidColorBrush(Accent), 1);
    public static readonly IBrush HandleFill = Brushes.White;
    public static readonly IPen HandlePen = new Pen(new SolidColorBrush(Accent), 1);

    /// <summary>A curve editor's ground and grid: the Mac puts `Color.black.opacity(0.35)` behind the graph and
    /// draws its grid in `white.opacity(0.12)` — over the chrome that is these two, a drawing context having no
    /// other way to stack them.</summary>
    public static readonly IBrush CurveGround = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17));
    public static readonly IBrush CurveGrid = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    private static IBrush? _checker;

    /// <summary>
    /// The transparency checker as a tiled brush: the Mac tiles it in screen points whatever the zoom is, so
    /// this is a 20-point tile of four squares rather than anything drawn per pixel.
    /// </summary>
    public static IBrush Checker
    {
        get
        {
            if (_checker is not null) return _checker;
            var size = (int)(CheckerSize * 2);
            var tile = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var context = tile.CreateDrawingContext())
            {
                var half = size / 2.0;
                context.FillRectangle(new SolidColorBrush(CheckerBase), new Rect(0, 0, size, size));
                var square = new SolidColorBrush(CheckerSquare);
                context.FillRectangle(square, new Rect(0, 0, half, half));
                context.FillRectangle(square, new Rect(half, half, half, half));
            }
            return _checker = new ImageBrush(tile)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(0, 0, size, size, RelativeUnit.Absolute),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
        }
    }

    /// <summary>
    /// Moves Fluent's dark palette onto the Mac's colours where the two differ. The accent is not spelled out
    /// per control — a hundred keys hold a shade of Fluent's own blue — so the ones that follow the accent are
    /// found by the shade they hold rather than by name, and each is written back in the type it was found in
    /// (some keys are colours, some are brushes) so nothing downstream is handed the wrong kind of value.
    /// Returns how many resources were moved, which is what the self check reads.
    /// </summary>
    public static int Apply(FluentTheme theme)
    {
        if (theme.Resources is not ResourceDictionary resources
            || !resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var provider)
            || provider is not ResourceDictionary dark) return 0;

        var shades = new Dictionary<Color, Color>
        {
            [Color.FromRgb(0x00, 0x78, 0xD4)] = Accent,
            [Color.FromRgb(0x23, 0xA0, 0xFF)] = AccentHover,
            [Color.FromRgb(0x00, 0x58, 0x9B)] = AccentPressed,
        };
        var moved = 0;
        // Read through TryGetResource rather than the indexer: a resource dictionary answers that one and not
        // the other, and only the try has the variant in hand.
        foreach (var key in dark.Keys.ToList())
        {
            if (!dark.TryGetResource(key, null, out var current)) continue;
            if (Tint(current) is not { } colour || !shades.TryGetValue(colour, out var replacement)) continue;
            dark[key] = Wear(current, replacement);
            moved++;
        }

        // The Mac's chrome, its label and its secondary text, where Fluent's own would be black, white and grey.
        moved += Wear(dark, "SystemControlBackgroundAltHighBrush", Chrome);
        moved += Wear(dark, "SystemControlForegroundBaseHighBrush", Label);
        moved += Wear(dark, "SystemControlForegroundBaseMediumBrush", Secondary);
        return moved;
    }

    /// <summary>The colour a resource holds, whether it is a colour or a brush of one.</summary>
    private static Color? Tint(object? value) => value switch
    {
        Color colour => colour,
        ISolidColorBrush brush => brush.Color,
        _ => null,
    };

    /// <summary>The same kind of value with the colour changed.</summary>
    private static object Wear(object? original, Color colour) => original switch
    {
        Color => colour,
        ImmutableSolidColorBrush => new ImmutableSolidColorBrush(colour),
        ISolidColorBrush => new SolidColorBrush(colour),
        _ => new SolidColorBrush(colour),
    };

    private static int Wear(ResourceDictionary dark, string key, Color colour)
    {
        if (!dark.TryGetResource(key, null, out var original)) return 0;
        dark[key] = Wear(original, colour);
        return 1;
    }
}
