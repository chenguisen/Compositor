using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Typing text on the canvas: the layer is made on the first character and drawn again as the words grow,
/// the whole session is one change, and letting it go puts the layer back the way it was.
/// </summary>
public class TextSessionTests
{
    private static LayerTextStyle Style(string content = "") => new()
    {
        Content = content,
        FontName = "Arial",
        FontSize = 48,
        Red = 0,
        Green = 0,
        Blue = 0,
    };

    [Fact]
    public void NothingIsAddedUntilACharacterIsTyped()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(20, 30));
        Assert.Null(session.LayerID);
        Assert.Empty(document.Layers);

        Assert.True(session.Type(document, "H"));
        Assert.NotNull(session.LayerID);
        Assert.Single(document.Layers);
        Assert.Equal("H", document.Layers.Single().LiveText!.Content);
        Assert.Equal(20, document.Layers.Single().Transform.X);
        Assert.Equal(30, document.Layers.Single().Transform.Y);
    }

    [Fact]
    public void EveryLetterIsDrawnAsItArrives()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        Assert.True(session.Type(document, "H"));
        var wasNarrow = document.Layers.Single().Asset!.Width;
        Assert.True(session.Type(document, "i"));
        Assert.Equal("Hi", session.Content);
        Assert.Equal("Hi", document.Layers.Single().LiveText!.Content);
        // A second letter makes the layer wider, so what is on the canvas follows what was typed.
        Assert.True(document.Layers.Single().Asset!.Width > wasNarrow);
    }

    [Fact]
    public void ANewlineIsTypedLikeAnyOtherCharacter()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        session.Type(document, "one");
        session.Type(document, "\n");
        session.Type(document, "two");
        Assert.Equal("one\ntwo", session.Content);
        // Two lines are taller than one.
        Assert.True(document.Layers.Single().Asset!.Height > TextEdits.BoxSize(Style("one")).Height);
    }

    [Fact]
    public void TheDeleteKeyTakesTheLastCharacterBack()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        session.Type(document, "Hi");
        Assert.True(session.Backspace(document));
        Assert.Equal("H", session.Content);
        Assert.Equal("H", document.Layers.Single().LiveText!.Content);
        Assert.True(session.Backspace(document));
        Assert.Equal("", session.Content);
        // Nothing left to take back.
        Assert.False(session.Backspace(document));
    }

    [Fact]
    public void TheDeleteKeyTakesAWholeSurrogatePair()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        session.Type(document, "\U0001F600");
        Assert.True(session.Backspace(document));
        Assert.Equal("", session.Content);
        Assert.False(session.Backspace(document));
    }

    [Fact]
    public void CommittingKeepsTheWordsAndAnswersWithTheLayer()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(10, 10));
        session.Type(document, "Kept");
        var kept = session.Commit(document);
        Assert.NotNull(kept);
        Assert.Single(document.Layers);
        Assert.Equal(kept, document.Layers.Single().ID);
        Assert.Equal("Kept", document.Layers.Single().Name);
    }

    [Fact]
    public void CommittingNothingTypedTakesAwayWhatWasStarted()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(10, 10));
        session.Type(document, "   ");
        Assert.Single(document.Layers);
        // Spaces alone are not worth a layer.
        Assert.Null(session.Commit(document));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void LettingGoOfNewTextTakesAwayWhatWasStarted()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(10, 10));
        session.Type(document, "Gone");
        Assert.True(session.Cancel(document));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void EditingALayerPutsItsOldWordsBackWhenLetGo()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        Assert.NotNull(TextEdits.Add(document, Style("Before"), new SKPoint(10, 10)));
        var layer = document.Layers.Single();

        var session = TextSession.Editing(layer);
        Assert.Equal("Before", session.Content);
        Assert.Equal(layer.ID, session.LayerID);
        session.Type(document, " and after");
        Assert.Equal("Before and after", layer.LiveText!.Content);

        Assert.True(session.Cancel(document));
        Assert.Equal("Before", layer.LiveText!.Content);
        Assert.Equal("Before", layer.Name);
    }

    [Fact]
    public void EditingALayerKeepsTheNewWordsWhenTheyAreFinished()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        Assert.NotNull(TextEdits.Add(document, Style("Before"), new SKPoint(10, 10)));
        var layer = document.Layers.Single();
        var session = TextSession.Editing(layer);
        session.Type(document, " more");
        Assert.Equal(layer.ID, session.Commit(document));
        Assert.Equal("Before more", layer.LiveText!.Content);
        Assert.Equal("Before more", layer.Name);
    }

    [Fact]
    public void EditingIsRefusedOnALayerThatIsNotText()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
        bitmap.Erase(SKColors.Red);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Plain"),
            new Model.LayerTransform(0, 0, 20, 20), "Plain");
        document.Layers.Add(layer);
        Assert.Throws<ArgumentException>(() => TextSession.Editing(layer));
    }

    [Fact]
    public void TheCaretSitsWhereTheTextEndsAndGrowsWithIt()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(50, 60));
        // Before anything is typed the caret is inside the padding of where the text will go.
        var empty = session.Caret(document);
        Assert.True(empty.Left >= 50, $"the caret is at {empty.Left}");
        Assert.True(empty.Top < 60 + Style().FontSize, $"the caret is at {empty.Top}");
        Assert.True(empty.Height > 1);

        session.Type(document, "Hi");
        var typed = session.Caret(document);
        // It has moved right with the letters, and down with the type's own size.
        Assert.True(typed.Left > empty.Left, $"{typed.Left} is not right of {empty.Left}");
        Assert.True(typed.Height > empty.Height / 2);
    }

    [Fact]
    public void TheBoxOfASessionWithNoLayerIsWhereTheTextWouldGo()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(30, 40));
        var box = session.Box(document);
        Assert.Equal(30, box.Left);
        Assert.Equal(40, box.Top);
        Assert.True(box.Width >= TextEdits.LeastSide);
        // A click inside it stays in the session; one outside it does not.
        Assert.True(session.Contains(document, new SKPoint(35, 45)));
        Assert.False(session.Contains(document, new SKPoint(5, 5)));
    }

    [Fact]
    public void TheBoxFollowsTheLayerOnceThereIsOne()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(30, 40));
        session.Type(document, "Hi");
        var layer = document.Layers.Single();
        layer.Transform = layer.Transform with { X = 100, Y = 120 };
        var box = session.Box(document);
        Assert.Equal(100, box.Left);
        Assert.Equal(120, box.Top);
        Assert.True(session.Contains(document, new SKPoint(105, 125)));
    }

    [Fact]
    public void TextTooBigToOneSurfaceIsRefusedAndTheSessionIsLeftAsItWas()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var style = Style();
        style.BoxSize = new JsonSize { Width = 20_000, Height = 20_000 };
        var session = TextSession.New(style, new SKPoint(0, 0));
        Assert.False(session.Type(document, "H"));
        Assert.Equal("", session.Content);
        Assert.Empty(document.Layers);
    }
}
