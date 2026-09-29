using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The crop tool's frame: the rectangle a drag makes, the handles it is adjusted by, the ratios it is held
/// to, and the edges it lines up with.
/// </summary>
public class CropEditsTests
{
    private static CanvasDocument Doc(int width = 200, int height = 100)
    {
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        return document;
    }

    private static ImageLayer Patch(int x, int y, int width, int height, string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new LayerTransform(x, y, width, height), name);
    }

    [Fact]
    public void ADragMakesAFrameOfWholePixels()
    {
        var frame = CropEdits.Create(new SKPoint(10, 20), new SKPoint(60, 70), null, symmetric: false);
        Assert.Equal(SKRectI.Create(10, 20, 50, 50), frame);
        // Dragging up and to the left makes the same frame as dragging down and to the right.
        Assert.Equal(frame, CropEdits.Create(new SKPoint(60, 70), new SKPoint(10, 20), null, false));
        // A drag that lands between pixels is taken to the nearest, halves away from zero.
        Assert.Equal(SKRectI.Create(11, 21, 50, 50),
            CropEdits.Create(new SKPoint(10.5f, 20.5f), new SKPoint(60.5f, 70.5f), null, false));
    }

    [Fact]
    public void ARatioFitsTheDragToItself()
    {
        // Twice as wide as tall: the width of the drag wins.
        var frame = CropEdits.Create(new SKPoint(0, 0), new SKPoint(100, 20), 2, symmetric: false);
        Assert.Equal(100, frame.Width);
        Assert.Equal(50, frame.Height);
        // The taller drag sets the width instead.
        var tall = CropEdits.Create(new SKPoint(0, 0), new SKPoint(10, 100), 2, symmetric: false);
        Assert.Equal(200, tall.Width);
        Assert.Equal(100, tall.Height);
    }

    [Fact]
    public void DraggingFromTheMiddleGrowsTheFrameBothWays()
    {
        var frame = CropEdits.Create(new SKPoint(50, 50), new SKPoint(70, 60), null, symmetric: true);
        Assert.Equal(SKRectI.Create(30, 40, 40, 20), frame);
    }

    [Fact]
    public void AnEmptyDragStillLeavesAPixel()
    {
        var frame = CropEdits.Create(new SKPoint(10, 10), new SKPoint(10, 10), null, symmetric: false);
        Assert.Equal(1, frame.Width);
        Assert.Equal(1, frame.Height);
    }

    [Fact]
    public void ACornerDragsTheFrameWithTheOppositeCornerHeld()
    {
        var original = SKRectI.Create(20, 20, 60, 40);
        var frame = CropEdits.Resize(original, TransformHandle.BottomRight,
            TransformEdits.Position(CropEdits.Box(original), TransformHandle.BottomRight),
            new SKPoint(100, 80), ratio: null, symmetric: false);
        Assert.Equal(20, frame.Left);
        Assert.Equal(20, frame.Top);
        Assert.Equal(80, frame.Width);
        Assert.Equal(60, frame.Height);
    }

    [Fact]
    public void AnEdgeDragMovesOnlyThatEdge()
    {
        var original = SKRectI.Create(20, 20, 60, 40);
        var frame = CropEdits.Resize(original, TransformHandle.Right,
            TransformEdits.Position(CropEdits.Box(original), TransformHandle.Right),
            new SKPoint(60, 40), ratio: null, symmetric: false);
        Assert.Equal(20, frame.Left);
        Assert.Equal(40, frame.Width);
        Assert.Equal(40, frame.Height);
    }

    [Fact]
    public void AResizeWithARatioKeepsTheShape()
    {
        var original = CropEdits.ApplyRatio(SKRectI.Create(0, 0, 80, 40), 2);
        var frame = CropEdits.Resize(original, TransformHandle.BottomRight,
            TransformEdits.Position(CropEdits.Box(original), TransformHandle.BottomRight),
            new SKPoint(200, 60), ratio: 2, symmetric: false);
        Assert.Equal(2.0, (double)frame.Width / frame.Height, 1);
    }

    [Fact]
    public void MovingKeepsTheSize()
    {
        var frame = CropEdits.Move(SKRectI.Create(10, 10, 30, 20), new SKPoint(10, 10), new SKPoint(25, 4));
        Assert.Equal(SKRectI.Create(25, 4, 30, 20), frame);
    }

    [Fact]
    public void ARatioKeepsTheWidthAndTheMiddle()
    {
        var frame = CropEdits.ApplyRatio(SKRectI.Create(10, 20, 100, 100), 2);
        Assert.Equal(10, frame.Left);
        Assert.Equal(100, frame.Width);
        Assert.Equal(50, frame.Height);
        // The middle it had is still the middle it has.
        Assert.Equal(70, frame.MidY);
    }

    [Fact]
    public void TheOriginalRatioIsTheDocumentsOwnShape()
    {
        using var document = Doc(200, 100);
        Assert.Equal(2.0, CropEdits.OriginalRatio(document));
        Assert.Equal(50, CropEdits.ApplyRatio(SKRectI.Create(0, 0, 100, 100), CropEdits.OriginalRatio(document)).Height);
    }

    [Fact]
    public void AFrameIsHeldToTheWholeOfWhatItMayBe()
    {
        Assert.True(CropEdits.Valid(SKRectI.Create(0, 0, 1, 1)));
        Assert.True(CropEdits.Valid(SKRectI.Create(-50, -50, 100, 100)));
        Assert.False(CropEdits.Valid(SKRectI.Create(0, 0, 0, 10)));
        Assert.False(CropEdits.Valid(SKRectI.Create(0, 0, 40_000, 10)));
    }

    [Fact]
    public void ADraggedEdgeSnapsToTheCanvasOrALayer()
    {
        using var document = Doc();
        document.Layers.Add(Patch(70, 10, 50, 50, "One"));
        // The left edge dragged to within a pixel of the other layer's left edge.
        var frame = SKRectI.Create(71, 0, 40, 100);
        var snapped = CropEdits.Snap(document, frame, new SKPoint(71, 50), new SKPoint(71, 50), symmetric: false,
            tolerance: 2, out var lineX, out _);
        Assert.Equal(70, snapped.Left);
        Assert.Equal(70, lineX);
        // The right edge is untouched: only the dragged edge moves.
        Assert.Equal(111, snapped.Right);
    }

    [Fact]
    public void TheRightEdgeSnapsWhenThatIsTheOneBeingDragged()
    {
        using var document = Doc();
        var frame = SKRectI.Create(0, 0, 199, 100);
        var snapped = CropEdits.Snap(document, frame, new SKPoint(199, 50), new SKPoint(199, 50), symmetric: false,
            tolerance: 2, out var lineX, out _);
        Assert.Equal(200, snapped.Right);
        Assert.Equal(200, lineX);
        Assert.Equal(0, snapped.Left);
    }

    [Fact]
    public void AMovedFrameSnapsAndKeepsItsSize()
    {
        using var document = Doc();
        var frame = SKRectI.Create(1, 0, 40, 100);
        var snapped = CropEdits.SnapMove(document, frame, tolerance: 2, out var lineX, out _);
        Assert.Equal(0, snapped.Left);
        Assert.Equal(0, lineX);
        Assert.Equal(40, snapped.Width);
        Assert.Equal(100, snapped.Height);
    }

    [Fact]
    public void ASymmetricDragMirrorsTheSnappedEdge()
    {
        using var document = Doc();
        // The frame's middle is at 50; the left edge dragged to within a pixel of 0.
        var frame = SKRectI.Create(1, 0, 99, 100);
        var snapped = CropEdits.Snap(document, frame, new SKPoint(1, 50), new SKPoint(50, 50), symmetric: true,
            tolerance: 2, out var lineX, out _);
        Assert.Equal(0, snapped.Left);
        Assert.Equal(100, snapped.Right);
        Assert.Equal(0, lineX);
    }

    [Fact]
    public void AnEdgeWithNothingNearItIsLeftAlone()
    {
        using var document = Doc(1000, 1000);
        var frame = SKRectI.Create(400, 400, 100, 100);
        var snapped = CropEdits.Snap(document, frame, new SKPoint(400, 450), new SKPoint(400, 450), false, 2,
            out var lineX, out var lineY);
        Assert.Equal(frame, snapped);
        Assert.Null(lineX);
        Assert.Null(lineY);
    }

    [Fact]
    public void CropTargetsAreEdgesAndNotMiddles()
    {
        using var document = Doc();
        document.Layers.Add(Patch(70, 10, 50, 50, "One"));
        var (xs, ys) = CropEdits.Targets(document);
        Assert.Contains(0d, xs);
        Assert.Contains(200d, xs);
        Assert.Contains(70d, xs);
        Assert.Contains(120d, xs);
        // The canvas middle is not a crop target.
        Assert.DoesNotContain(100d, xs);
        Assert.Contains(60d, ys);
    }

    [Fact]
    public void AFrameOffTheCanvasIsStillACrop()
    {
        // The frame may reach past the canvas; what is outside it is simply not in the picture.
        using var document = Doc(200, 100);
        var frame = SKRectI.Create(-10, -10, 100, 100);
        Assert.True(CropEdits.Valid(frame));
        Assert.True(CanvasEdits.Crop(document, frame));
        Assert.Equal(100, document.Width);
        Assert.Equal(100, document.Height);
    }
}
