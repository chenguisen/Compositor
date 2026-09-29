using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The transform inspector's arithmetic: the handles, the drags they work out, and the snapping that lines a
/// layer up with the canvas, the guides and its neighbours.
/// </summary>
public class TransformEditsTests
{
    /// <summary>A 100 by 50 box at (10, 20), upright.</summary>
    private static LayerTransform Box => new(10, 20, 100, 50);

    private static void Close(SKPoint actual, double x, double y, double tolerance = 0.01)
    {
        Assert.True(Math.Abs(actual.X - x) <= tolerance && Math.Abs(actual.Y - y) <= tolerance,
            $"({actual.X},{actual.Y}) is not ({x},{y})");
    }

    [Fact]
    public void TheEightHandlesSitAroundTheBox()
    {
        Close(TransformEdits.Position(Box, TransformHandle.TopLeft), 10, 20);
        Close(TransformEdits.Position(Box, TransformHandle.Top), 60, 20);
        Close(TransformEdits.Position(Box, TransformHandle.TopRight), 110, 20);
        Close(TransformEdits.Position(Box, TransformHandle.Right), 110, 45);
        Close(TransformEdits.Position(Box, TransformHandle.BottomRight), 110, 70);
        Close(TransformEdits.Position(Box, TransformHandle.Bottom), 60, 70);
        Close(TransformEdits.Position(Box, TransformHandle.BottomLeft), 10, 70);
        Close(TransformEdits.Position(Box, TransformHandle.Left), 10, 45);
    }

    [Fact]
    public void TheHandlesTurnWithTheBox()
    {
        var turned = Box with { Rotation = 90 };
        // A quarter turn clockwise about the middle at (60, 45) carries the top left round to the right of
        // the middle and above it.
        Close(TransformEdits.Position(turned, TransformHandle.TopLeft), 85, -5);
        Close(TransformEdits.Position(turned, TransformHandle.BottomRight), 35, 95);
    }

    [Fact]
    public void TheTurningGripSitsAboveTheTopEdgeAndTurnsWithIt()
    {
        Close(TransformEdits.RotatePosition(Box, 20), 60, 0);
        Close(TransformEdits.RotatePosition(Box with { Rotation = 90 }, 20), 105, 45);
    }

    [Fact]
    public void AClickTakesHoldOfTheNearestHandleOrEdge()
    {
        // On a corner, within the tolerance; and a point on the edge takes that edge's handle.
        Assert.Equal(TransformHandle.TopLeft, TransformEdits.HandleAt(Box, new SKPoint(11, 21), 5, 20));
        Assert.Equal(TransformHandle.Top, TransformEdits.HandleAt(Box, new SKPoint(20, 20), 5, 20));
        // Anywhere along the top edge is the top handle, so the whole side can be dragged.
        Assert.Equal(TransformHandle.Top, TransformEdits.HandleAt(Box, new SKPoint(40, 20.5f), 5, 20));
        Assert.Equal(TransformHandle.Left, TransformEdits.HandleAt(Box, new SKPoint(10.5f, 60), 5, 20));
        // The middle of the box takes hold of nothing.
        Assert.Null(TransformEdits.HandleAt(Box, new SKPoint(60, 45), 5, 20));
        // The turning grip is its own handle.
        Assert.Equal(TransformHandle.Rotate, TransformEdits.HandleAt(Box, new SKPoint(60, 2), 5, 20));
    }

    [Fact]
    public void AMoveShiftsTheWholeBoxAndShiftKeepsItOnOneAxis()
    {
        var moved = TransformEdits.Move(Box, 5, -3);
        Assert.Equal(new LayerTransform(15, 17, 100, 50), moved);
        // The longer part of the drag wins, and the other axis is dropped.
        Assert.Equal(new LayerTransform(18, 20, 100, 50), TransformEdits.Move(Box, 8, -3, axisLock: true));
        Assert.Equal(new LayerTransform(10, 25, 100, 50), TransformEdits.Move(Box, 3, 5, axisLock: true));
    }

    [Fact]
    public void DraggingTheBottomRightHandleResizesAboutTheTopLeft()
    {
        // From the corner itself, ten right and five down: the box grows by exactly that.
        var resized = TransformEdits.Resize(Box, TransformHandle.BottomRight,
            TransformEdits.Position(Box, TransformHandle.BottomRight), new SKPoint(120, 75), lockRatio: false, fromCentre: false);
        Assert.Equal(10, resized.X);
        Assert.Equal(20, resized.Y);
        Assert.Equal(110, resized.Width);
        Assert.Equal(55, resized.Height);
    }

    [Fact]
    public void DraggingAnEdgeHandleMovesOnlyThatEdge()
    {
        var resized = TransformEdits.Resize(Box, TransformHandle.Right,
            TransformEdits.Position(Box, TransformHandle.Right), new SKPoint(70, 45), lockRatio: false, fromCentre: false);
        Assert.Equal(10, resized.X);
        Assert.Equal(60, resized.Width);
        Assert.Equal(50, resized.Height);
    }

    [Fact]
    public void DraggingPastTheFarSideTurnsTheLayerOverRatherThanStoppingAtNothing()
    {
        // The right edge dragged twenty to the left of the left edge: the box is twenty wide and flipped.
        var resized = TransformEdits.Resize(Box, TransformHandle.Right,
            TransformEdits.Position(Box, TransformHandle.Right), new SKPoint(-10, 45), lockRatio: false, fromCentre: false);
        Assert.Equal(20, resized.Width);
        Assert.True(resized.FlipX);
        // It lies on the other side of the anchor, which is the left edge at x = 10.
        Assert.Equal(-10, resized.X);
    }

