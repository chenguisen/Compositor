using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Choosing what an edit touches, and painting that respects it.</summary>
public class SelectionTests
{
    private static (CanvasDocument Document, ImageLayer Layer) Flat(int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.White);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Paint"),
            new Model.LayerTransform(0, 0, width, height), "Paint");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void WithNothingSelectedAnEditMayTouchEverything()
    {
        var (document, _) = Flat(20, 20);
        using var __ = document;
        Assert.Null(document.Selection.Path);
        Assert.True(document.Selection.Contains(0, 0));
        Assert.True(document.Selection.Contains(19, 19));
    }

    [Fact]
    public void SelectAllIsASelectionEvenThoughNothingIsLeftOut()
    {
        var (document, _) = Flat(20, 20);
        using var __ = document;

        Assert.True(SelectionEdits.SelectAll(document));
        // It is an outline the tool can show, unlike having no selection at all.
        Assert.Equal(SKRect.Create(0, 0, 20, 20), document.Selection.Path!.Bounds);
        Assert.True(document.Selection.Contains(19, 19));
        Assert.False(SelectionEdits.SelectAll(document));

        Assert.True(SelectionEdits.Deselect(document));
        Assert.Null(document.Selection.Path);
        Assert.False(SelectionEdits.Deselect(document));
    }

    [Fact]
    public void ASelectionIsHeldToTheCanvas()
    {
        var (document, _) = Flat(20, 20);
        using var __ = document;
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(-5, 5, 40, 10)));
        Assert.Equal(SKRect.Create(0, 5, 20, 10), document.Selection.Path!.Bounds);
    }

    [Fact]
    public void AnEmptyRectangleIsNoSelectionAtAll()
    {
        var (document, _) = Flat(20, 20);
        using var __ = document;
        Assert.False(SelectionEdits.Select(document, SKRectI.Create(30, 30, 5, 5)));
        Assert.Null(document.Selection.Path);
    }

    [Fact]
    public void PaintingStopsAtTheSelectionsEdge()
    {
        var (document, layer) = Flat(40, 20);
        using var __ = document;
        SelectionEdits.Select(document, SKRectI.Create(10, 0, 10, 20));

        // One long stroke across the whole layer, which only the selected columns may take.
        var stroke = new[] { new SKPoint(0.5f, 10.5f), new SKPoint(39.5f, 10.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, new BrushSettings(Diameter: 8, Red: 1)));

        Assert.Equal(255, layer.Asset!.Image.GetPixel(5, 10).Green);
        Assert.Equal(0, layer.Asset.Image.GetPixel(15, 10).Green);
        Assert.Equal(0, layer.Asset.Image.GetPixel(19, 10).Green);
        Assert.Equal(255, layer.Asset.Image.GetPixel(25, 10).Green);
    }

    [Fact]
    public void ChangingTheSelectionIsAnUndoStep()
    {
        var (document, layer) = Flat(20, 20);
        using var __ = document;
        var history = new DocumentHistory();

        history.Begin("Select", document, layer.ID);
        SelectionEdits.Select(document, SKRectI.Create(2, 3, 5, 5));
        history.End(document, layer.ID);

        Assert.Equal(SKRect.Create(2, 3, 5, 5), document.Selection.Path!.Bounds);
        Assert.True(history.CanUndo);

        // A selection is part of the document, so undo covers it even though it is not saved.
        var restored = history.Undo();
        Assert.NotNull(restored);
        Assert.Null(restored!.Value.Document!.Selection.Path);
    }
}
