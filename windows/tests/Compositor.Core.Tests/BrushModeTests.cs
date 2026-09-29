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

    /// <summary>A field of one colour with a small blemish painted on it.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Blemished(SKColor field, SKColor blemish)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(60, 40));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = field };
            canvas.DrawRect(SKRect.Create(0, 0, 60, 40), paint);
            paint.Color = blemish;
            canvas.DrawRect(SKRect.Create(28, 18, 4, 4), paint);
        }
        var document = new CanvasDocument(Guid.NewGuid(), 60, 40);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Field"),
            new Model.LayerTransform(0, 0, 60, 40), "Field");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void SpotHealingRebuildsTheAreaFromItsSurroundings()
    {
        var (document, layer) = Blemished(new SKColor(40, 90, 180), new SKColor(240, 80, 40));
        using var _ = document;
        Assert.Equal(new SKColor(240, 80, 40, 255), layer.Asset!.Image.GetPixel(29, 19));

        // A dab over the blemish, with a fixed seed so the result can be asserted.
        var settings = new BrushSettings(Diameter: 14, Hardness: 1, Opacity: 1, Mode: BrushMode.Heal, Seed: 7);
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(29.5f, 19.5f)], settings));

        // The blemish has gone: the area now holds the colour around it, and so does its middle.
        var healed = layer.Asset.Image.GetPixel(29, 19);
        Assert.True(Math.Abs(healed.Red - 40) <= 6 && Math.Abs(healed.Green - 90) <= 6 && Math.Abs(healed.Blue - 180) <= 6,
            $"the healed pixel is {healed}");
        Assert.Equal(new SKColor(40, 90, 180, 255), layer.Asset.Image.GetPixel(29, 14));
        // The field the dab did not reach is untouched.
        Assert.Equal(new SKColor(40, 90, 180, 255), layer.Asset.Image.GetPixel(0, 0));
        Assert.Equal(new SKColor(40, 90, 180, 255), layer.Asset.Image.GetPixel(59, 39));
    }

    [Fact]
    public void SpotHealingLeavesTheRestOfTheLayerAlone()
    {
        var (document, layer) = Blemished(new SKColor(40, 90, 180), new SKColor(240, 80, 40));
        using var _ = document;
        var settings = new BrushSettings(Diameter: 10, Mode: BrushMode.Heal, Seed: 3);
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(29.5f, 19.5f)], settings));

        // A ring well outside the dab's reach is exactly as it was.
        for (var x = 0; x < 60; x += 7)
        {
            Assert.Equal(new SKColor(40, 90, 180, 255), layer.Asset!.Image.GetPixel(x, 2));
            Assert.Equal(new SKColor(40, 90, 180, 255), layer.Asset.Image.GetPixel(x, 37));
        }
    }

    [Fact]
    public void SpotHealingRespectsTheSelection()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(60, 40));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = new SKColor(40, 90, 180) };
            canvas.DrawRect(SKRect.Create(0, 0, 60, 40), paint);
            paint.Color = new SKColor(240, 80, 40);
            canvas.DrawRect(SKRect.Create(28, 18, 4, 4), paint);
        }
        using var document = new CanvasDocument(Guid.NewGuid(), 60, 40);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Field"),
            new Model.LayerTransform(0, 0, 60, 40), "Field");
        document.Layers.Add(layer);
        // A selection that leaves the right half of the blemish out.
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 29, 40));

        var settings = new BrushSettings(Diameter: 14, Mode: BrushMode.Heal, Seed: 5);
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(29.5f, 19.5f)], settings));

        // The half inside the selection is rebuilt — no longer the blemish it was — and the half outside it
        // still holds the blemish exactly. What the rebuilt half comes out as is up to the kernel, which
        // reads the surroundings, and those still hold the other half of the blemish.
        Assert.NotEqual(new SKColor(240, 80, 40, 255), layer.Asset!.Image.GetPixel(28, 19));
        Assert.Equal(new SKColor(240, 80, 40, 255), layer.Asset.Image.GetPixel(30, 19));
    }
}
