using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The whole chain the command line tool walks: a project on disk, read back, turned into a document and
/// flattened. It is the only test that goes through the format and the compositor in one go.
/// </summary>
public class ProjectRenderTests : ProjectTestBase
{
    [Fact]
    public void AProjectOnDiskRendersToItsLayers()
    {
        var background = ImageLayer(BackgroundID, "Background");
        background.Transform = Concrete(0, 0, 8, 6);
        var top = ImageLayer(ClippedID, "Top");
        top.Transform = Concrete(0, 0, 8, 6);
        top.BlendMode = LayerBlendMode.Multiply;
        var manifest = Manifest(11, background, top);
        manifest.Width = 8;
        manifest.Height = 6;

        using var snapshot = new ProjectSnapshot(manifest);
        snapshot.Images[BackgroundID] = Asset(8, 6, new SKColor(200, 200, 200), "Background");
        snapshot.Images[ClippedID] = Asset(8, 6, new SKColor(128, 128, 128), "Top");
        var package = PathIn("Render.comp");
        ProjectStore.Save(snapshot, package);

        using var loaded = ProjectStore.Load(package);
        using var document = loaded.ToDocument();
        using var result = DocumentRenderer.Render(document);

        Assert.Equal(8, result.Width);
        Assert.Equal(6, result.Height);
        // Multiply of 128 over 200 is 200·128/255 ≈ 100, and nothing else was drawn.
        var pixel = result.GetPixel(4, 3);
        Assert.InRange(pixel.Red, 99, 101);
        Assert.InRange(pixel.Green, 99, 101);
        Assert.Equal(255, pixel.Alpha);
    }

    [Fact]
    public void AProjectWhoseCanvasIsBiggerThanItsLayersRendersTheLayersWhereTheySit()
    {
        var layer = ImageLayer(BackgroundID, "Patch");
        layer.Transform = Concrete(2, 1, 3, 2);
        var manifest = Manifest(11, layer);
        manifest.Width = 8;
        manifest.Height = 6;

        using var snapshot = new ProjectSnapshot(manifest);
        snapshot.Images[BackgroundID] = Asset(3, 2, SKColors.Red, "Patch");
        var package = PathIn("Patch.comp");
        ProjectStore.Save(snapshot, package);

        using var loaded = ProjectStore.Load(package);
        using var document = loaded.ToDocument();
        using var result = DocumentRenderer.Render(document);

        Assert.Equal(255, result.GetPixel(2, 1).Alpha);
        Assert.Equal(255, result.GetPixel(4, 2).Alpha);
        Assert.Equal(0, result.GetPixel(1, 1).Alpha);
        Assert.Equal(0, result.GetPixel(2, 0).Alpha);
    }
}
