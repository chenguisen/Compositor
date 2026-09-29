using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// What a drag lines up with, and the switches that take each kind away. The pull itself is covered by the
/// transform tests; these are the targets the View menu chooses between.
/// </summary>
public class SnapToTests
{
    /// <summary>A layer at (30, 20) and a guide, in a canvas with edges and middles of its own.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Scene()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(10, 10));
        bitmap.Erase(SKColors.White);
        var document = new CanvasDocument(Guid.NewGuid(), 100, 80);
        var other = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Other"),
            new LayerTransform(60, 40, 10, 10), "Other");
        var moving = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Moving"),
            new LayerTransform(30, 20, 10, 10), "Moving");
        document.Layers.Add(other);
        document.Layers.Add(moving);
        GuideEdits.Add(document, GuideAxis.Vertical, 12);
        GuideEdits.Add(document, GuideAxis.Horizontal, 7);
        return (document, moving);
    }

    [Fact]
    public void EverythingIsAskedForByDefault()
    {
        var (document, moving) = Scene();
        using var _ = document;
        var (xs, ys) = TransformEdits.SnapTargets(document, new[] { moving.ID });
        // The canvas: its edges and middle. The guide: where it sits. The other layer: its edges and middle.
        Assert.Contains(0, xs);
        Assert.Contains(50, xs);
        Assert.Contains(100, xs);
        Assert.Contains(12, xs);
        Assert.Contains(60, xs);
        Assert.Contains(65, xs);
        Assert.Contains(70, xs);
        Assert.Contains(7, ys);
        Assert.Contains(40, ys);
        Assert.Contains(45, ys);
        // And never the moving layer's own box.
        Assert.DoesNotContain(30, xs);
        Assert.DoesNotContain(20, ys);
    }

    [Fact]
    public void EachSwitchTakesAwayItsOwnKindOfTarget()
    {
        var (document, moving) = Scene();
        using var _ = document;
        var (canvas, _) = TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.Canvas);
        Assert.Contains(50, canvas);
        Assert.DoesNotContain(12, canvas);
        Assert.DoesNotContain(60, canvas);

        var (guides, guideY) = TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.Guides);
        Assert.Contains(12, guides);
        Assert.Equal([7], guideY);
        Assert.DoesNotContain(50, guides);

        var (layers, _) = TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.Layers);
        Assert.Contains(60, layers);
        Assert.DoesNotContain(50, layers);
        Assert.DoesNotContain(12, layers);
    }

    [Fact]
    public void SwitchingAllThreeOffLeavesNothingToSnapTo()
    {
        var (document, moving) = Scene();
        using var _ = document;
        var (xs, ys) = TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.None);
        Assert.Empty(xs);
        Assert.Empty(ys);
        // So a drag that would have landed on a target lands where it was let go.
        var draft = new LayerTransform(12, 7, 10, 10);
        var placed = TransformEdits.Snap(document, draft, new[] { moving.ID }, 4, out var lineX, out var lineY, SnapTo.None);
        Assert.Equal(draft, placed);
        Assert.Null(lineX);
        Assert.Null(lineY);
    }

    [Fact]
    public void TheGridIsATargetOnlyWhileItIsGivenAndAskedFor()
    {
        var (document, moving) = Scene();
        using var _ = document;
        var grid = new LayoutGrid(20, 4);
        // The grid's lines are every five pixels across and down.
        var (xs, ys) = TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.Grid, grid);
        Assert.Equal([0, 5, 10, 15, 20], xs.Take(5));
        Assert.Contains(95, xs);
        Assert.Contains(75, ys);
        // Asking for the grid without giving one leaves nothing to snap to.
        Assert.Empty(TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.Grid).Xs);
        // And a switch that is off does not add them.
        var (others, _) = TransformEdits.SnapTargets(document, new[] { moving.ID }, SnapTo.Canvas, grid);
        Assert.DoesNotContain(15, others);
    }

    [Fact]
    public void ADragLinesUpWithTheGridWhenItIsTheOnlyThingOn()
    {
        var (document, moving) = Scene();
        using var _ = document;
        var grid = new LayoutGrid(20, 4);
        // The layer's left edge is two pixels off a line of the grid at ten, and its top edge two off one at
        // twenty: both are near enough to take hold of.
        var draft = new LayerTransform(12, 22, 10, 10);
        var on = TransformEdits.Snap(document, draft, new[] { moving.ID }, 4, out var line, out var lineY,
            SnapTo.Grid, grid);
        Assert.Equal(10, on.X, 6);
        Assert.Equal(20, on.Y, 6);
        Assert.Equal(0, line % 5);
        Assert.Equal(0, lineY % 5);
        // Without the grid there is nothing there to line up with.
        var off = TransformEdits.Snap(document, draft, new[] { moving.ID }, 4, out var none, out var nothing, SnapTo.None);
        Assert.Equal(draft, off);
        Assert.Null(none);
        Assert.Null(nothing);
    }

    [Fact]
    public void AGuideIsSnappedToWhileItsSwitchIsOnAndNotOnceItIsOff()
    {
        var (document, moving) = Scene();
        using var _ = document;
        // The moving layer's left edge is two pixels off the guide at twelve.
        var draft = new LayerTransform(10, 20, 10, 10);
        // Only the guide across is near enough; vertically there is nothing to land on.
        var on = TransformEdits.Snap(document, draft, new[] { moving.ID }, 4, out var line, out var noLine, SnapTo.Guides);
        Assert.Null(noLine);
        Assert.Equal(12, on.X, 6);
        Assert.Equal(12, line);

        var off = TransformEdits.Snap(document, draft, new[] { moving.ID }, 4, out var none, out var noneDown, SnapTo.Canvas);
        Assert.Null(noneDown);
        Assert.Equal(10, off.X, 6);
        Assert.Null(none);
    }
}
