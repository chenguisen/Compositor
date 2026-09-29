using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using LayerMask = Compositor.Core.Model.LayerMask;

namespace Compositor.Core.Tests;

/// <summary>
/// Filling and clearing through the selection, and taking a selection from what a layer shows or from what its
/// mask hides. What the selection covers is what is touched, and by how much it covers it.
/// </summary>
public class FillEditsTests
{
    /// <summary>A layer of one colour, and the document holding it. The layer covers the canvas by default.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Flat(SKColor? colour = null, int side = 40,
        LayerTransform? transform = null)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(colour ?? new SKColor(200, 60, 40));
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
            transform ?? new LayerTransform(0, 0, side, side), "Flat");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor At(ImageLayer layer, int x, int y) => layer.Asset!.Image.GetPixel(x, y);

    [Fact]
    public void FillingWithNoSelectionPaintsTheWholeLayer()
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(FillEdits.Fill(document, layer.ID, new SKColor(0, 0, 255)));
        Assert.Equal(new SKColor(0, 0, 255), At(layer, 0, 0));
        Assert.Equal(new SKColor(0, 0, 255), At(layer, 39, 39));
        Assert.Equal(255, At(layer, 20, 20).Alpha);
    }

    [Fact]
    public void FillingStaysInsideTheSelection()
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(10, 10, 20, 20)));
        Assert.True(FillEdits.Fill(document, layer.ID, new SKColor(0, 0, 255)));
        // Inside the rectangle is the new colour, outside it is what the layer held.
        Assert.Equal(new SKColor(0, 0, 255), At(layer, 20, 20));
        Assert.Equal(new SKColor(0, 0, 255), At(layer, 12, 12));
        Assert.Equal(new SKColor(200, 60, 40), At(layer, 5, 5));
        Assert.Equal(new SKColor(200, 60, 40), At(layer, 35, 35));
    }

    [Fact]
    public void ASoftEdgeIsFilledSoftlyRatherThanCutOff()
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(10, 10, 20, 20)));
        Assert.True(SelectionEdits.Feather(document, 6));
        Assert.True(FillEdits.Fill(document, layer.ID, new SKColor(0, 0, 255)));
        // The middle took the colour whole; a pixel under the feathered rim took some of it.
        Assert.Equal(new SKColor(0, 0, 255), At(layer, 20, 20));
        var rim = new List<SKColor>();
        for (var x = 6; x < 14; x++) rim.Add(At(layer, x, 20));
        Assert.Contains(rim, colour => colour.Blue > 60 && colour.Red > 0 && colour.Red < 200);
        // And the far corner was not touched at all.
        Assert.Equal(new SKColor(200, 60, 40), At(layer, 1, 1));
    }

    [Fact]
    public void ClearingMakesTheSelectedPixelsTransparent()
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(10, 10, 20, 20)));
        Assert.True(FillEdits.Clear(document, layer.ID));
        Assert.Equal(0, At(layer, 20, 20).Alpha);
        Assert.Equal(0, At(layer, 12, 12).Alpha);
        // What was outside keeps both its colour and its opacity.
        Assert.Equal(new SKColor(200, 60, 40), At(layer, 5, 5));
        Assert.Equal(255, At(layer, 35, 35).Alpha);
    }

    [Fact]
    public void AMaskIsFilledThroughTheSelectionToo()
    {
        var (document, layer) = Flat();
        using var _ = document;
        // A mask painting everything black, so the layer is hidden.
        var mask = new SKBitmap(Bitmaps.MaskInfo(40, 40));
        mask.Erase(SKColors.Black);
        layer.Mask = LayerMask.AssetFrom(mask);
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(10, 10, 20, 20)));
        Assert.True(FillEdits.FillMask(document, layer.ID, 255));
        Assert.Equal(255, layer.Mask!.Asset.Image.GetPixel(20, 20).Red);
        Assert.Equal(0, layer.Mask.Asset.Image.GetPixel(5, 5).Red);
        // Filling a layer with no mask is refused rather than quietly painting pixels instead.
        Assert.False(FillEdits.FillMask(document, Guid.NewGuid(), 255));
    }

    [Fact]
    public void FillingALayerThatIsNotThereOrIsAFolderIsRefused()
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.False(FillEdits.Fill(document, Guid.NewGuid(), SKColors.White));
        Assert.False(FillEdits.Clear(document, Guid.NewGuid()));
        var folder = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 40, 40), "Folder") { IsGroup = true };
        document.Layers.Add(folder);
        Assert.False(FillEdits.Fill(document, folder.ID, SKColors.White));
        Assert.False(FillEdits.Clear(document, folder.ID));
        // A layer off the canvas is covered by no selection at all, so there is nothing to clear.
        var away = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(new SKBitmap(Bitmaps.ColorInfo(8, 8)), "Away"),
            new LayerTransform(200, 200, 8, 8), "Away");
        document.Layers.Add(away);
        Assert.True(SelectionEdits.SelectAll(document));
        Assert.False(FillEdits.Clear(document, away.ID));
        Assert.False(FillEdits.Fill(document, away.ID, SKColors.White));
    }

    [Fact]
    public void ALayersOwnPixelsBecomeTheSelectionInTheirPlaceOnTheDocument()
    {
        // The layer is a small patch away from the document's corner, so the selection has to be carried from
        // the layer's own grid to where the layer sits.
        var (document, layer) = Flat(transform: new LayerTransform(10, 10, 20, 20));
        using var _ = document;
        Assert.True(SelectionEdits.SelectLayerPixels(document, layer.ID));
        var path = document.Selection.Path;
        Assert.NotNull(path);
        Assert.True(path.Contains(20, 20), "the middle of the patch is not selected");
        Assert.False(path.Contains(2, 2), "a corner of the document was selected");
        Assert.False(path.Contains(38, 38), "past the patch was selected");
        // The outline is where the patch is: ten in from the corner and twenty across.
        var bounds = path.Bounds;
        Assert.Equal(10, bounds.Left, 1);
        Assert.Equal(30, bounds.Right, 1);
    }

    [Fact]
    public void OnlyTheOpaquePartOfALayerIsTaken()
    {
        var (document, layer) = Flat();
        using var _ = document;
        // The left half is cleared, so only the right half shows.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 40));
        bitmap.Erase(new SKColor(200, 60, 40));
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 20; x++) bitmap.SetPixel(x, y, SKColors.Transparent);
        }
        layer.Asset = ImportedImage.Create(bitmap, "Half");
        Assert.True(SelectionEdits.SelectLayerPixels(document, layer.ID));
        var path = document.Selection.Path;
        Assert.NotNull(path);
        Assert.True(path.Contains(30, 20), "the half that shows was not taken");
        Assert.False(path.Contains(10, 20), "the half that is clear was taken");
        // A folder holds no pixels of its own, so there is nothing to take from it.
        var folder = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 40, 40), "Folder") { IsGroup = true };
        document.Layers.Add(folder);
        Assert.False(SelectionEdits.SelectLayerPixels(document, folder.ID));
    }

    [Fact]
    public void AMasksHiddenAreasBecomeTheSelection()
    {
        var (document, layer) = Flat();
        using var _ = document;
        // A mask that hides the right half: black there, white elsewhere.
        var mask = new SKBitmap(Bitmaps.MaskInfo(40, 40));
        mask.Erase(SKColors.White);
        for (var y = 0; y < 40; y++)
        {
            for (var x = 20; x < 40; x++) mask.SetPixel(x, y, SKColors.Black);
        }
        layer.Mask = LayerMask.AssetFrom(mask);
        Assert.True(SelectionEdits.SelectMaskDark(document, layer.ID));
        var path = document.Selection.Path;
        Assert.NotNull(path);
        Assert.True(path.Contains(30, 20), "the hidden half is not selected");
        Assert.False(path.Contains(10, 20), "the shown half was selected");
        // A layer with no mask has nothing hidden.
        var bare = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 40, 40), "Bare");
        document.Layers.Add(bare);
        Assert.False(SelectionEdits.SelectMaskDark(document, bare.ID));
    }

    [Fact]
    public void AnEmptySelectionIsLeftWhenNothingPassesTheTest()
    {
        var (document, layer) = Flat();
        using var _ = document;
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 40));
        bitmap.Erase(SKColors.Transparent);
        layer.Asset = ImportedImage.Create(bitmap, "Clear");
        // Nothing passes the test, so nothing is selected rather than an old shape being left behind. The call
        // answers false because there was nothing to take away, as the wand's does when it matches nothing.
        SelectionEdits.SelectLayerPixels(document, layer.ID);
        Assert.Null(document.Selection.Path);
    }
}
