using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The layer-panel verbs have to leave a document that can be written and read back: a folder with a mask,
/// a layer wrapped in one, a clipped run and a hidden layer, saved through the ordinary writer and loaded
/// through the ordinary reader, with the same picture at the end.
/// </summary>
public class LayerVerbRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorLayers-" + Guid.NewGuid().ToString("N"));

    public LayerVerbRoundTripTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ImageLayer Patch(SKColor colour, string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(12, 8));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new Model.LayerTransform(0, 0, 12, 8), name);
    }

    private string PathIn(string name) => Path.Combine(_root, name);

    [Fact]
    public void ADocumentBuiltByTheLayerVerbsSavesLoadsAndPaintsTheSame()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 12, 8);
        var backdrop = Patch(new SKColor(30, 60, 120), "Backdrop");
        document.Layers.Add(backdrop);
        // A clip: the patch takes the backdrop's coverage.
        var patch = Patch(new SKColor(220, 120, 40), "Patch");
        document.Layers.Add(patch);
        Assert.True(LayerMaskEdits.Toggle(document, patch.ID));
        // A layer wrapped in a new folder, with a mask the folder carries into what it holds.
        var loose = Patch(new SKColor(60, 200, 90), "Loose");
        document.Layers.Add(loose);
        var folder = LayerPlacement.GroupSelected(document, [loose.ID]);
        Assert.NotNull(folder);
        Assert.True(LayerMaskEdits.Add(document, folder.Value, revealing: true));
        // A layer with no pixels of its own, made with the folder selected, so it goes inside the folder.
        Assert.NotNull(LayerPlacement.AddBlank(document, folder.Value));
        // A hidden layer: hidden is saved, and comes back hidden.
        Assert.True(LayerEdits.SetVisible(document, patch.ID, false));

        using var before = DocumentRenderer.Render(document);
        var package = PathIn("Verbs.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);

        using var loaded = ProjectStore.Load(package);
        using var after = loaded.ToDocument();

        // Every parent, clip, mask and flag came back as it went in.
        Assert.Equal(document.Layers.Count, after.Layers.Count);
        for (var index = 0; index < document.Layers.Count; index++)
        {
            var was = document.Layers[index];
            var now = after.Layers[index];
            Assert.Equal(was.ID, now.ID);
            Assert.Equal(was.Name, now.Name);
            Assert.Equal(was.ParentID, now.ParentID);
            Assert.Equal(was.MaskSourceID, now.MaskSourceID);
            Assert.Equal(was.IsGroup, now.IsGroup);
            Assert.Equal(was.IsVisible, now.IsVisible);
            Assert.Equal(was.Transform, now.Transform);
            Assert.Equal(was.Mask is null, now.Mask is null);
        }
        // And the canvas is the same picture, to the byte: the folder's mask, the hidden patch and the
        // empty layer all survived the trip.
        using var replayed = DocumentRenderer.Render(after);
        Assert.Equal(new SKColor(60, 200, 90), replayed.GetPixel(6, 4));
        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                Assert.Equal(before.GetPixel(x, y), replayed.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void ANewlyMadeFolderAndBlankLayerAreEmptyButStillSave()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 12, 8);
        var backdrop = Patch(new SKColor(30, 60, 120), "Backdrop");
        document.Layers.Add(backdrop);
        Assert.NotNull(LayerPlacement.AddFolder(document, backdrop.ID));
        Assert.NotNull(LayerPlacement.AddBlank(document, backdrop.ID));

        var package = PathIn("Empty.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
        using var loaded = ProjectStore.Load(package);

        // A folder and a layer with no pixels are records with no image file, not missing records.
        var folder = loaded.Manifest.Layers.Single(layer => layer.IsGroup == true);
        Assert.Null(folder.ImageFile);
        Assert.Equal("Folder 1", folder.Name);
        Assert.Equal(2, loaded.Manifest.Layers.Count(layer => layer.ImageFile is null));
        Assert.Single(loaded.Images);
    }
}
