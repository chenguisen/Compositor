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
    public void APreviewNeedsALayerThatIsThere()
    {
        var (document, _) = Warm(20);
        using var _document = document;
        Assert.Null(FilterPreview.Begin(document, Guid.NewGuid()));
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
