using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The Camera Raw filter's Geometry group: the picture turned, keystoned and zoomed inside its own pixels, with
/// the empty wedges trimmed away when Constrain Crop asks for it. The corners are the whole of it, so they are
/// what these check first.
/// </summary>
public class GeometryEditsTests
{
    /// <summary>A layer marked with a colour in each corner, so where each corner went can be read off.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Marked(int side = 60)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(new SKColor(90, 90, 90));
        foreach (var (x, y, colour) in new (int, int, SKColor)[]
                 {
                     (0, 0, new SKColor(220, 40, 40)),
                     (side - 1, 0, new SKColor(40, 200, 40)),
                     (side - 1, side - 1, new SKColor(40, 60, 220)),
                     (0, side - 1, new SKColor(220, 200, 40)),
                 })
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var px = Math.Clamp(x + dx, 0, side - 1);
                    var py = Math.Clamp(y + dy, 0, side - 1);
                    bitmap.SetPixel(px, py, colour);
                }
            }
        }
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Marked"),
            new LayerTransform(0, 0, side, side), "Marked");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor At(ImageLayer layer, double x, double y)
    {
        var toPixel = BrushEdits.PixelToDocument(layer.Transform, layer.Asset!.Width, layer.Asset.Height);
        Assert.True(toPixel.TryInvert(out var fromDocument));
        var at = fromDocument.MapPoint((float)x, (float)y);
        var px = (int)Math.Floor(at.X);
        var py = (int)Math.Floor(at.Y);
        if (px < 0 || py < 0 || px >= layer.Asset.Width || py >= layer.Asset.Height) return SKColors.Transparent;
        return layer.Asset.Image.GetPixel(px, py);
    }

    private static bool IsNear(SKColor colour, SKColor wanted, int slack = 60) =>
        Math.Abs(colour.Red - wanted.Red) <= slack && Math.Abs(colour.Green - wanted.Green) <= slack
        && Math.Abs(colour.Blue - wanted.Blue) <= slack;

    [Fact]
    public void GeometryThatAsksForNothingIsRefusedAndLeavesTheLayerAlone()
    {
        var (document, layer) = Marked();
        using var _ = document;
        var original = layer.Asset;
        Assert.False(GeometryEdits.Apply(document, layer.ID, new CameraRawGeometrySettings()));
        Assert.Same(original, layer.Asset);
    }

    [Fact]
    public void TheFourCornersLandWhereTheSettingsSayTheyDo()
    {
        // The corners are the whole of the geometry, so they are checked against the sum the Mac build does:
        // no turn, no aspect, no zoom, one vertical keystone and one offset each way.
        var settings = new CameraRawGeometrySettings { Vertical = 50, OffsetX = 20, OffsetY = 10 };
        var corners = settings.Corners(200, 100);
        // 50 of the 100 range is a keystone of 200 * 0.18 * 0.5 = 18 outwards at the top, 0.15 * 200 * 0.2 = 6
        // to the right, and 0.15 * 100 * 0.1 = 1.5 out at the top and the bottom, as the Mac build's offset does.
        Assert.Equal(4, corners.Length);
        Assert.Equal(-18 + 6, corners[0].X, 3);
        Assert.Equal(-1.5f, corners[0].Y, 3);
        Assert.Equal(200 + 18 + 6, corners[1].X, 3);
        Assert.Equal(-1.5f, corners[1].Y, 3);
        Assert.Equal(200 + 6, corners[2].X, 3);
        Assert.Equal(100 + 1.5f, corners[2].Y, 3);
        Assert.Equal(6, corners[3].X, 3);
        Assert.Equal(100 + 1.5f, corners[3].Y, 3);
    }

    [Fact]
    public void ATurnTakesThePictureRoundWithIt()
    {
        var (document, layer) = Marked();
        using var _ = document;
        var settings = new CameraRawGeometrySettings { Rotate = 20 };
        Assert.True(GeometryEdits.Apply(document, layer.ID, settings));
        Assert.Equal(60, layer.Asset!.Width);
        Assert.Equal(60, layer.Asset.Height);
        // The middle of the picture stays where it was: a turn goes round it.
        Assert.True(IsNear(At(layer, 30, 30), new SKColor(90, 90, 90), 40),
            $"the middle moved: {At(layer, 30, 30)}");
        // A turned square no longer fills its box: the corners it was cut away from are empty, while some row
        // still reaches right across, which is the turned picture's own width.
        Assert.Equal(0, At(layer, 2, 2).Alpha);
        Assert.Equal(0, At(layer, 57, 2).Alpha);
        var widest = 0;
        for (var row = 0; row < 60; row++) widest = Math.Max(widest, Opaque(layer, row));
        Assert.Equal(60, widest);
        Assert.True(Opaque(layer, 0) < widest, "the top row should be cut back by the turn");
    }

    [Fact]
    public void ConstrainCropTakesTheEmptyWedgesAway()
    {
        var (document, layer) = Marked();
        using var _ = document;
        // Zoomed out, the picture no longer reaches the edges of its box, which leaves a margin all round.
        Assert.True(GeometryEdits.Apply(document, layer.ID, new CameraRawGeometrySettings { Scale = -30 }));
        Assert.True(Clear(layer) > 400, $"a shrunken picture should leave a margin: {Clear(layer)} pixels clear");

        var (cropped, tight) = Marked();
        using var _tight = cropped;
        Assert.True(GeometryEdits.Apply(cropped, tight.ID,
            new CameraRawGeometrySettings { Scale = -30, ConstrainCrop = true }));
        // The margin is trimmed and what is left fitted back into the same box, so the picture reaches its
        // edges again — give or take the resample's own soft border.
        Assert.Equal(60, tight.Asset!.Width);
        Assert.Equal(60, tight.Asset.Height);
        Assert.True(Clear(tight) < 60, $"the crop did not reach the margin: {Clear(tight)} pixels left clear");
        // …and the marks that were cut back to the middle are out at the corners again.
        Assert.True(Where(tight, new SKColor(220, 40, 40)).X < 12,
            $"the top left mark did not come back out: {Where(tight, new SKColor(220, 40, 40))}");
    }

    /// <summary>How much of a layer is empty, which is what a margin or a cut-away corner is.</summary>
    private static int Clear(ImageLayer layer)
    {
        var clear = 0;
        for (var y = 0; y < layer.Asset!.Height; y++)
        {
            for (var x = 0; x < layer.Asset.Width; x++)
            {
                if (layer.Asset.Image.GetPixel(x, y).Alpha == 0) clear++;
            }
        }
        return clear;
    }

    [Fact]
    public void AKeystoneWidensOneEndAndNarrowsTheOther()
    {
        var (document, layer) = Marked();
        using var _ = document;
        Assert.True(GeometryEdits.Apply(document, layer.ID, new CameraRawGeometrySettings { Vertical = 60 }));
        // The top of the picture is pulled 6.5 pixels further out at each side, so it is drawn wider than the
        // box: the top row reaches right across, while the marks that were the very top corners are off the
        // edge and gone. The bottom of the picture was not touched at all.
        Assert.Equal(60, Opaque(layer, 0));
        Assert.Equal(60, Opaque(layer, 59));
        Assert.Equal(new SKPoint(-1, -1), Where(layer, new SKColor(220, 40, 40)));
        Assert.Equal(new SKPoint(-1, -1), Where(layer, new SKColor(40, 200, 40)));
        var blue = Where(layer, new SKColor(40, 60, 220));
        Assert.True(blue.X > 52 && blue.Y > 52, $"the bottom right mark moved: {blue}");
    }

    /// <summary>How much of a row is painted, which is where a turn or a keystone shows itself.</summary>
    private static int Opaque(ImageLayer layer, int row)
    {
        var painted = 0;
        for (var x = 0; x < layer.Asset!.Width; x++)
        {
            if (layer.Asset.Image.GetPixel(x, row).Alpha > 200) painted++;
        }
        return painted;
    }

    [Fact]
    public void GeometryRefusesALayerThatIsNotThere()
    {
        var (document, _) = Marked();
        using var _document = document;
        Assert.False(GeometryEdits.Apply(document, Guid.NewGuid(), new CameraRawGeometrySettings { Rotate = 10 }));
    }

    [Fact]
    public void TheCameraRawFilterMovesThePictureBeforeItGradesIt()
    {
        // The geometry group is part of the filter: asking for it alone still changes the layer, and asking for
        // it beside a grade does both.
        var (document, layer) = Marked();
        using var _ = document;
        Assert.True(CameraRawEdits.Apply(document, layer.ID,
            new CameraRawSettings { Geometry = new CameraRawGeometrySettings { Rotate = 15 } }));
        Assert.Equal(60, layer.Asset!.Width);
        // The corners the turn cut away are empty, which the filter does not put back.
        var clear = 0;
        for (var y = 0; y < layer.Asset.Height; y++)
        {
            for (var x = 0; x < layer.Asset.Width; x++)
            {
                if (layer.Asset.Image.GetPixel(x, y).Alpha < 200) clear++;
            }
        }
        Assert.True(clear > 100, $"the picture was not turned: {clear} pixels empty");
        // The filter's own settings say they ask for something, and a shape it cannot make refuses it.
        Assert.True(new CameraRawSettings { Geometry = new CameraRawGeometrySettings { Rotate = 15 } }.AdjustsGeometry);
        Assert.False(new CameraRawSettings { Geometry = new CameraRawGeometrySettings { Rotate = 200 } }.IsValid);
    }

    /// <summary>Where a colour is on the layer, as a whole-pixel place; −1,−1 when it is nowhere.</summary>
    private static SKPoint Where(ImageLayer layer, SKColor wanted)
    {
        var toPixel = BrushEdits.PixelToDocument(layer.Transform, layer.Asset!.Width, layer.Asset.Height);
        Assert.True(toPixel.TryInvert(out var fromDocument));
        for (var y = 0; y < layer.Asset.Height; y++)
        {
            for (var x = 0; x < layer.Asset.Width; x++)
            {
                if (!IsNear(layer.Asset.Image.GetPixel(x, y), wanted, 60)) continue;
                return fromDocument.MapPoint(x + 0.5f, y + 0.5f);
            }
        }
        return new SKPoint(-1, -1);
    }
}
