using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The brush's other modes: the Clone Stamp, which paints what the layer shows elsewhere, and the Blur
/// brush, which paints a softened copy of it.
/// </summary>
public class BrushModeTests
{
    /// <summary>A 40 by 20 layer, red on the left half and blue on the right, both opaque.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Halves()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(SKRect.Create(0, 0, 20, 20), paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(SKRect.Create(20, 0, 20, 20), paint);
        }
        var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Halves"),
            new Model.LayerTransform(0, 0, 40, 20), "Halves");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void TheCloneStampPaintsWhatTheLayerShowsAtTheSource()
    {
        var (document, layer) = Halves();
        using var _ = document;
        // The source sits twenty pixels to the left, so a stroke over the blue half copies the red half.
        var settings = new BrushSettings(Diameter: 6, Opacity: 1, Mode: BrushMode.Clone,
            CloneFrom: new SKPointI(-20, 0));
        var stroke = new[] { new SKPoint(30.5f, 10.5f) };
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, settings));

        // Under the middle of the dab the layer now shows what lay twenty pixels away: red.
        Assert.Equal(new SKColor(255, 0, 0, 255), layer.Asset!.Image.GetPixel(30, 10));
        // Away from the dab the layer is untouched.
        Assert.Equal(new SKColor(0, 0, 255, 255), layer.Asset.Image.GetPixel(38, 10));
    }

    [Fact]
    public void TheCloneStampLeavesTheLayerAloneWhereTheSampleIsEmpty()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(SKRect.Create(0, 0, 10, 20), paint);
        }
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Corner"),
            new Model.LayerTransform(0, 0, 40, 20), "Corner");
        document.Layers.Add(layer);

        // The sample is read forty pixels to the right of the brush, which is past the painted part: the
        // layer under the dab keeps what it had, as drawing an empty sample over it would leave it.
        var settings = new BrushSettings(Diameter: 6, Mode: BrushMode.Clone, CloneFrom: new SKPointI(30, 0));
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(5.5f, 10.5f)], settings));
        Assert.Equal(new SKColor(255, 0, 0, 255), layer.Asset!.Image.GetPixel(5, 10));
    }

    [Fact]
    public void TheCloneStampNeedsSomewhereToCopyFrom()
    {
        var (document, layer) = Halves();
        using var _ = document;
        Assert.False(BrushEdits.Paint(document, layer.ID, [new SKPoint(30.5f, 10.5f)],
            new BrushSettings(Diameter: 6, Mode: BrushMode.Clone)));
    }

    [Fact]
    public void TheBlurBrushSoftensWhatIsUnderIt()
    {
        var (document, layer) = Halves();
        using var _ = document;

        // A wide soft brush over the boundary between the halves blurs the edge across it.
        var settings = new BrushSettings(Diameter: 12, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur);
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(20.5f, 10.5f)], settings));

        // On the boundary the colour is now between the two, where it used to be one or the other.
        var middle = layer.Asset!.Image.GetPixel(19, 10);
        Assert.True(middle.Red is > 0 and < 255, $"red is {middle.Red}");
        Assert.True(middle.Blue is > 0 and < 255, $"blue is {middle.Blue}");
        // Far from the brush the halves are as they were.
        Assert.Equal(new SKColor(255, 0, 0, 255), layer.Asset.Image.GetPixel(2, 10));
        Assert.Equal(new SKColor(0, 0, 255, 255), layer.Asset.Image.GetPixel(37, 10));
    }

    [Fact]
    public void TheBlurBrushSoftensFurtherOnASecondStroke()
    {
        var (document, layer) = Halves();
        using var _ = document;
        var settings = new BrushSettings(Diameter: 12, Mode: BrushMode.Blur);
        var stroke = new[] { new SKPoint(20.5f, 10.5f) };

        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, settings));
        var once = layer.Asset!.Image.GetPixel(19, 10);
        Assert.True(BrushEdits.Paint(document, layer.ID, stroke, settings));
        var twice = layer.Asset.Image.GetPixel(19, 10);

        // The second stroke softens what the first left, so the boundary colour has moved further towards
        // the middle of the two.
        Assert.True(twice.Blue > once.Blue, $"blue went from {once.Blue} to {twice.Blue}");
    }

    [Fact]
    public void APaintStrokeIsUnchangedByTheNewModesBeingAvailable()
    {
        var (document, layer) = Halves();
        using var _ = document;
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(5.5f, 10.5f)],
            new BrushSettings(Diameter: 6, Red: 0, Green: 1, Blue: 0)));
        Assert.Equal(new SKColor(0, 255, 0, 255), layer.Asset!.Image.GetPixel(5, 10));
    }

    [Fact]
    public void CloneAndBlurRespectTheSelection()
    {
        var (document, layer) = Halves();
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 22, 20));

        // The blur brush is dragged across the boundary, but only the selected columns may take it.
        var settings = new BrushSettings(Diameter: 12, Mode: BrushMode.Blur);
        Assert.True(BrushEdits.Paint(document, layer.ID,
            [new SKPoint(20.5f, 10.5f), new SKPoint(30.5f, 10.5f)], settings));

        Assert.Equal(new SKColor(0, 0, 255, 255), layer.Asset!.Image.GetPixel(30, 10));
    }
}
