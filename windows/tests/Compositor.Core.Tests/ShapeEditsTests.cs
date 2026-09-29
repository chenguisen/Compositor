using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The Shape tool: the box a drag makes, the pixels a shape draws, and that a shape layer is still a shape
/// after it is scaled — which is what keeps a rounded corner round.
/// </summary>
public class ShapeEditsTests
{
    private static LayerShapeStyle Style(ShapeKind kind, double red = 1, double green = 0, double blue = 0) => new()
    {
        Kind = kind,
        Red = red,
        Green = green,
        Blue = blue,
        CornerRadius = 0,
    };

    [Fact]
    public void ADragMakesABoxOfWholePixels()
    {
        var box = ShapeEdits.Box(new SKPoint(10, 20), new SKPoint(60, 70), square: false, fromCentre: false);
        Assert.Equal(SKRectI.Create(10, 20, 50, 50), box);
        // Dragging up and to the left makes the same box.
        Assert.Equal(box, ShapeEdits.Box(new SKPoint(60, 70), new SKPoint(10, 20), false, false));
    }

    [Fact]
    public void ShiftMakesASquareAndAltGrowsFromTheStart()
    {
        var square = ShapeEdits.Box(new SKPoint(10, 20), new SKPoint(60, 30), square: true, fromCentre: false);
        Assert.Equal(50, square.Width);
        Assert.Equal(50, square.Height);
        // Alt grows it both ways, so the start is the middle.
        var centred = ShapeEdits.Box(new SKPoint(50, 50), new SKPoint(70, 60), square: false, fromCentre: true);
        Assert.Equal(SKRectI.Create(30, 40, 40, 20), centred);
    }

    [Fact]
    public void ShiftPutsALineOnEighthsOfATurn()
    {
        // Nearly flat: the angle lands flat and the drag's length is kept, so the end is a shade further out.
        var flat = ShapeEdits.LineEnd(new SKPoint(0, 0), new SKPoint(100, 4));
        Assert.Equal(0, flat.Y, 3);
        Assert.Equal(Math.Sqrt(100 * 100 + 4 * 4), Math.Sqrt((double)flat.X * flat.X + (double)flat.Y * flat.Y), 3);
        // At about forty-five degrees: it lands on exactly forty-five.
        var diagonal = ShapeEdits.LineEnd(new SKPoint(0, 0), new SKPoint(80, 70));
        Assert.Equal(diagonal.X, diagonal.Y, 3);
    }

    [Fact]
    public void ARectangleIsFilledRightUpToItsEdges()
    {
        using var image = ShapeEdits.Image(Style(ShapeKind.Rectangle), 40, 20)!;
        Assert.Equal(SKAlphaType.Unpremul, image.AlphaType);
        Assert.Equal(new SKColor(255, 0, 0, 255), image.GetPixel(20, 10));
        Assert.Equal(new SKColor(255, 0, 0, 255), image.GetPixel(0, 0));
        Assert.Equal(new SKColor(255, 0, 0, 255), image.GetPixel(39, 19));
    }

    [Fact]
    public void ARoundedRectangleLeavesItsCornersClear()
    {
        var style = Style(ShapeKind.Rectangle);
        style.CornerRadius = 8;
        using var image = ShapeEdits.Image(style, 40, 40)!;
        // The middle of an edge is filled; the very corner is not.
        Assert.Equal(255, image.GetPixel(20, 1).Alpha);
        Assert.Equal(0, image.GetPixel(0, 0).Alpha);
        Assert.Equal(0, image.GetPixel(1, 1).Alpha);
        // A radius of more than half the shorter side makes a pill rather than eating the shape.
        style.CornerRadius = 400;
        using var pill = ShapeEdits.Image(style, 40, 20)!;
        Assert.Equal(255, pill.GetPixel(20, 10).Alpha);
        Assert.Equal(0, pill.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void AnEllipseLeavesTheCornersOfItsBoxClear()
    {
        using var image = ShapeEdits.Image(Style(ShapeKind.Ellipse, 0, 1, 0), 40, 20)!;
        Assert.Equal(new SKColor(0, 255, 0, 255), image.GetPixel(20, 10));
        Assert.Equal(0, image.GetPixel(0, 0).Alpha);
        Assert.Equal(0, image.GetPixel(39, 19).Alpha);
        Assert.Equal(255, image.GetPixel(20, 1).Alpha);
    }

    [Fact]
    public void ALineIsStrokedBetweenTheEndsItWasGiven()
    {
        var style = Style(ShapeKind.Line, 0, 0, 1);
        style.LineWidth = 2;
        style.Start = new JsonPoint(0, 0);
        style.End = new JsonPoint(1, 1);
        using var image = ShapeEdits.Image(style, 40, 40)!;
        // Down the diagonal, and nowhere else.
        Assert.Equal(255, image.GetPixel(20, 20).Alpha);
        Assert.Equal(0, image.GetPixel(35, 4).Alpha);
        Assert.Equal(0, image.GetPixel(4, 35).Alpha);
    }

    [Fact]
    public void AShapeTooBigForOneSurfaceIsRefused()
    {
        Assert.True(ShapeEdits.TooLarge(20_000, 20_000));
        Assert.True(ShapeEdits.TooLarge(0, 10));
        Assert.False(ShapeEdits.TooLarge(200, 100));
        Assert.Null(ShapeEdits.Image(Style(ShapeKind.Rectangle), 20_000, 20_000));
    }

    [Fact]
    public void AShapeLayerIsStillAShape()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 200, 200);
        var backdrop = Importer(document);
        var id = ShapeEdits.Add(document, Style(ShapeKind.Ellipse), SKRectI.Create(20, 30, 60, 40), backdrop.ID);
        Assert.NotNull(id);
        var layer = document.Layers.Single(candidate => candidate.ID == id);
        Assert.Equal("Ellipse 1", layer.Name);
        // The pixels and the style share one bitmap, which is what makes it live.
        Assert.NotNull(layer.LiveShape);
        Assert.Same(layer.Asset!.Image, layer.Shape!.Image);
        // It goes above the layer it was drawn over, where it was dragged.
        Assert.Equal(1, document.Layers.IndexOf(layer));
        Assert.Equal(20, layer.Transform.X);
        Assert.Equal(30, layer.Transform.Y);
        Assert.Equal(60, layer.Asset.Width);
        Assert.Equal(40, layer.Asset.Height);
    }

