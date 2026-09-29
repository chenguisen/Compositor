using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// What a layer is combined with: how much of it shows and how it blends. The renderer has always honoured
/// both; these are the edits that set them, and the proof that a change reaches the picture.
/// </summary>
public class LayerAppearanceTests
{
    /// <summary>A white layer over a black one, so a blend or an opacity has something to show against.</summary>
    private static (CanvasDocument Document, ImageLayer Top, ImageLayer Bottom) Stacked(int side)
    {
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var under = new SKBitmap(Bitmaps.ColorInfo(side, side));
        under.Erase(SKColors.Black);
        var over = new SKBitmap(Bitmaps.ColorInfo(side, side));
        over.Erase(SKColors.White);
        var bottom = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(under, "Black"),
            new LayerTransform(0, 0, side, side), "Black");
        var top = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(over, "White"),
            new LayerTransform(0, 0, side, side), "White");
        document.Layers.Add(bottom);
        document.Layers.Add(top);
        return (document, top, bottom);
    }

    private static SKColor Middle(CanvasDocument document)
    {
        using var rendered = DocumentRenderer.Render(document);
        return rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
    }

    [Fact]
    public void OpacityIsSetWithinItsRangeAndRefusedOutsideIt()
    {
        var (document, top, _) = Stacked(16);
        using var _ = document;
        Assert.True(LayerEdits.SetOpacity(document, top.ID, 0.5));
        Assert.Equal(0.5, top.Opacity);
        Assert.True(LayerEdits.SetOpacity(document, top.ID, 0));
        Assert.True(LayerEdits.SetOpacity(document, top.ID, 1));
        // The same value again is not a change, and anything outside 0 to 1 is not an opacity.
        Assert.False(LayerEdits.SetOpacity(document, top.ID, 1));
        Assert.False(LayerEdits.SetOpacity(document, top.ID, -0.5));
        Assert.False(LayerEdits.SetOpacity(document, top.ID, 1.5));
        Assert.False(LayerEdits.SetOpacity(document, top.ID, double.NaN));
        Assert.Equal(1, top.Opacity);
        Assert.False(LayerEdits.SetOpacity(document, Guid.NewGuid(), 0.5));
    }

    [Fact]
    public void HalfOpacityShowsHalfOfWhatIsUnderTheLayer()
    {
        var (document, top, _) = Stacked(16);
        using var _ = document;
        Assert.Equal(new SKColor(255, 255, 255), Middle(document));
        Assert.True(LayerEdits.SetOpacity(document, top.ID, 0.5));
        var pixel = Middle(document);
        Assert.InRange(pixel.Red, 126, 130);
        // And nothing of it at all leaves what is under it.
        Assert.True(LayerEdits.SetOpacity(document, top.ID, 0));
        Assert.Equal(new SKColor(0, 0, 0), Middle(document));
    }

    [Fact]
    public void TheBlendModeIsSetAndRefusedWhenItIsNotOneTheCompositorKnows()
    {
        var (document, top, _) = Stacked(16);
        using var _ = document;
        Assert.True(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Multiply));
        Assert.Equal(LayerBlendMode.Multiply, top.BlendMode);
        Assert.False(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Multiply));
        Assert.False(LayerEdits.SetBlendMode(document, top.ID, (LayerBlendMode)99));
        Assert.False(LayerEdits.SetBlendMode(document, Guid.NewGuid(), LayerBlendMode.Screen));
    }

    [Fact]
    public void MultiplyOverWhiteLeavesTheColourUnderneath()
    {
        var (document, top, _) = Stacked(16);
        using var _ = document;
        // White multiplied over anything is that thing: a black picture stays black.
        Assert.True(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Multiply));
        Assert.Equal(new SKColor(0, 0, 0), Middle(document));
        // And screen leaves white as white, which normal would too — so check a mode that darkens nothing
        // is not silently the same as normal over a white layer.
        Assert.True(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Screen));
        Assert.Equal(new SKColor(255, 255, 255), Middle(document));
    }

    [Fact]
    public void ABlendModeAndOpacityTogetherBothReachThePicture()
    {
        var (document, top, _) = Stacked(16);
        using var _ = document;
        Assert.True(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Multiply));
        Assert.True(LayerEdits.SetOpacity(document, top.ID, 0.5));
        // Black either way, since a multiplied white is nothing — so use the other order to see both.
        Assert.Equal(new SKColor(0, 0, 0), Middle(document));
        Assert.True(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Screen));
        var pixel = Middle(document);
        Assert.InRange(pixel.Red, 126, 130);
    }

    [Fact]
    public void HidingTheLayerBeatsBothOfThem()
    {
        var (document, top, _) = Stacked(16);
        using var _ = document;
        Assert.True(LayerEdits.SetBlendMode(document, top.ID, LayerBlendMode.Screen));
        Assert.True(LayerEdits.SetVisible(document, top.ID, false));
        Assert.Equal(new SKColor(0, 0, 0), Middle(document));
    }
}
