using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// A filter being looked at rather than applied: the canvas shows it, the document keeps what it had, and the
/// pixels it makes are the preview's own to free — which is the one thing that is easy to get wrong, so it is
/// what most of these check.
/// </summary>
public class FilterPreviewTests
{
    private static (CanvasDocument Document, ImageLayer Layer) Warm(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(new SKColor(200, 60, 40));
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Warm"),
            new LayerTransform(0, 0, side, side), "Warm");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static Func<CanvasDocument, Guid, bool> Invert => (document, id) =>
        FilterEdits.ApplyAdjustment(document, id, new Format.LayerAdjustment { Kind = Format.AdjustmentKind.Invert });

    [Fact]
    public void ThePreviewShowsTheFilterAndTheDocumentKeepsWhatItHad()
    {
        var (document, layer) = Warm(20);
        using var _ = document;
        var original = layer.Asset;
        using var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);

        Assert.True(preview.Show(Invert));
        // The preview has the filtered pixels…
        var shown = preview.Document.Layers.First(entry => entry.ID == layer.ID);
        Assert.NotSame(original, shown.Asset);
        // …which are the picture turned over…
        Assert.Equal(55, shown.Asset!.Image.GetPixel(10, 10).Red);
        // …and the document it came from is exactly as it was, pixels and all.
        Assert.Same(original, layer.Asset);
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset!.Image.GetPixel(10, 10));
    }

    [Fact]
    public void MovingTheAmountsAgainReplacesTheLastPreviewRatherThanCrashing()
    {
        var (document, layer) = Warm(20);
        using var _ = document;
        using var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        // A slider being dragged: each of these replaces the preview's pixels, and the ones it made before are
        // freed as it goes — where freeing the ones it was given would leave the layer holding nothing.
        for (var step = 0; step < 6; step++)
        {
            Assert.True(preview.Show(Invert));
            var shown = preview.Document.Layers.First(entry => entry.ID == layer.ID);
            Assert.Equal(55, shown.Asset!.Image.GetPixel(10, 10).Red);
        }
        // The document is still whole, and so is the layer's own picture.
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset!.Image.GetPixel(10, 10));
    }

    [Fact]
    public void PuttingThePreviewAwayLeavesTheDocumentDrawable()
    {
        var (document, layer) = Warm(20);
        using var _ = document;
        var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        Assert.True(preview.Show(Invert));
        preview.Dispose();
        // The pixels the preview was handed belong to the layer, so they are still there to draw.
        using var rendered = Rendering.DocumentRenderer.Render(document);
        Assert.Equal(new SKColor(200, 60, 40), rendered.GetPixel(10, 10));
        // Putting it away twice is nothing, and showing anything on it afterwards is refused.
        preview.Dispose();
        Assert.False(preview.Show(Invert));
    }

    [Fact]
    public void AFilterThatRefusesLeavesTheLastPreviewStanding()
    {
        var (document, layer) = Warm(20);
        using var _ = document;
        using var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        Assert.True(preview.Show(Invert));
        // A filter whose amounts cannot be used is refused, and the picture on the canvas does not change.
        Assert.False(preview.Show((doc, id) => FilterEdits.ApplyAdjustment(doc, id,
            new Format.LayerAdjustment { Kind = Format.AdjustmentKind.GaussianBlur, BlurRadius = 900 })));
        var shown = preview.Document.Layers.First(entry => entry.ID == layer.ID);
        Assert.Equal(55, shown.Asset!.Image.GetPixel(10, 10).Red);
    }

    [Fact]
    public void AnAdjustmentLayerCanBePreviewedWithoutItsPixelsBeingTouchd()
    {
        // An adjustment layer holds no pixels: what its panel changes is the adjustment, which the renderer
        // reads. A preview of that has to work, and must not make the preview think it owns any pixels.
        var (document, layer) = Warm(12);
        using var _ = document;
        var made = LayerPlacement.AddAdjustment(document, Format.AdjustmentKind.Invert, layer.ID)!.Value;
        using var preview = FilterPreview.Begin(document, made);
        Assert.NotNull(preview);
        for (var step = 0; step < 3; step++)
        {
            Assert.True(preview.Show((target, id) => LayerAdjustmentEdits.Set(target, id,
                new Format.LayerAdjustment { Kind = Format.AdjustmentKind.Invert })));
        }
        // The layer under it is still whole, and so is the adjustment layer's own record.
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset!.Image.GetPixel(6, 6));
        Assert.NotNull(document.Layers.First(entry => entry.ID == made).Adjustment);
        Assert.Null(document.Layers.First(entry => entry.ID == made).Asset);
    }

    [Fact]
    public void AGradientCanBeLookedAtBeforeItIsFilledIn()
    {
        // The gradient tool's drag: the fill is run on the preview as the line moves, so the run of it can be
        // seen before the mouse comes up.
        var (document, layer) = Warm(40);
        using var _ = document;
        using var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        var from = new SKColor(0, 0, 0);
        var to = new SKColor(255, 255, 255);
        Assert.True(preview.Show((target, id) => GradientEdits.Fill(target, id, false,
            new SKPoint(0, 20), new SKPoint(40, 20), from, to, 1, GradientShape.Linear)));

        // The preview runs from one colour to the other across the picture…
        var shown = preview.Document.Layers.First(entry => entry.ID == layer.ID);
        Assert.True(shown.Asset!.Image.GetPixel(0, 20).Red < 40, $"the far end is {shown.Asset.Image.GetPixel(0, 20)}");
        Assert.True(shown.Asset.Image.GetPixel(39, 20).Red > 210, $"the near end is {shown.Asset.Image.GetPixel(39, 20)}");
        // …and the layer itself is the flat colour it was, not a pixel of the gradient on it.
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset!.Image.GetPixel(0, 20));
        Assert.Equal(new SKColor(200, 60, 40), layer.Asset.Image.GetPixel(39, 20));
    }

    [Fact]
    public void APreviewNeedsALayerThatIsThere()
    {
        var (document, _) = Warm(20);
        using var _document = document;
        Assert.Null(FilterPreview.Begin(document, Guid.NewGuid()));
        // Nor does a list with nothing in it that is there make a preview.
        Assert.Null(FilterPreview.Begin(document, [Guid.NewGuid(), Guid.NewGuid()]));
    }

    [Fact]
    public void SeveralLayersCanBeLookedAtTogetherAndEachKeepsWhatItHad()
    {
        // A distortion over a group resamples every layer in it, so the preview has to carry them all and
        // forget the last look of each — not compound them, and not free the layers' own pixels.
        var (document, layer) = Warm(20);
        using var _ = document;
        var other = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(layer.Asset!.Image.Copy(), "Other"),
            new LayerTransform(30, 0, 20, 20), "Other");
        document.Layers.Add(other);
        var originals = new[] { layer.Asset, other.Asset };
        var box = TransformEdits.GroupBox(document, [layer.ID, other.ID]);
        Assert.NotNull(box);
        using var preview = FilterPreview.Begin(document, [layer.ID, other.ID]);
        Assert.NotNull(preview);
        var corners = DistortWarp.Corners(box.Value);
        for (var index = 0; index < 4; index++) corners[index] = new SKPoint(corners[index].X + 10, corners[index].Y);

        Assert.True(preview.Show(document => DistortEdits.Distort(document, [layer.ID, other.ID], box.Value, corners)));
        var shownLeft = preview.Document.Layers.First(entry => entry.ID == layer.ID);
        var shownRight = preview.Document.Layers.First(entry => entry.ID == other.ID);
        Assert.Equal(10, shownLeft.Transform.X, 3);
        Assert.Equal(40, shownRight.Transform.X, 3);
        // Looking again starts from the layers' own pixels, so it lands in the same place rather than 10 further.
        Assert.True(preview.Show(document => DistortEdits.Distort(document, [layer.ID, other.ID], box.Value, corners)));
        Assert.Equal(10, preview.Document.Layers.First(entry => entry.ID == layer.ID).Transform.X, 3);
        // A shape that cannot be made is refused, and the last look stands.
        var flat = new[] { corners[0], corners[1], corners[1], corners[3] };
        Assert.False(preview.Show(document => DistortEdits.Distort(document, [layer.ID, other.ID], box.Value, flat)));
        Assert.Equal(10, preview.Document.Layers.First(entry => entry.ID == layer.ID).Transform.X, 3);
        Assert.Equal(40, preview.Document.Layers.First(entry => entry.ID == other.ID).Transform.X, 3);
        // Neither layer's own pixels were taken over or freed.
        Assert.Same(originals[0], layer.Asset);
        Assert.Same(originals[1], other.Asset);
        preview.Dispose();
        using var rendered = Rendering.DocumentRenderer.Render(document);
        Assert.Equal(new SKColor(200, 60, 40), rendered.GetPixel(10, 10));
    }

    [Fact]
    public void ThePreviewSharesEverythingTheFilterDoesNotTouch()
    {
        var (document, layer) = Warm(20);
        using var _ = document;
        // A second layer, untouched by the filter, is the same pixels in both.
        var other = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(layer.Asset!.Image.Copy(), "Other"),
            new LayerTransform(0, 0, 20, 20), "Other");
        document.Layers.Add(other);
        using var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        Assert.True(preview.Show(Invert));
        Assert.Same(other.Asset, preview.Document.Layers.First(entry => entry.ID == other.ID).Asset);
        Assert.Same(other.Asset!.Image, document.Layers.First(entry => entry.ID == other.ID).Asset!.Image);
    }
}