    [Fact]
    public void ShapeNamesSkipTheOnesTheDocumentIsUsing()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 200, 200);
        var backdrop = Importer(document);
        Assert.NotNull(ShapeEdits.Add(document, Style(ShapeKind.Rectangle), SKRectI.Create(0, 0, 10, 10), backdrop.ID));
        Assert.NotNull(ShapeEdits.Add(document, Style(ShapeKind.Rectangle), SKRectI.Create(0, 0, 10, 10), backdrop.ID));
        Assert.NotNull(ShapeEdits.Add(document, Style(ShapeKind.Ellipse), SKRectI.Create(0, 0, 10, 10), backdrop.ID));
        Assert.Equal(new[] { "Ellipse 1", "Rectangle 1", "Rectangle 2" },
            document.Layers.Skip(1).Select(layer => layer.Name).OrderBy(name => name));
    }

    [Fact]
    public void AShapeDrawnAtANewSizeKeepsItsCornersRound()
    {
        var style = Style(ShapeKind.Rectangle);
        style.CornerRadius = 10;
        using var document = new CanvasDocument(Guid.NewGuid(), 200, 200);
        var id = ShapeEdits.Add(document, style, SKRectI.Create(0, 0, 40, 40), null);
        Assert.NotNull(id);
        var layer = document.Layers.Single();

        // Doubling the canvas doubles the shape's box; the radius stays ten pixels, so it is a tenth of the
        // side where it was a quarter.
        Assert.True(ImageEdits.Resize(document, 400, 400, 72));
        Assert.Equal(80, layer.Transform.Width);
        Assert.Equal(80, layer.Asset!.Width);
        Assert.NotNull(layer.LiveShape);
        // The corner is still clear and the middle still filled, at the new size.
        Assert.Equal(0, layer.Asset.Image.GetPixel(0, 0).Alpha);
        Assert.Equal(255, layer.Asset.Image.GetPixel(40, 2).Alpha);
        // The radius was drawn again at ten pixels rather than stretched to twenty: a pixel four in from the
        // corner is inside a ten-pixel rounding and outside a twenty-pixel one.
        var stretched = Style(ShapeKind.Rectangle);
        stretched.CornerRadius = 20;
        using var comparison = ShapeEdits.Image(stretched, 80, 80)!;
        Assert.Equal(255, layer.Asset.Image.GetPixel(4, 4).Alpha);
        Assert.Equal(0, comparison.GetPixel(4, 4).Alpha);
    }

    [Fact]
    public void AShapeLayerSavesAndLoadsWithItsStyle()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 200, 200);
        var style = Style(ShapeKind.Rectangle, 0, 0, 1);
        style.CornerRadius = 6;
        Assert.NotNull(ShapeEdits.Add(document, style, SKRectI.Create(10, 10, 30, 20), null));

        var root = Path.Combine(Path.GetTempPath(), "CompositorShape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "Shape.comp");
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
            using var loaded = ProjectStore.Load(package);
            var record = loaded.Manifest.Layers.Single();
            Assert.NotNull(record.Shape);
            Assert.Equal(ShapeKind.Rectangle, record.Shape!.Kind);
            Assert.Equal(6, record.Shape.CornerRadius);
            Assert.Equal(1, record.Shape.Blue);
            using var after = loaded.ToDocument();
            Assert.NotNull(after.Layers.Single().LiveShape);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A backdrop layer for a document, so a shape has something to sit above.</summary>
    private static ImageLayer Importer(CanvasDocument document)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(200, 200));
        bitmap.Erase(SKColors.White);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Backdrop"),
            new LayerTransform(0, 0, 200, 200), "Backdrop");
        document.Layers.Add(layer);
        return layer;
    }
}