    [Fact]
    public void AltDragsFromTheMiddleAndGrowsBothWays()
    {
        var resized = TransformEdits.Resize(Box, TransformHandle.BottomRight,
            TransformEdits.Position(Box, TransformHandle.BottomRight), new SKPoint(120, 75), lockRatio: false, fromCentre: true);
        // Ten out from the middle is twenty on the side, so the box grows by that on both sides.
        Assert.Equal(120, resized.Width);
        Assert.Equal(60, resized.Height);
        // And the middle has not moved.
        Assert.Equal(60, resized.CenterX);
        Assert.Equal(45, resized.CenterY);
    }

    [Fact]
    public void LockingTheRatioKeepsTheSidesInProportion()
    {
        var resized = TransformEdits.Resize(Box, TransformHandle.BottomRight,
            TransformEdits.Position(Box, TransformHandle.BottomRight), new SKPoint(220, 70), lockRatio: true, fromCentre: false);
        Assert.Equal(2.0, resized.Width / resized.Height, 3);
        // The top left stayed where it was.
        Assert.Equal(10, resized.X);
        Assert.Equal(20, resized.Y);
    }

    [Fact]
    public void ATurnIsMeasuredAboutTheMiddle()
    {
        // From the right of the middle round to below it: a quarter turn clockwise.
        var turned = TransformEdits.Rotate(Box, TransformEdits.Position(Box, TransformHandle.Right),
            TransformEdits.Position(Box, TransformHandle.Bottom), steps: false);
        Assert.Equal(90, turned.Rotation, 3);
        // With Shift it lands on a whole fifteen degrees.
        var stepped = TransformEdits.Rotate(Box, new SKPoint(0, 45), new SKPoint(60, 0), steps: true);
        Assert.Equal(0, stepped.Rotation % 15, 3);
    }

    [Fact]
    public void MovingASnappedBoxLandsItsEdgeOnTheCanvasOrALayer()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 100);
        var layer = Patch(10, 20, 100, 50, "One");
        document.Layers.Add(layer);

        // Dragged to within a pixel of the canvas's right edge.
        var draft = layer.Transform with { X = 299 };
        var snapped = TransformEdits.Snap(document, draft, [layer.ID], tolerance: 2, out var lineX, out _);
        Assert.Equal(300, snapped.X);
        Assert.Equal(400, lineX);

        // Its middle within a pixel of the canvas's middle.
        var middle = layer.Transform with { X = 149.5 };
        var snappedMiddle = TransformEdits.Snap(document, middle, [layer.ID], tolerance: 2, out var middleLine, out _);
        Assert.Equal(150, snappedMiddle.X);
        Assert.Equal(200, middleLine);
    }

    [Fact]
    public void SnappingLeavesABoxAloneWhenNothingIsNear()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 1000, 1000);
        var layer = Patch(10, 20, 100, 50, "One");
        document.Layers.Add(layer);

        // A hundred wide on a thousand wide canvas: keeping every edge and middle a long way from the
        // canvas's own edges and middle takes care.
        var draft = layer.Transform with { X = 300.3, Y = 600.3 };
        var snapped = TransformEdits.Snap(document, draft, [layer.ID], tolerance: 2, out var lineX, out var lineY);
        Assert.Equal(draft, snapped);
        Assert.Null(lineX);
        Assert.Null(lineY);
    }

    [Fact]
    public void ALayerSnapsToItsNeighbourButNotToItself()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 1000, 1000);
        var one = Patch(10, 20, 100, 50, "One");
        var two = Patch(300, 400, 100, 50, "Two");
        document.Layers.Add(one);
        document.Layers.Add(two);

        // Where the other layer's left edge is, within a pixel: it snaps to it.
        var draft = two.Transform with { X = 10.5 };
        var snapped = TransformEdits.Snap(document, draft, [two.ID], tolerance: 2, out var lineX, out _);
        Assert.Equal(10, snapped.X);
        Assert.Equal(10, lineX);

        // Moving both, there is nothing of the two to snap to but the canvas.
        var alone = TransformEdits.Snap(document, draft, [one.ID, two.ID], tolerance: 2, out lineX, out _);
        Assert.Equal(10.5, alone.X);
        Assert.Null(lineX);
    }

    [Fact]
    public void AGuideIsSomethingToSnapTo()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 1000, 1000);
        document.Guides.Add(new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 642 });
        var layer = Patch(10, 20, 100, 50, "One");
        document.Layers.Add(layer);

        var draft = layer.Transform with { X = 641 };
        var snapped = TransformEdits.Snap(document, draft, [layer.ID], tolerance: 2, out var lineX, out _);
        Assert.Equal(642, snapped.X);
        Assert.Equal(642, lineX);
    }

    [Fact]
    public void TheSnapDistanceIsAFixedNumberOfScreenPixels()
    {
        // The pull is 10 screen pixels, so the tolerance in document pixels is that over the zoom.
        Assert.Equal(TransformSnap.Distance, 10);
        var (x, y, lineX, lineY) = TransformSnap.Offset(new SKRect(0, 0, 10, 10), [12], [30], tolerance: 3);
        Assert.Equal(2, x);
        Assert.Equal(0, y);
        Assert.Equal(12, lineX);
        Assert.Null(lineY);
    }

    [Fact]
    public void ASnapTakesTheNearestTargetWhenSeveralAreInReach()
    {
        var (x, _, lineX, _) = TransformSnap.Offset(new SKRect(0, 0, 10, 10), [9, 10.5], [], tolerance: 3);
        // The right edge at 10 is half a pixel from 10.5 and a whole one from 9, so it takes 10.5.
        Assert.Equal(0.5, x, 3);
        Assert.Equal(10.5, lineX);
    }

    private static ImageLayer Patch(double x, double y, int width, int height, string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new LayerTransform(x, y, width, height), name);
    }
}
