using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// What a layer draws around itself. The rasterizer and the six effects are covered by the effect tests;
/// these are the edits the Effects menu makes, and the proof that one reaches the picture.
/// </summary>
public class LayerEffectEditTests
{
    /// <summary>A red square on an empty canvas, with room around it for a shadow or a glow.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Square(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side / 2, side / 2));
        bitmap.Erase(new SKColor(200, 40, 40));
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Square"),
            new LayerTransform(side / 4.0, side / 4.0, side / 2.0, side / 2.0), "Square");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static bool IsRed(SKColor colour) => colour.Red > colour.Green + 40 && colour.Red > colour.Blue + 40;

    [Fact]
    public void AnEffectIsPutOnTheLayerAndTakenOffAgain()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        Assert.Null(layer.Effects);
        var stroke = new LayerEffects { Stroke = new StrokeEffect { Size = 6, Blue = 1 } };
        Assert.True(LayerEdits.SetEffects(document, layer.ID, stroke));
        Assert.Equal(6, layer.Effects!.Stroke!.Size);

        Assert.True(LayerEdits.SetEffects(document, layer.ID, null));
        Assert.Null(layer.Effects);
    }

    [Fact]
    public void EffectsTheRendererCannotUseAreRefused()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        Assert.False(LayerEdits.SetEffects(document, layer.ID,
            new LayerEffects { Stroke = new StrokeEffect { Size = 9_000 } }));
        Assert.Null(layer.Effects);
        Assert.False(LayerEdits.SetEffects(document, Guid.NewGuid(),
            new LayerEffects { Stroke = new StrokeEffect() }));
    }

    [Fact]
    public void AnEmptyBagIsTheSameAsNoEffectsAtAll()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        // The panel hands back an empty bag when the one effect it was editing is ticked off.
        Assert.False(LayerEdits.SetEffects(document, layer.ID, new LayerEffects()));
        Assert.Null(layer.Effects);
    }

    [Fact]
    public void OneEffectIsChangedWithoutDisturbingTheOthers()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        Assert.True(LayerEdits.SetEffects(document, layer.ID, new LayerEffects { Stroke = new StrokeEffect { Size = 6 } }));
        Assert.True(LayerEdits.SetEffect(document, layer.ID, EffectKind.OuterGlow,
            new LayerEffects { OuterGlow = new OuterGlowEffect { Size = 12 } }));
        Assert.Equal(6, layer.Effects!.Stroke!.Size);
        Assert.Equal(12, layer.Effects.OuterGlow!.Size);

        // And one taken away leaves the other where it was.
        Assert.True(LayerEdits.SetEffect(document, layer.ID, EffectKind.Stroke, new LayerEffects()));
        Assert.Null(layer.Effects!.Stroke);
        Assert.Equal(12, layer.Effects.OuterGlow!.Size);
    }

    [Fact]
    public void TheSameEffectsAgainAreNotAChange()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        var stroke = new LayerEffects { Stroke = new StrokeEffect { Size = 6, Blue = 1, Opacity = 0.5 } };
        Assert.True(LayerEdits.SetEffects(document, layer.ID, stroke));
        // A fresh bag saying the same thing is the same effects: no edit, so no step in the history.
        Assert.False(LayerEdits.SetEffects(document, layer.ID,
            new LayerEffects { Stroke = new StrokeEffect { Size = 6, Blue = 1, Opacity = 0.5 } }));
        Assert.False(LayerEdits.SetEffect(document, layer.ID, EffectKind.Stroke,
            new LayerEffects { Stroke = new StrokeEffect { Size = 6, Blue = 1, Opacity = 0.5 } }));
        // But a different amount is.
        Assert.True(LayerEdits.SetEffects(document, layer.ID,
            new LayerEffects { Stroke = new StrokeEffect { Size = 7, Blue = 1, Opacity = 0.5 } }));
    }

    [Fact]
    public void AStrokeIsDrawnAroundWhatTheLayerShows()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        // Just outside the square there is nothing at all to begin with.
        using (var before = DocumentRenderer.Render(document))
        {
            Assert.Equal(0, before.GetPixel(14, 32).Alpha);
        }
        Assert.True(LayerEdits.SetEffect(document, layer.ID, EffectKind.Stroke,
            new LayerEffects { Stroke = new StrokeEffect { Size = 6, Green = 1 } }));
        using var after = DocumentRenderer.Render(document);
        // A green line now runs just outside the square's left edge.
        var pixel = after.GetPixel(14, 32);
        Assert.True(pixel.Alpha > 0, "the stroke was not drawn");
        Assert.True(pixel.Green > pixel.Red, $"the stroke is not the colour asked for: {pixel}");
        // And the square itself is still red.
        Assert.True(IsRed(after.GetPixel(32, 32)));
    }

    [Fact]
    public void AColourOverlayPaintsTheLayerAndTheLayerOnly()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        Assert.True(LayerEdits.SetEffect(document, layer.ID, EffectKind.ColorOverlay,
            new LayerEffects { ColorOverlay = new ColorOverlayEffect { Blue = 1 } }));
        using var after = DocumentRenderer.Render(document);
        var pixel = after.GetPixel(32, 32);
        Assert.True(pixel.Blue > pixel.Red, $"the overlay is not the colour asked for: {pixel}");
        // Outside the layer nothing was painted.
        Assert.Equal(0, after.GetPixel(2, 2).Alpha);
    }

    [Fact]
    public void ClearingTakesEveryEffectAwayAtOnce()
    {
        var (document, layer) = Square(64);
        using var _ = document;
        Assert.True(LayerEdits.SetEffects(document, layer.ID, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 4 },
            OuterGlow = new OuterGlowEffect { Size = 8 },
        }));
        Assert.True(LayerEdits.SetEffects(document, layer.ID, null));
        Assert.Null(layer.Effects);
    }
}
