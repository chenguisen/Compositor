using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>The undo history and the layer edits that go through it.</summary>
public class HistoryTests
{
    private static ImportedImage Asset(int size) => ImportedImage.Create(
        new SKBitmap(Bitmaps.ColorInfo(size, size)), "Layer");

    private static ImageLayer Layer(double x, double y, int width, int height, double rotation = 0)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Model.LayerTransform(x, y, width, height, rotation), "Layer");
    }

    [Fact]
    public void FlippingALayerTurnsItAboutItsOwnMiddle()
    {
        var layer = Layer(10, 20, 4, 6, rotation: 30);
        using var document = new CanvasDocument(Guid.NewGuid(), 16, 24);
        document.Layers.Add(layer);

        Assert.True(LayerEdits.Flip(document, [layer.ID], horizontally: true));

        // Its middle does not move when the line runs through it; the picture flips and the angle turns back.
        Assert.Equal(10, layer.Transform.X);
        Assert.Equal(20, layer.Transform.Y);
        Assert.True(layer.Transform.FlipX);
        Assert.False(layer.Transform.FlipY);
        Assert.Equal(-30, layer.Transform.Rotation);
    }

    [Fact]
    public void FlippingTheCanvasMirrorsLayersAndGuidesAcrossIt()
    {
        var layer = Layer(10, 20, 4, 6);
        using var document = new CanvasDocument(Guid.NewGuid(), 16, 24);
        document.Layers.Add(layer);
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 3 });
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 5 });

        LayerEdits.FlipCanvas(document, horizontally: true);

        // A layer 12 pixels from the left of a 16 wide canvas ends up 12 from the right: 2·8 − 12 − 2 = 2.
        Assert.Equal(2, layer.Transform.X);
        Assert.Equal(20, layer.Transform.Y);
        Assert.True(layer.Transform.FlipX);
        // The vertical guide crosses to the other side; the horizontal one is not what a horizontal flip moves.
        Assert.Equal(13, document.Guides[0].Position);
        Assert.Equal(5, document.Guides[1].Position);
    }

    [Fact]
    public void FlippingAFolderTakesItsContentsWithIt()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var folder = Layer(0, 0, 40, 40);
        folder.IsGroup = true;
        folder.Asset!.Dispose();
        folder.Asset = null;
        var child = Layer(10, 10, 8, 8);
        child.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(child);

        Assert.True(LayerEdits.Flip(document, [folder.ID], horizontally: false));
        // The box is measured around the folder's contents, not around the folder's own rectangle: the child
        // is the only thing with pixels, so it turns about its own middle and stays where it is.
        Assert.True(child.Transform.FlipY);
        Assert.Equal(10, child.Transform.Y);
        Assert.Equal(10, child.Transform.X);
    }

    [Fact]
    public void AnEditIsOneUndoStepAndUndoPutsItBack()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(10, 20, 4, 6);
        document.Layers.Add(layer);
        var history = new DocumentHistory();

        history.Begin("Move", document, layer.ID);
        Assert.True(LayerEdits.Move(document, layer.ID, 5, -3));
        history.End(document, layer.ID);

        Assert.Equal(15, layer.Transform.X);
        Assert.Equal(17, layer.Transform.Y);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("Move", history.UndoName);
        Assert.True(history.IsModified);

        var restored = history.Undo();
        Assert.NotNull(restored);
        Assert.Equal(10, restored!.Value.Document!.Layers[0].Transform.X);
        Assert.Equal(20, restored.Value.Document!.Layers[0].Transform.Y);
        Assert.Equal(layer.ID, restored.Value.ActiveLayerID);
        Assert.True(history.CanRedo);
        Assert.False(history.IsModified);

        var again = history.Redo();
        Assert.NotNull(again);
        Assert.Equal(15, again!.Value.Document!.Layers[0].Transform.X);
    }

    [Fact]
    public void AnEditThatChangesNothingKeepsTheRedoHistory()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(10, 20, 4, 6);
        document.Layers.Add(layer);
        var history = new DocumentHistory();

        history.Begin("Move", document, layer.ID);
        LayerEdits.Move(document, layer.ID, 5, 0);
        history.End(document, layer.ID);
        history.Undo();
        Assert.True(history.CanRedo);

        // Selecting or navigating opens and closes an edit without changing anything, which must not throw
        // the redo away.
        history.Begin("Select", document, layer.ID);
        history.End(document, layer.ID);

        Assert.True(history.CanRedo);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void TheHistoryForgetsTheOldestStepsPastItsLimit()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(0, 0, 4, 4);
        document.Layers.Add(layer);
        var history = new DocumentHistory(entryLimit: 2);

        for (var step = 1; step <= 4; step++)
        {
            history.Begin($"Move {step}", document, layer.ID);
            LayerEdits.Move(document, layer.ID, 1, 0);
            history.End(document, layer.ID);
        }

        Assert.Equal(2, history.UndoCount);
        Assert.Equal("Move 4", history.UndoName);
        history.Undo();
        Assert.Equal("Move 3", history.UndoName);
    }

    [Fact]
    public void MarkSavedClearsTheModifiedFlagAndFurtherEditsRaiseItAgain()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(0, 0, 4, 4);
        document.Layers.Add(layer);
        var history = new DocumentHistory();
        Assert.False(history.IsModified);

        history.Begin("Move", document, layer.ID);
        LayerEdits.Move(document, layer.ID, 1, 0);
        history.End(document, layer.ID);
        Assert.True(history.IsModified);

        history.MarkSaved();
        Assert.False(history.IsModified);

        history.Begin("Move", document, layer.ID);
        LayerEdits.Move(document, layer.ID, 1, 0);
        history.End(document, layer.ID);
        Assert.True(history.IsModified);

        // Undoing back to what was saved leaves it unmodified again.
        history.Undo();
        Assert.False(history.IsModified);
    }

    [Fact]
    public void TheHistoryCountsEachImageOnceHoweverManyStepsNameIt()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(0, 0, 4, 4);
        document.Layers.Add(layer);
        var history = new DocumentHistory();

        // Two edits, each swapping in a fresh picture, so the history holds two images the live layer no
        // longer names. A 4x4 image is 64 bytes and its small copy another 64.
        history.Begin("First", document, layer.ID);
        layer.Asset = Asset(4);
        history.End(document, layer.ID);
        history.Begin("Second", document, layer.ID);
        layer.Asset = Asset(4);
        history.End(document, layer.ID);

        Assert.Equal(256, history.RetainedBytes(document));
    }

    [Fact]
    public void ADragIsOneUndoStepHoweverManyMovesItReports()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(10, 10, 4, 4);
        document.Layers.Add(layer);
        var history = new DocumentHistory();

        // The pattern the canvas uses for a drag: open the edit when the pointer goes down, apply each
        // delta as it arrives, close it when the pointer comes up.
        history.Begin("Move", document, layer.ID);
        foreach (var step in new[] { 3.0, 4.0, 5.0 }) LayerEdits.Move(document, layer.ID, step, 0);
        history.End(document, layer.ID);

        Assert.Equal(22, layer.Transform.X);
        Assert.Equal(1, history.UndoCount);
        var restored = history.Undo();
        Assert.NotNull(restored);
        Assert.Equal(10, restored!.Value.Document!.Layers[0].Transform.X);
    }

    [Fact]
    public void ASnapshotDoesNotOwnThePixelsItShares()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = Layer(0, 0, 4, 4);
        document.Layers.Add(layer);

        var clone = document.Clone();
        Assert.NotSame(document.Layers[0], clone.Layers[0]);
        Assert.Same(document.Layers[0].Asset, clone.Layers[0].Asset);
        Assert.True(document.SameAs(clone));

        // Disposing the copy leaves the pixels for the document it was copied from.
        clone.Dispose();
        Assert.Equal(4, layer.Asset!.Image.Width);
        Assert.Equal(255, layer.Asset.Image.GetPixel(0, 0).Alpha);
    }
}
