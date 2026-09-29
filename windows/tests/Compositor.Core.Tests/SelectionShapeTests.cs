using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The selection shapes beyond a rectangle — ellipse, lasso, wand — and what Modify, Inverse and Move do
/// to them. Coverage is read back as the gray level each document pixel is given.
/// </summary>
public class SelectionShapeTests
{
    private static CanvasDocument Doc(int width = 20, int height = 20)
    {
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        document.Layers.Add(Layer(left: SKColors.Red, right: SKColors.Blue, width, height, x: 0, y: 0));
        return document;
    }

    private static ImageLayer Layer(SKColor left, SKColor right, int width, int height, double x, double y)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = left };
            canvas.DrawRect(SKRect.Create(0, 0, width / 2f, height), paint);
            paint.Color = right;
            canvas.DrawRect(SKRect.Create(width / 2f, 0, width / 2f, height), paint);
        }
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Half"),
            new Model.LayerTransform(x, y, width, height), "Half");
    }

    /// <summary>How much of a document pixel the selection lets through: 0 to 255.</summary>
    private static int CoverageAt(CanvasDocument document, int x, int y)
    {
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        using var coverage = document.Selection.Coverage(region);
        if (coverage is null) return 255;
        var column = x - region.Left;
        var row = y - region.Top;
        if (column < 0 || row < 0 || column >= region.Width || row >= region.Height) return 0;
        return coverage.GetPixelSpan()[row * coverage.RowBytes + column];
    }

    [Fact]
    public void AnEllipseTakesTheOvalAndLeavesTheCorners()
    {
        using var document = Doc();
        Assert.True(SelectionEdits.SelectEllipse(document, SKRectI.Create(0, 0, 10, 10)));

        Assert.Equal(255, CoverageAt(document, 5, 5));
        Assert.Equal(255, CoverageAt(document, 5, 4));
        Assert.Equal(255, CoverageAt(document, 4, 5));
        // The four corners of the box the oval is drawn in are outside it. A pixel the rim only clips is
        // partly taken, which is what the antialiased edge is for.
        Assert.Equal(0, CoverageAt(document, 0, 0));
        Assert.Equal(0, CoverageAt(document, 9, 0));
        Assert.Equal(0, CoverageAt(document, 0, 9));
        Assert.Equal(0, CoverageAt(document, 9, 9));
        Assert.False(SelectionEdits.SelectEllipse(document, SKRectI.Create(0, 0, 10, 10)));
    }

    [Fact]
    public void AnEllipseIsHeldToTheCanvas()
    {
        using var document = Doc();
        Assert.True(SelectionEdits.SelectEllipse(document, SKRectI.Create(-5, -5, 20, 20)));
        var bounds = document.Selection.Path!.Bounds;
        Assert.Equal(0, bounds.Left);
        Assert.Equal(0, bounds.Top);
        Assert.Equal(15, bounds.Right);
        Assert.Equal(15, bounds.Bottom);
    }

    [Fact]
    public void ALassoTakesTheOutlineThroughItsPointsAndClosesIt()
    {
        using var document = Doc();
        var triangle = new[]
        {
            new SKPoint(2, 2), new SKPoint(14, 2), new SKPoint(2, 14),
        };
        Assert.True(SelectionEdits.SelectLasso(document, triangle));

        // Inside the triangle, and outside it on either side of the hypotenuse.
        Assert.Equal(255, CoverageAt(document, 3, 3));
        Assert.Equal(255, CoverageAt(document, 2, 12));
        Assert.Equal(0, CoverageAt(document, 12, 12));
        Assert.Equal(0, CoverageAt(document, 15, 15));
    }

    [Fact]
    public void ALassoTakesLessThanThreePointsAsNothing()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(2, 2, 4, 4));

        // A line encloses nothing, so the selection is let go rather than narrowed to nothing.
        Assert.True(SelectionEdits.SelectLasso(document, [new SKPoint(4, 4), new SKPoint(6, 6)]));
        Assert.Null(document.Selection.Path);
        Assert.Equal(255, CoverageAt(document, 5, 5));
    }

    [Fact]
    public void TheWandTakesEverythingLikeThePixelItStartsOn()
    {
        using var document = Doc();
        using var sample = SelectionEdits.Sample(document, null)!;
        Assert.True(SelectionEdits.SelectWand(document, sample, 2, 5, new WandOptions(), SelectionMode.Replace));

        // The left half is one colour and the right half another, so only the left is taken.
        Assert.Equal(255, CoverageAt(document, 1, 1));
        Assert.Equal(255, CoverageAt(document, 9, 9));
        Assert.Equal(0, CoverageAt(document, 10, 1));
        Assert.Equal(0, CoverageAt(document, 19, 19));
        // The outline runs along pixel edges, so it is the exact half.
        Assert.Equal(0, document.Selection.Path!.Bounds.Left);
        Assert.Equal(10, document.Selection.Path.Bounds.Right);
    }

    [Fact]
    public void TheWandCanTakeSeparatedPatchesOfTheSameColour()
    {
        using var document = Doc(30, 10);
        document.Layers.Clear();
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(30, 10));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(SKRect.Create(0, 0, 10, 10), paint);
            paint.Color = SKColors.Green;
            canvas.DrawRect(SKRect.Create(10, 0, 10, 10), paint);
            paint.Color = SKColors.Red;
            canvas.DrawRect(SKRect.Create(20, 0, 10, 10), paint);
        }
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Bands"),
            new Model.LayerTransform(0, 0, 30, 10), "Bands"));
        using var sample = SelectionEdits.Sample(document, null)!;

        // Contiguous takes only the red run the seed is in.
        Assert.True(SelectionEdits.SelectWand(document, sample, 2, 5, new WandOptions(Contiguous: true),
            SelectionMode.Replace));
        Assert.Equal(255, CoverageAt(document, 2, 5));
        Assert.Equal(0, CoverageAt(document, 22, 5));

        // Not contiguous takes every red patch.
        Assert.True(SelectionEdits.SelectWand(document, sample, 2, 5, new WandOptions(Contiguous: false),
            SelectionMode.Replace));
        Assert.Equal(255, CoverageAt(document, 2, 5));
        Assert.Equal(255, CoverageAt(document, 22, 5));
        Assert.Equal(0, CoverageAt(document, 15, 5));
    }

    [Fact]
    public void TheWandReadsOneLayerOnItsOwnWhenAskedForIt()
    {
        using var document = Doc(20, 20);
        // Offset by five, so the layer's own green half covers 5 to 15 while the canvas shows the layer
        // beneath it. The wand's reference is an average over a square, so the seed is kept well inside.
        var top = Layer(SKColors.Green, SKColors.Yellow, 20, 20, x: 5, y: 5);
        top.IsVisible = false;
        document.Layers.Add(top);
        using var sample = SelectionEdits.Sample(document, top.ID)!;

        // The sample is only that layer's pixels, sitting where the layer's transform puts them.
        Assert.True(SelectionEdits.SelectWand(document, sample, 9, 9, new WandOptions(), SelectionMode.Replace));
        Assert.Equal(255, CoverageAt(document, 9, 9));
        Assert.Equal(255, CoverageAt(document, 14, 9));
        // Nothing of the layer's yellow half, and nothing of the layer below it.
        Assert.Equal(0, CoverageAt(document, 16, 9));
        Assert.Equal(0, CoverageAt(document, 2, 2));
    }

    [Fact]
    public void InverseTakesTheRestOfTheCanvas()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));

        Assert.True(SelectionEdits.Invert(document));
        Assert.Equal(255, CoverageAt(document, 0, 0));
        Assert.Equal(255, CoverageAt(document, 19, 19));
        Assert.Equal(0, CoverageAt(document, 10, 10));
        // Inverting what is already nothing but a canvas-wide selection leaves nothing.
        SelectionEdits.SelectAll(document);
        Assert.True(SelectionEdits.Invert(document));
        Assert.True(document.Selection.IsEmpty);
        Assert.Equal(0, CoverageAt(document, 10, 10));
    }

    [Fact]
    public void InvertingNoSelectionDoesNothing()
    {
        using var document = Doc();
        Assert.False(SelectionEdits.Invert(document));
    }

    [Fact]
    public void ShapesAreAddedToTheSelectionAndTakenOutOfIt()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 5, 5));
        var second = Rectangle(10, 10, 5, 5);

        Assert.True(SelectionEdits.Apply(document, second, SelectionMode.Add));
        Assert.Equal(255, CoverageAt(document, 2, 2));
        Assert.Equal(255, CoverageAt(document, 12, 12));
        Assert.Equal(0, CoverageAt(document, 7, 7));

        Assert.True(SelectionEdits.Apply(document, second, SelectionMode.Subtract));
        Assert.Equal(255, CoverageAt(document, 2, 2));
        Assert.Equal(0, CoverageAt(document, 12, 12));
    }

    [Fact]
    public void SubtractingFromNoSelectionChangesNothing()
    {
        using var document = Doc();
        Assert.False(SelectionEdits.Apply(document, Rectangle(2, 2, 4, 4), SelectionMode.Subtract));
        Assert.Null(document.Selection.Path);
    }

    [Fact]
    public void AddingToNothingTakesTheShapeItself()
    {
        using var document = Doc();
        Assert.True(SelectionEdits.Apply(document, Rectangle(10, 10, 5, 5), SelectionMode.Add));
        Assert.Equal(255, CoverageAt(document, 12, 12));
        Assert.Equal(0, CoverageAt(document, 2, 2));
    }

    [Fact]
    public void ExpandGrowsTheOutlineAndContractShrinksIt()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));

        Assert.True(SelectionEdits.Expand(document, 2));
        Assert.Equal(255, CoverageAt(document, 3, 10));
        Assert.Equal(255, CoverageAt(document, 10, 16));
        Assert.Equal(0, CoverageAt(document, 2, 10));
        var grown = document.Selection.Path!.Bounds;
        Assert.Equal(3, grown.Left);
        Assert.Equal(17, grown.Right);

        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));
        Assert.True(SelectionEdits.Contract(document, 2));
        Assert.Equal(255, CoverageAt(document, 7, 10));
        Assert.Equal(255, CoverageAt(document, 12, 10));
        Assert.Equal(0, CoverageAt(document, 6, 10));
        Assert.Equal(0, CoverageAt(document, 14, 10));
        Assert.Equal(SKRect.Create(7, 7, 6, 6), document.Selection.Path!.Bounds);
    }

    [Fact]
    public void ContractingPastTheMiddleLeavesNothingSelected()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));

        Assert.True(SelectionEdits.Contract(document, 8));
        Assert.True(document.Selection.IsEmpty);
        Assert.Equal(0, CoverageAt(document, 10, 10));
    }

    [Fact]
    public void ExpandAndContractNeedASelectionAndAnAmount()
    {
        using var document = Doc();
        Assert.False(SelectionEdits.Expand(document, 2));
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));
        Assert.False(SelectionEdits.Expand(document, 0));
        Assert.False(SelectionEdits.Expand(document, SelectionEdits.MaxAmount + 1));
        // The rectangle is untouched by a refusal.
        Assert.Equal(SKRect.Create(5, 5, 10, 10), document.Selection.Path!.Bounds);
    }

    [Fact]
    public void FeatherSoftensTheEdgeEitherSideOfTheOutline()
    {
        using var document = Doc(60, 40);
        SelectionEdits.Select(document, SKRectI.Create(20, 10, 20, 20));

        Assert.True(SelectionEdits.Feather(document, 6));
        Assert.Equal(6, document.Selection.Feather);
        // The middle of a selection this size is still whole, since a feather six pixels deep reaches about
        // nine pixels either way; just outside the edge is now partly taken; far outside is not.
        Assert.True(CoverageAt(document, 30, 20) >= 250, $"the middle is {CoverageAt(document, 30, 20)}");
        var across = CoverageAt(document, 19, 20);
        Assert.True(across is > 20 and < 235, $"just outside the edge is {across}, not a partial value");
        Assert.Equal(0, CoverageAt(document, 5, 20));

        // A second feather softens further, but less than the two added together.
        Assert.True(SelectionEdits.Feather(document, 8));
        Assert.Equal(10, document.Selection.Feather);
    }

    [Fact]
    public void AFeatheredSelectionReachingTheCanvasEdgeStaysSolidThere()
    {
        using var document = Doc(40, 20);
        SelectionEdits.SelectAll(document);
        Assert.True(SelectionEdits.Feather(document, 6));

        // What lies beyond the canvas is read as the pixels at its edge, not as emptiness, so the outline is
        // not faded away where it runs along the border — clampedToExtent, as the Mac build blurs it.
        Assert.Equal(255, CoverageAt(document, 0, 10));
        Assert.Equal(255, CoverageAt(document, 39, 10));
        Assert.Equal(255, CoverageAt(document, 20, 0));
        Assert.Equal(255, CoverageAt(document, 20, 19));
        Assert.Equal(255, CoverageAt(document, 20, 10));
    }

    [Fact]
    public void FeatherNeedsASelectionAndAnAmount()
    {
        using var document = Doc();
        Assert.False(SelectionEdits.Feather(document, 4));
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));
        Assert.False(SelectionEdits.Feather(document, 0));
        Assert.Equal(0, document.Selection.Feather);
    }

    [Fact]
    public void MovingTheSelectionMovesTheOutlineAndNothingElse()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));

        Assert.True(SelectionEdits.Move(document, 3, -2));
        Assert.Equal(SKRect.Create(8, 3, 10, 10), document.Selection.Path!.Bounds);
        Assert.Equal(255, CoverageAt(document, 10, 5));
        Assert.Equal(0, CoverageAt(document, 6, 5));
        // Moving by nothing is not a change.
        Assert.False(SelectionEdits.Move(document, 0, 0));
    }

    [Fact]
    public void AnOutlineMayBeMovedOffTheCanvasAndBack()
    {
        using var document = Doc();
        SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10));

        // Second row of pixels with nothing selected at all.
        Assert.True(SelectionEdits.Move(document, 30, 0));
        Assert.Equal(SKRect.Create(35, 5, 10, 10), document.Selection.Path!.Bounds);
        // Nothing of it is on the canvas, so nothing may be edited.
        Assert.Equal(0, CoverageAt(document, 10, 10));
        Assert.Equal(SKRectI.Create(0, 0, 0, 0), document.Selection.CoverageRect(20, 20));

        Assert.True(SelectionEdits.Move(document, -30, 0));
        Assert.Equal(SKRect.Create(5, 5, 10, 10), document.Selection.Path!.Bounds);
    }

    [Fact]
    public void PaintingAnEllipseOnlyTakesTheOval()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(SKColors.White);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Paint"),
            new Model.LayerTransform(0, 0, 40, 20), "Paint");
        document.Layers.Add(layer);
        SelectionEdits.SelectEllipse(document, SKRectI.Create(10, 0, 20, 20));

        var stroke = new[] { new SKPoint(0.5f, 10.5f), new SKPoint(39.5f, 10.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, new BrushSettings(Diameter: 8, Red: 1)));

        // The middle of the oval is painted; the ends of the stroke, outside it, are not.
        Assert.Equal(0, layer.Asset!.Image.GetPixel(20, 10).Green);
        Assert.Equal(255, layer.Asset.Image.GetPixel(4, 10).Green);
        Assert.Equal(255, layer.Asset.Image.GetPixel(35, 10).Green);
        // And a pixel the oval does not reach is left alone.
        Assert.Equal(255, layer.Asset.Image.GetPixel(12, 1).Green);
    }

    [Fact]
    public void AFeatheredEdgePaintsPartOfTheWay()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(SKColors.White);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Paint"),
            new Model.LayerTransform(0, 0, 40, 20), "Paint");
        document.Layers.Add(layer);
        SelectionEdits.Select(document, SKRectI.Create(10, 0, 20, 20));
        SelectionEdits.Feather(document, 6);

        var stroke = new[] { new SKPoint(0.5f, 10.5f), new SKPoint(39.5f, 10.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, new BrushSettings(Diameter: 2, Red: 1)));

        // The middle is painted solid; a pixel just outside the edge takes part of the colour; and the tail
        // of the feather runs out before the ends of the stroke, which are left alone.
        Assert.Equal(0, layer.Asset!.Image.GetPixel(20, 10).Green);
        var faded = layer.Asset.Image.GetPixel(9, 10).Green;
        Assert.True(faded is > 0 and < 255, $"the feathered edge is {faded}, not a partial value");
        Assert.Equal(255, layer.Asset.Image.GetPixel(39, 10).Green);
        Assert.Equal(255, layer.Asset.Image.GetPixel(0, 10).Green);
    }

    [Fact]
    public void SelectingIsStillOneUndoStep()
    {
        using var document = Doc();
        var history = new DocumentHistory();

        history.Begin("Elliptical Marquee", document, null);
        SelectionEdits.SelectEllipse(document, SKRectI.Create(0, 0, 10, 10));
        history.End(document, null);
        Assert.True(history.CanUndo);

        // An ellipse and a different ellipse of the same size compare as different selections, so the
        // second one is a step of its own rather than being taken for no change.
        history.Begin("Elliptical Marquee", document, null);
        SelectionEdits.Apply(document, Oval(4, 4, 10, 10), SelectionMode.Replace);
        history.End(document, null);
        Assert.Equal(2, history.UndoCount);
    }

    private static SKPath Rectangle(float x, float y, float width, float height)
    {
        using var builder = new SKPathBuilder();
        builder.AddRect(SKRect.Create(x, y, width, height), SKPathDirection.Clockwise);
        return builder.Detach();
    }

    private static SKPath Oval(float x, float y, float width, float height)
    {
        using var builder = new SKPathBuilder();
        builder.AddOval(SKRect.Create(x, y, width, height), SKPathDirection.Clockwise);
        return builder.Detach();
    }
}
