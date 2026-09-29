using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The text layer: how big it comes out, what it draws, and that a text layer stays editable — the pixels
/// and the style are the same bitmap, so the style can be changed and the layer drawn again.
/// </summary>
public class TextEditsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorText-" + Guid.NewGuid().ToString("N"));

    public TextEditsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static LayerTextStyle Style(string content, double size = 72) => new()
    {
        Content = content,
        FontName = "Arial",
        FontSize = size,
        Red = 0,
        Green = 0,
        Blue = 0,
    };

    /// <summary>How many pixels hold ink, and where the ink starts, so a glyph's shape need not be known.</summary>
    private static (int Count, int Left, int Right, int Top, int Bottom) Ink(SKBitmap image, SKColor? colour = null)
    {
        var count = 0;
        var left = image.Width;
        var right = -1;
        var top = image.Height;
        var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.Alpha == 0) continue;
                if (colour is { } wanted && pixel.Red < 200 && wanted.Red >= 200) continue;
                count++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return (count, left, right, top, bottom);
    }

    [Fact]
    public void ATextLayerIsNamedAfterItsFirstWords()
    {
        Assert.Equal("Hello there", TextEdits.LayerName("Hello   there"));
        Assert.Equal("Hello there world", TextEdits.LayerName("Hello\nthere\r\nworld"));
        Assert.Equal("Text", TextEdits.LayerName("   \n  "));
        Assert.Equal(new string('x', 40), TextEdits.LayerName(new string('x', 60)));
    }

    [Fact]
    public void PointTextIsAsBigAsItMeasures()
    {
        var small = TextEdits.BoxSize(Style("Hello", 36));
        var large = TextEdits.BoxSize(Style("Hello", 72));
        Assert.True(large.Width > small.Width, $"{large.Width} is not wider than {small.Width}");
        Assert.True(large.Height > small.Height, $"{large.Height} is not taller than {small.Height}");

        // The room around the text is part of the box, so even an empty line has the padding.
        var empty = TextEdits.BoxSize(Style("", 72));
        Assert.True(empty.Width >= TextEdits.LeastSide, $"an empty box is {empty.Width} wide");
        Assert.True(empty.Height >= TextEdits.LeastSide, $"an empty box is {empty.Height} tall");
    }

    [Fact]
    public void ASecondLineMakesTheLayerTaller()
    {
        var one = TextEdits.BoxSize(Style("Hello"));
        var two = TextEdits.BoxSize(Style("Hello\nHello"));
        Assert.True(two.Height > one.Height, $"{two.Height} is not taller than {one.Height}");
        Assert.Equal(one.Width, two.Width);
    }

    [Fact]
    public void AParagraphBoxIsTheBoxItWasGiven()
    {
        var style = Style("Hello");
        style.BoxSize = new JsonSize { Width = 300, Height = 200 };
        Assert.Equal((300, 200), TextEdits.BoxSize(style));

        // A box too small to hold a caret is held to the least side.
        style.BoxSize = new JsonSize { Width = 4, Height = 4 };
        Assert.Equal((TextEdits.LeastSide, TextEdits.LeastSide), TextEdits.BoxSize(style));
    }

    [Fact]
    public void TheTextIsDrawnInItsOwnColourWithRoomAroundIt()
    {
        using var image = TextEdits.Image(Style("Hello"))!;
        Assert.Equal(SKAlphaType.Unpremul, image.AlphaType);
        var ink = Ink(image);
        Assert.True(ink.Count > 0, "nothing was drawn");
        // The padding is left clear, and the ink is inside the box.
        Assert.True(ink.Left >= TextEdits.Padding - 1, $"ink starts at {ink.Left}");
        Assert.True(ink.Right < image.Width, $"ink reaches {ink.Right} of {image.Width}");
        Assert.True(ink.Top >= 1, $"ink starts at {ink.Top}");
        Assert.True(ink.Bottom < image.Height);
    }

    [Fact]
    public void AColourRunIsDrawnInItsOwnColourAndToTheRightOfWhatPrecedesIt()
    {
        using var image = TextEdits.Image(Style("XXXX"))!;
        var oneLine = Ink(image);

        var style = Style("XXXX");
        style.ColorRuns = [new LayerTextColorRun { Location = 2, Length = 2, Red = 1, Green = 0, Blue = 0 }];
        using var coloured = TextEdits.Image(style)!;
        var (count, left, _, _, _) = Ink(coloured, SKColors.Red);
        Assert.True(count > 0, "the red run was not drawn");
        // The red run is the right-hand half, so its ink starts past the middle of the whole line.
        Assert.True(left > oneLine.Left + (oneLine.Right - oneLine.Left) / 2, $"the red run starts at {left}");
    }

    [Fact]
    public void TrackingWidensTheLine()
    {
        var plain = TextEdits.BoxSize(Style("Hello"));
        var style = Style("Hello");
        style.Tracking = 12;
        Assert.True(TextEdits.BoxSize(style).Width > plain.Width);
    }

    [Fact]
    public void CentreingMovesTheTextInwards()
    {
        var left = Style("Hi");
        left.BoxSize = new JsonSize { Width = 400, Height = 120 };
        left.Alignment = TextAlignment.Left;
        using var leftImage = TextEdits.Image(left)!;

        var centred = Style("Hi");
        centred.BoxSize = new JsonSize { Width = 400, Height = 120 };
        centred.Alignment = TextAlignment.Center;
        using var centredImage = TextEdits.Image(centred)!;

        var right = Style("Hi");
        right.BoxSize = new JsonSize { Width = 400, Height = 120 };
        right.Alignment = TextAlignment.Right;
        using var rightImage = TextEdits.Image(right)!;

        Assert.True(Ink(centredImage).Left > Ink(leftImage).Left);
        Assert.True(Ink(rightImage).Left > Ink(centredImage).Left);
    }

    [Fact]
    public void AParagraphWrapsAtItsBox()
    {
        var style = Style("one two three four five six", 24);
        style.BoxSize = new JsonSize { Width = 120, Height = 400 };
        using var image = TextEdits.Image(style)!;
        var ink = Ink(image);
        // Six words in a 96-pixel measure take more than one line, so the ink is taller than one line.
        Assert.True(ink.Bottom - ink.Top > style.LineHeight, $"the ink is {ink.Bottom - ink.Top} tall");
        Assert.True(ink.Right < 120, $"the ink reaches {ink.Right}, past the box");
    }

    [Fact]
    public void AnUnknownFaceFallsBackToTheOneTheSystemHas()
    {
        Assert.NotNull(TextEdits.Typeface("No such face at all"));
        Assert.NotNull(TextEdits.Typeface(null));
    }

    [Fact]
    public void ATextLayerIsStillAnEditableTextLayer()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var id = TextEdits.Add(document, Style("Hello"), new SKPoint(20, 30));
        Assert.NotNull(id);
        var layer = document.Layers.Single();
        Assert.Equal("Hello", layer.Name);
        // The pixels and the style share one bitmap, which is what makes it live text.
        Assert.NotNull(layer.LiveText);
        Assert.Same(layer.Asset!.Image, layer.Text!.Image);
        // The style that drew it comes back with it.
        Assert.Equal("Hello", layer.LiveText.Content);
        Assert.Equal("Arial", layer.LiveText.FontName);
        Assert.Equal(0, layer.LiveText.Red);
        // It is placed where it was asked for, and its pixels are the size of the box.
        Assert.Equal(20, layer.Transform.X);
        Assert.Equal(30, layer.Transform.Y);
        Assert.Equal(TextEdits.BoxSize(layer.LiveText).Width, layer.Asset.Width);
    }

    [Fact]
    public void EditingTheStyleDrawsTheLayerAgainAboutItsMiddle()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 600, 300);
        var id = TextEdits.Add(document, Style("Hello", 36), new SKPoint(20, 30));
        Assert.NotNull(id);
        var layer = document.Layers.Single();
        var centreX = layer.Transform.CenterX;
        var centreY = layer.Transform.CenterY;
        var wasSmall = layer.Asset!.Width;

        var bigger = Style("Hello", 72);
        Assert.True(TextEdits.SetStyle(document, id!.Value, bigger));
        Assert.True(layer.Asset!.Width > wasSmall);
        Assert.Equal(centreX, layer.Transform.CenterX, 6);
        Assert.Equal(centreY, layer.Transform.CenterY, 6);
        Assert.Equal(72, layer.LiveText!.FontSize);
    }

    [Fact]
    public void ATextLayerSavesAndLoadsWithItsStyle()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var style = Style("Save me");
        style.ColorRuns = [new LayerTextColorRun { Location = 0, Length = 4, Red = 1, Green = 0, Blue = 0 }];
        Assert.NotNull(TextEdits.Add(document, style, new SKPoint(10, 10)));

        var package = Path.Combine(_root, "Text.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
        using var loaded = ProjectStore.Load(package);
        var record = loaded.Manifest.Layers.Single();
        var saved = record.Text;
        Assert.NotNull(saved);
        Assert.Equal("Save me", saved!.Content);
        Assert.Equal("Arial", saved.FontName);
        Assert.Single(saved.ColorRuns!);
        Assert.Equal(1, saved.ColorRuns![0].Red);

        using var after = loaded.ToDocument();
        Assert.NotNull(after.Layers.Single().LiveText);
    }

    [Fact]
    public void TextTooBigForOneSurfaceIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var style = Style("Hello");
        style.BoxSize = new JsonSize { Width = 20_000, Height = 20_000 };
        Assert.Null(TextEdits.Image(style));
        Assert.Null(TextEdits.Add(document, style, new SKPoint(0, 0)));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void TheCaretSitsAfterTheLastCharacterOfTheLastLine()
    {
        // An empty layer has its caret on the first line, inside the padding.
        var empty = TextEdits.Caret(Style(""));
        Assert.Equal(TextEdits.Padding, empty.X, 2);
        Assert.True(empty.Y > TextEdits.Padding, $"the caret is at {empty.Y}");

        var one = TextEdits.Caret(Style("Hello"));
        var two = TextEdits.Caret(Style("Hello" + Environment.NewLine + "Hi"));
        // Each is on its own baseline, so the second line is a line lower.
        Assert.True(two.Y > one.Y, $"{two.Y} is not below {one.Y}");
        Assert.Equal(one.Y + Style("x").LineHeight, two.Y, 2);
        // And on the second line the caret is after fewer characters, so it is nearer the left.
        Assert.True(two.X < one.X, $"{two.X} is not left of {one.X}");
        // A longer line puts the caret further right.
        Assert.True(TextEdits.Caret(Style("Hello there")).X > one.X);
    }

    [Fact]
    public void TrackingMovesTheCaretWithTheLetters()
    {
        var plain = TextEdits.Caret(Style("Hello"));
        var spaced = Style("Hello");
        spaced.Tracking = 10;
        Assert.True(TextEdits.Caret(spaced).X > plain.X);
    }

    [Fact]
    public void AParagraphPutsTheCaretAfterTheWordOnItsLastLine()
    {
        var style = Style("one two three four", 24);
        style.BoxSize = new JsonSize { Width = 90, Height = 400 };
        var caret = TextEdits.Caret(style);
        // The text wraps, so the caret is on the last of the lines and inside the box.
        Assert.True(caret.Y > Style("x").LineHeight, $"the caret is at {caret.Y}");
        Assert.True(caret.X < 90, $"the caret is at {caret.X}");
    }
}
