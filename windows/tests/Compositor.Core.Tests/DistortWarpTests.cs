using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using LayerMask = Compositor.Core.Model.LayerMask;

namespace Compositor.Core.Tests;

/// <summary>
/// Free distortion: four corners moved one at a time, and the pixels resampled into the shape they make. The
/// arithmetic is checked here — corners, which shapes a perspective can take, and where the picture lands.
/// </summary>
public class DistortWarpTests
{
    /// <summary>A layer of two colours side by side, and the document holding it.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Split(int side, LayerTransform? transform = null)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                bitmap.SetPixel(x, y, x < side / 2 ? new SKColor(220, 40, 40) : new SKColor(40, 60, 220));
        }
        var document = new CanvasDocument(Guid.NewGuid(), side * 2, side * 2);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Split"),
            transform ?? new LayerTransform(side / 2.0, side / 2.0, side, side), "Split");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static bool IsRed(SKColor colour) => colour.Red > colour.Green + 40 && colour.Red > colour.Blue + 40;

    private static SKColor AtDocument(ImageLayer layer, double x, double y)
    {
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, layer.Asset!.Width, layer.Asset.Height);
        Assert.True(toDocument.TryInvert(out var toPixel));
        var at = toPixel.MapPoint((float)x, (float)y);
        var px = (int)Math.Floor(at.X);
        var py = (int)Math.Floor(at.Y);
        if (px < 0 || py < 0 || px >= layer.Asset.Width || py >= layer.Asset.Height) return SKColors.Transparent;
        return layer.Asset.Image.GetPixel(px, py);
    }

    [Fact]
    public void TheCornersOfATransformComeBackInHandleOrder()
    {
        var corners = DistortWarp.Corners(new LayerTransform(10, 20, 30, 40));
        Assert.Equal(new SKPoint(10, 20), corners[0]);
        Assert.Equal(new SKPoint(40, 20), corners[1]);
        Assert.Equal(new SKPoint(40, 60), corners[2]);
        Assert.Equal(new SKPoint(10, 60), corners[3]);
        // A turned layer's corners turn with it, the way the transform says it turns: clockwise.
        var turned = DistortWarp.Corners(new LayerTransform(0, 0, 10, 10, 90));
        // To within the rounding of a cosine at a right angle.
        Assert.Equal(10, turned[0].X, 3);
        Assert.Equal(0, turned[0].Y, 3);
        Assert.Equal(10, turned[1].X, 3);
        Assert.Equal(10, turned[1].Y, 3);
        Assert.Equal(0, turned[2].X, 3);
        Assert.Equal(10, turned[2].Y, 3);
        Assert.Equal(0, turned[3].X, 3);
        Assert.Equal(0, turned[3].Y, 3);
    }

    [Fact]
    public void AShapeNeedsAreaAndFourCorners()
    {
        var square = new[] { new SKPoint(0, 0), new SKPoint(10, 0), new SKPoint(10, 10), new SKPoint(0, 10) };
        Assert.True(DistortWarp.IsUsable(square));
        Assert.True(DistortWarp.IsConvex(square));
        // Three corners is not a shape here.
        Assert.False(DistortWarp.IsUsable([square[0], square[1], square[2]]));
        // A collapsed or folded-over shape has a triangle of nothing in it, so there is nothing to draw.
        Assert.False(DistortWarp.IsUsable([square[0], square[1], square[2], square[2]]));
        Assert.False(DistortWarp.IsUsable([square[0], square[1], new SKPoint(5, 0), square[3]]));
        // A corner pulled past its neighbours folds it, which is drawn rather than refused — but it is not a
        // shape a perspective can take.
        var folded = new[] { square[0], square[1], new SKPoint(2, 8), square[3] };
        Assert.True(DistortWarp.IsUsable(folded));
        Assert.False(DistortWarp.IsConvex(folded));
        // A mirrored shape is wound the other way and is still convex.
        Assert.True(DistortWarp.IsConvex([square[3], square[2], square[1], square[0]]));
        // Somewhere impossible is not usable.
        Assert.False(DistortWarp.IsUsable([square[0], square[1], square[2], new SKPoint(float.NaN, 0)]));
    }

    [Fact]
    public void AShapeThatIsNotMovedLeavesThePictureWhereItWas()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        var before = AtDocument(layer, 12, 12);
        Assert.True(IsRed(before));

        Assert.True(DistortEdits.Distort(document, layer.ID, DistortWarp.Corners(layer.Transform)));
        // The same shape again: the pixels are the same size and still show the same thing where they were.
        Assert.True(IsRed(AtDocument(layer, 12, 12)));
        Assert.False(IsRed(AtDocument(layer, 24, 12)));
        Assert.Equal(new SKColor(220, 40, 40), WithoutAlpha(AtDocument(layer, 12, 12)));
    }

    [Fact]
    public void ACornerPulledOutwardsTakesThePictureWithIt()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        // The top left corner is pulled across and down, so the shape leans. Not along the diagonal, which
        // would leave three corners in a line and no shape at all.
        var corners = DistortWarp.Corners(layer.Transform);
        corners[0] = new SKPoint(corners[0].X + 6, corners[0].Y + 8);
        Assert.True(DistortWarp.IsConvex(corners));
        Assert.True(DistortEdits.Distort(document, layer.ID, corners));

        // The layer hugs the shape: a rectangle over its bounds, and no bigger.
        var bounds = DistortWarp.Bounds(corners);
        Assert.Equal(0, layer.Transform.Rotation);
        Assert.Equal(bounds.Width, layer.Transform.Width, 3);
        Assert.Equal(bounds.Height, layer.Transform.Height, 3);
        // Both halves of the picture came across, and it stops where the shape does.
        var tones = Tones(layer);
        Assert.Contains(true, tones.Select(IsRed));
        Assert.Contains(true, tones.Select(colour => colour.Blue > colour.Red + 40));
        Assert.Equal(0, AtDocument(layer, 38, 38).Alpha);
    }

    [Fact]
    public void AStretchedShapeMakesThePictureDeeper()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        var corners = DistortWarp.Corners(layer.Transform);
        // The right-hand side is pulled further right and deeper, so the shape is a trapezoid.
        corners[1] = new SKPoint(corners[1].X + 20, corners[1].Y);
        corners[2] = new SKPoint(corners[2].X + 20, corners[2].Y + 20);
        Assert.True(DistortEdits.Distort(document, layer.ID, corners));
        Assert.Equal(DistortWarp.Bounds(corners).Height, layer.Transform.Height, 3);
        Assert.True(layer.Transform.Height > 20, $"the shape did not deepen: {layer.Transform.Height}");
        // The picture is still in it, both halves of it.
        var tones = Tones(layer);
        Assert.Contains(true, tones.Select(IsRed));
        Assert.Contains(true, tones.Select(colour => colour.Blue > colour.Red + 40));
    }

    [Fact]
    public void AFoldedShapeIsDrawnInTwoHalves()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        // One corner pushed in past the diagonal folds the shape over, which a perspective cannot take.
        var corners = DistortWarp.Corners(layer.Transform);
        corners[2] = new SKPoint(corners[2].X - 30, corners[2].Y - 30);
        Assert.False(DistortWarp.IsConvex(corners));
        Assert.True(DistortEdits.Distort(document, layer.ID, corners));
        // The picture is still there: the halves were drawn, and something is opaque where the shape is.
        var drawn = 0;
        for (var y = 0; y < layer.Asset!.Height; y++)
            for (var x = 0; x < layer.Asset.Width; x++)
            {
                if (layer.Asset.Image.GetPixel(x, y).Alpha > 0) drawn++;
            }
        Assert.True(drawn > 100, $"the folded shape drew almost nothing: {drawn} pixels");
    }

    [Fact]
    public void AShapeTooBigToMakeIsRefused()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        var corners = DistortWarp.Corners(layer.Transform);
        // Corners a million pixels apart ask for a surface that cannot be had.
        corners[2] = new SKPoint(500_000, 500_000);
        Assert.False(DistortEdits.Distort(document, layer.ID, corners));
        // Left exactly as it was.
        Assert.True(IsRed(AtDocument(layer, 12, 12)));
        Assert.Null(DistortWarp.Warp(layer.Asset!.Image, layer.Transform, corners));
    }

    [Fact]
    public void AMaskOnTheLayersOwnGridIsResampledWithThePixels()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        // A mask painting the right half black, so half the layer is hidden.
        var mask = new SKBitmap(Bitmaps.MaskInfo(20, 20));
        mask.Erase(SKColors.White);
        for (var y = 0; y < 20; y++)
            for (var x = 10; x < 20; x++)
                mask.SetPixel(x, y, SKColors.Black);
        layer.Mask = LayerMask.AssetFrom(mask);

        var corners = DistortWarp.Corners(layer.Transform);
        corners[0] = new SKPoint(corners[0].X + 6, corners[0].Y + 8);
        Assert.True(DistortEdits.Distort(document, layer.ID, corners));

        Assert.NotNull(layer.Mask);
        Assert.Null(layer.Mask.Placement);
        // The mask covers the same grid as the pixels it goes with.
        Assert.Equal(layer.Asset!.Width, layer.Mask.Asset.Width);
        Assert.Equal(layer.Asset.Height, layer.Mask.Asset.Height);
        // And it still hides the same half: wherever the layer is red its mask is light, and wherever it is
        // blue its mask is dark.
        var matched = 0;
        for (var y = 0; y < layer.Asset.Height; y++)
        {
            for (var x = 0; x < layer.Asset.Width; x++)
            {
                var colour = layer.Asset.Image.GetPixel(x, y);
                if (colour.Alpha == 0) continue;
                var covered = layer.Mask.Asset.Image.GetPixel(x, y).Red;
                // A resample softens the mask's edge, so the halves are told apart at the middle rather than
                // at their ends: what matters is that the light half is over the red one and the dark half
                // over the blue one.
                if (IsRed(colour))
                {
                    Assert.True(covered > 128, $"the mask hides the layer's red half at {x},{y}: {covered}");
                    matched++;
                }
                else if (colour.Blue > colour.Red + 40)
                {
                    Assert.True(covered < 128, $"the mask shows the layer's blue half at {x},{y}: {covered}");
                    matched++;
                }
            }
        }
        Assert.True(matched > 100, $"too few pixels were checked: {matched}");
    }

    [Fact]
    public void ALayerThatIsNotThereOrHasNoPixelsIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        var square = new[] { new SKPoint(0, 0), new SKPoint(20, 0), new SKPoint(20, 20), new SKPoint(0, 20) };
        Assert.False(DistortEdits.Distort(document, blank.ID, square));
        Assert.False(DistortEdits.Distort(document, Guid.NewGuid(), square));
    }

    /// <summary>Two solid squares side by side, each one colour, and the document holding them.</summary>
    private static (CanvasDocument Document, ImageLayer Left, ImageLayer Right) SideBySide()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 80, 40);
        var left = new ImageLayer(Guid.NewGuid(), Solid(new SKColor(220, 40, 40)), new LayerTransform(0, 0, 20, 20), "Left");
        var right = new ImageLayer(Guid.NewGuid(), Solid(new SKColor(40, 60, 220)), new LayerTransform(30, 0, 20, 20), "Right");
        document.Layers.Add(left);
        document.Layers.Add(right);
        return (document, left, right);
    }

    private static ImportedImage Solid(SKColor colour)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
        bitmap.Erase(colour);
        return ImportedImage.Create(bitmap, "Solid");
    }

    [Fact]
    public void ABoxCarriedByItsOwnShapeStaysWhereItWas()
    {
        var box = new LayerTransform(4, 6, 20, 10);
        var corners = DistortWarp.Corners(box);
        // The shape a transform already has takes it nowhere, so carrying it by that shape leaves it alone.
        var carried = DistortWarp.Carried(box, box, corners);
        Assert.NotNull(carried);
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(corners[index].X, carried[index].X, 3);
            Assert.Equal(corners[index].Y, carried[index].Y, 3);
        }
    }

    [Fact]
    public void ALayerBesideTheOneDraggedIsCarriedByTheSamePerspective()
    {
        var (document, left, right) = SideBySide();
        using var _ = document;
        var box = TransformEdits.GroupBox(document, [left.ID, right.ID]);
        Assert.NotNull(box);
        // A move of the whole box: every corner of it goes the same way, which carries each layer by that much
        // and leaves the distance between them exactly as it was.
        var corners = DistortWarp.Corners(box.Value);
        for (var index = 0; index < 4; index++)
        {
            corners[index] = new SKPoint(corners[index].X + 10, corners[index].Y + 5);
        }
        var carried = DistortWarp.Carried(left.Transform, box.Value, corners);
        Assert.NotNull(carried);
        Assert.Equal(10, carried[0].X, 3);
        Assert.Equal(5, carried[0].Y, 3);
        Assert.Equal(30, carried[1].X, 3);

        Assert.True(DistortEdits.Distort(document, [left.ID, right.ID], box.Value, corners));
        Assert.Equal(10, left.Transform.X, 3);
        Assert.Equal(5, left.Transform.Y, 3);
        Assert.Equal(20, left.Transform.Width, 3);
        Assert.Equal(40, right.Transform.X, 3);
        Assert.Equal(5, right.Transform.Y, 3);
        // The gap between them is what it was, and each still holds its own picture.
        var gap = right.Transform.X - (left.Transform.X + left.Transform.Width);
        Assert.Equal(10, gap, 3);
        Assert.True(IsRed(AtDocument(left, 20, 15)));
        Assert.True(AtDocument(right, 50, 15).Blue > AtDocument(right, 50, 15).Red + 40);
    }

    [Fact]
    public void AStretchedGroupStretchesEveryLayerInIt()
    {
        var (document, left, right) = SideBySide();
        using var _ = document;
        var box = TransformEdits.GroupBox(document, [left.ID, right.ID]);
        Assert.NotNull(box);
        // The box's right-hand side is pulled 20 further right, so the box is 70 wide where it was 50: every
        // layer in it is stretched by the same amount, and keeps its place along the box.
        var corners = DistortWarp.Corners(box.Value);
        corners[1] = new SKPoint(corners[1].X + 20, corners[1].Y);
        corners[2] = new SKPoint(corners[2].X + 20, corners[2].Y);
        Assert.True(DistortEdits.Distort(document, [left.ID, right.ID], box.Value, corners));

        Assert.Equal(0, left.Transform.X, 3);
        Assert.Equal(28, left.Transform.Width, 3);
        Assert.Equal(42, right.Transform.X, 3);
        Assert.Equal(28, right.Transform.Width, 3);
        Assert.Equal(20, left.Transform.Height, 3);
        // Both pictures are in their new shape, each where its own layer was carried to.
        Assert.True(IsRed(AtDocument(left, 14, 10)));
        Assert.True(AtDocument(right, 56, 10).Blue > AtDocument(right, 56, 10).Red + 40);
    }

    [Fact]
    public void ADistortionOfSeveralLayersIsRefusedWhenNoneOfThemCanBeMade()
    {
        var (document, left, right) = SideBySide();
        using var _ = document;
        var box = TransformEdits.GroupBox(document, [left.ID, right.ID]);
        Assert.NotNull(box);
        var corners = DistortWarp.Corners(box.Value);
        // Nothing selected at all: there is no layer to carry.
        Assert.False(DistortEdits.Distort(document, [Guid.NewGuid()], box.Value, corners));
        // A shape with no area between its corners is not one a perspective can take, for the box or for a layer.
        var flat = new[] { corners[0], corners[1], corners[1], corners[3] };
        Assert.Null(DistortWarp.Carried(left.Transform, box.Value, flat));
        Assert.False(DistortEdits.Distort(document, [left.ID, right.ID], box.Value, flat));
        Assert.Equal(0, left.Transform.X);
        Assert.Equal(30, right.Transform.X);
    }

    [Fact]
    public void ADistortedLayerStaysWhatItWasInEveryOtherWay()
    {
        var (document, layer) = Split(20);
        using var _ = document;
        layer.Opacity = 0.5;
        layer.BlendMode = LayerBlendMode.Multiply;
        var corners = DistortWarp.Corners(layer.Transform);
        corners[3] = new SKPoint(corners[3].X - 5, corners[3].Y + 5);
        Assert.True(DistortEdits.Distort(document, layer.ID, corners));
        Assert.Equal("Split", layer.Name);
        Assert.Equal(0.5, layer.Opacity);
        Assert.Equal(LayerBlendMode.Multiply, layer.BlendMode);
        Assert.Equal(SKAlphaType.Unpremul, layer.Asset!.Image.AlphaType);
    }

    private static SKColor WithoutAlpha(SKColor colour) => new(colour.Red, colour.Green, colour.Blue);

    /// <summary>The colours an opaque part of a layer holds, sampled across it.</summary>
    private static List<SKColor> Tones(ImageLayer layer)
    {
        var tones = new List<SKColor>();
        for (var y = 0; y < layer.Asset!.Height; y += 2)
        {
            for (var x = 0; x < layer.Asset.Width; x += 2)
            {
                var colour = layer.Asset.Image.GetPixel(x, y);
                if (colour.Alpha > 0) tones.Add(WithoutAlpha(colour));
            }
        }
        return tones;
    }
}
