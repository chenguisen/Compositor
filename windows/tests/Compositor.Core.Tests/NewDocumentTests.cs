using Compositor.Core.Document;
using Compositor.Core.Model;

namespace Compositor.Core.Tests;

/// <summary>
/// File ▸ New: a blank canvas of a chosen size with one empty layer over it, which is what a project starts
/// from before anything is imported or painted.
/// </summary>
public class NewDocumentTests
{
    [Fact]
    public void ANewDocumentIsBlankAndTheSizeItWasAskedFor()
    {
        using var document = LayerPlacement.NewDocument(640, 480);
        Assert.NotNull(document);
        Assert.Equal(640, document.Width);
        Assert.Equal(480, document.Height);
        Assert.Equal(72, document.Resolution);
        // One layer, empty, covering the canvas: it holds no pixels until the first paint, as the Mac build's
        // new document does, so nothing has to be allocated for a canvas nobody has drawn on yet.
        var layer = Assert.Single(document.Layers);
        Assert.Equal("Layer 1", layer.Name);
        Assert.Null(layer.Asset);
        Assert.Equal(0, layer.Transform.X);
        Assert.Equal(640, layer.Transform.Width);
        Assert.Equal(480, layer.Transform.Height);
    }

    [Fact]
    public void ANewDocumentKeepsTheResolutionItWasGiven()
    {
        using var document = LayerPlacement.NewDocument(100, 100, 300);
        Assert.NotNull(document);
        Assert.Equal(300, document.Resolution);
    }

    [Fact]
    public void ASizeADocumentCannotBeIsRefused()
    {
        Assert.Null(LayerPlacement.NewDocument(0, 100));
        Assert.Null(LayerPlacement.NewDocument(100, 0));
        Assert.Null(LayerPlacement.NewDocument(-5, 100));
        Assert.Null(LayerPlacement.NewDocument(DocumentLimits.MaxSide + 1, 10));
        Assert.Null(LayerPlacement.NewDocument(10, DocumentLimits.MaxSide + 1));
        // Wider and taller than a surface may be, even though each side is allowed.
        Assert.Null(LayerPlacement.NewDocument(DocumentLimits.MaxSide, DocumentLimits.MaxSide));
    }

    [Fact]
    public void ANewDocumentCanBeSavedAndReadBack()
    {
        // It goes through the project format like any other document: a canvas with an empty layer is a
        // project, not a special case.
        var path = Path.Combine(Path.GetTempPath(), "compositor-new-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            using (var document = LayerPlacement.NewDocument(320, 200, 144))
            {
                Assert.NotNull(document);
                IO.ProjectStore.Save(IO.ProjectSnapshot.FromDocument(document), path);
            }
            using var read = IO.ProjectStore.Load(path).ToDocument();
            Assert.Equal(320, read.Width);
            Assert.Equal(200, read.Height);
            Assert.Equal(144, read.Resolution);
            var layer = Assert.Single(read.Layers);
            Assert.Equal("Layer 1", layer.Name);
            Assert.Null(layer.Asset);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
