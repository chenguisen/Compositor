using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The Layers panel's other verbs: renaming, showing and hiding, moving a layer through its siblings,
/// duplicating one and deleting one.
/// </summary>
public class LayerEditTests
{
    private static ImageLayer Patch(SKColor colour, double x, double y, int width, int height, string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new Model.LayerTransform(x, y, width, height), name);
    }

    private static ImageLayer Folder(string name, Guid? parent = null) =>
        new(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), name)
        {
            IsGroup = true,
            ParentID = parent,
        };

    private static CanvasDocument Doc(int width, int height, params ImageLayer[] layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        document.Layers.AddRange(layers);
        return document;
    }

    [Fact]
    public void RenamingTakesTheWhitespaceOffAndRefusesABlankName()
    {
        var layer = Patch(SKColors.Red, 0, 0, 8, 8, "Layer");
        using var document = Doc(8, 8, layer);

        Assert.True(LayerEdits.Rename(document, layer.ID, "  Backdrop  "));
        Assert.Equal("Backdrop", layer.Name);
        Assert.False(LayerEdits.Rename(document, layer.ID, "   "));
        Assert.Equal("Backdrop", layer.Name);
        // Nothing to do is not an edit, so it must not become a step in the history.
        Assert.False(LayerEdits.Rename(document, layer.ID, "Backdrop"));
    }

    [Fact]
    public void ShowingAndHidingTogglesWhatTheCanvasDraws()
    {
        var layer = Patch(SKColors.Red, 0, 0, 8, 8, "Layer");
        using var document = Doc(8, 8, layer);

        Assert.False(LayerEdits.SetVisible(document, layer.ID, true));
        Assert.True(LayerEdits.SetVisible(document, layer.ID, false));
        using (var hidden = DocumentRenderer.Render(document))
        {
            Assert.Equal(0, hidden.GetPixel(4, 4).Alpha);
        }
        Assert.True(LayerEdits.SetVisible(document, layer.ID, true));
        using var shown = DocumentRenderer.Render(document);
        Assert.Equal(255, shown.GetPixel(4, 4).Alpha);
    }

    [Fact]
    public void AHiddenFolderHidesWhatIsInsideIt()
    {
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Red, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        using var document = Doc(8, 8, folder, inside);

        Assert.True(LayerEdits.SetVisible(document, folder.ID, false));
        using var hidden = DocumentRenderer.Render(document);
        Assert.Equal(0, hidden.GetPixel(4, 4).Alpha);
    }

    [Fact]
    public void MovingALayerSwapsItWithTheSiblingNextToIt()
    {
        var a = Patch(SKColors.Blue, 0, 0, 8, 8, "A");
        var b = Patch(SKColors.Green, 0, 0, 8, 8, "B");
        var c = Patch(SKColors.Red, 0, 0, 8, 8, "C");
        using var document = Doc(8, 8, a, b, c);

        Assert.True(LayerEdits.MoveBy(document, b.ID, 1));
        Assert.Equal(new[] { a.ID, c.ID, b.ID }, document.Layers.Select(layer => layer.ID));
        Assert.True(LayerEdits.MoveBy(document, b.ID, -1));
        Assert.Equal(new[] { a.ID, b.ID, c.ID }, document.Layers.Select(layer => layer.ID));
    }

    [Fact]
    public void MovingStopsAtTheEndsOfTheStack()
    {
        var a = Patch(SKColors.Blue, 0, 0, 8, 8, "A");
        var b = Patch(SKColors.Green, 0, 0, 8, 8, "B");
        using var document = Doc(8, 8, a, b);

        Assert.False(LayerEdits.MoveBy(document, b.ID, 1));
        Assert.False(LayerEdits.MoveBy(document, a.ID, -1));
        Assert.Equal(new[] { a.ID, b.ID }, document.Layers.Select(layer => layer.ID));
    }

    [Fact]
    public void MovingALayerPassesTheLayersItCannotReach()
    {
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        var top = Patch(SKColors.Red, 0, 0, 8, 8, "Top");
        using var document = Doc(8, 8, folder, inside, top);

        // The layer inside the folder is its only one, so it has no sibling to trade places with — and the
        // next record in the array is the folder it belongs to, which is not a sibling either.
        Assert.False(LayerEdits.MoveBy(document, inside.ID, 1));
        Assert.False(LayerEdits.MoveBy(document, inside.ID, -1));
        // The folder itself moves among the top-level layers, still holding what is inside it.
        Assert.True(LayerEdits.MoveBy(document, folder.ID, 1));
        Assert.Equal(new[] { top.ID, inside.ID, folder.ID }, document.Layers.Select(layer => layer.ID));
        Assert.Equal(folder.ID, inside.ParentID);
    }

    [Fact]
    public void DuplicatingPutsTheCopyJustAboveTheOriginal()
    {
        var a = Patch(SKColors.Blue, 0, 0, 8, 8, "A");
        var b = Patch(SKColors.Green, 0, 0, 8, 8, "B");
        using var document = Doc(8, 8, a, b);

        var copy = LayerEdits.Duplicate(document, a.ID);
        Assert.NotNull(copy);
        Assert.Equal(3, document.Layers.Count);
        Assert.Equal(new[] { a.ID, copy.Value, b.ID }, document.Layers.Select(layer => layer.ID));
        Assert.Equal("A copy", document.Layers[1].Name);
        // The copy shares the pixels until one of them is edited.
        Assert.Same(a.Asset, document.Layers[1].Asset);
    }

    [Fact]
    public void DuplicatingAFolderTakesWhatIsInsideIt()
    {
        var beneath = Patch(SKColors.Blue, 0, 0, 8, 8, "Beneath");
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        var clipped = Patch(SKColors.Red, 0, 0, 8, 8, "Clipped");
        clipped.ParentID = folder.ID;
        clipped.MaskSourceID = inside.ID;
        using var document = Doc(8, 8, beneath, folder, inside, clipped);

        var copy = LayerEdits.Duplicate(document, folder.ID);
        Assert.NotNull(copy);
        Assert.Equal(7, document.Layers.Count);
        var copyOfFolder = document.Layers.Single(layer => layer.ID == copy.Value);
        Assert.True(copyOfFolder.IsGroup);
        Assert.Equal("Folder copy", copyOfFolder.Name);
        // The copies' parent and clip point at the copies, not at the originals.
        var copies = document.Layers.Where(layer => layer.ParentID == copy.Value).ToList();
        Assert.Equal(2, copies.Count);
        var copyOfInside = copies.Single(layer => layer.MaskSourceID is null);
        var copyOfClipped = copies.Single(layer => layer.MaskSourceID is not null);
        Assert.Equal(copyOfInside.ID, copyOfClipped.MaskSourceID);
        Assert.NotEqual(inside.ID, copyOfInside.ID);
        Assert.Equal("Inside", copyOfInside.Name);
        // Nothing points back at what stayed behind.
        Assert.Equal(folder.ID, inside.ParentID);
    }

    [Fact]
    public void DeletingAFolderTakesWhatIsInsideIt()
    {
        var beneath = Patch(SKColors.Blue, 0, 0, 8, 8, "Beneath");
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        using var document = Doc(8, 8, beneath, folder, inside);

        Assert.True(LayerEdits.Delete(document, folder.ID));
        Assert.Equal(new[] { beneath.ID }, document.Layers.Select(layer => layer.ID));
    }

    [Fact]
    public void DeletingIsRefusedWhenSomethingElseIsClippedToIt()
    {
        var baseLayer = Patch(SKColors.Blue, 0, 0, 8, 8, "Base");
        var clipped = Patch(SKColors.Green, 0, 0, 8, 8, "Clipped");
        clipped.MaskSourceID = baseLayer.ID;
        using var document = Doc(8, 8, baseLayer, clipped);

        // The Mac build offers to bake or unlink; this port refuses rather than change the picture quietly.
        Assert.False(LayerEdits.Delete(document, baseLayer.ID));
        Assert.Equal(2, document.Layers.Count);
        // A layer nothing else depends on goes freely.
        Assert.True(LayerEdits.Delete(document, clipped.ID));
        Assert.Equal(new[] { baseLayer.ID }, document.Layers.Select(layer => layer.ID));
    }

    [Fact]
    public void DeletingAFolderWhoseChildSuppliesAClipIsFine()
    {
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        var clipped = Patch(SKColors.Red, 0, 0, 8, 8, "Clipped");
        clipped.ParentID = folder.ID;
        clipped.MaskSourceID = inside.ID;
        using var document = Doc(8, 8, folder, inside, clipped);

        // The base and the layer clipped to it both go, so nothing that stays holds a clip to a layer that
        // is gone.
        Assert.True(LayerEdits.Delete(document, folder.ID));
        Assert.Empty(document.Layers);
    }
}
