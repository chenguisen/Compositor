using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Adjustment layers: making one over the selected layer, and changing the settings it carries. What the
/// adjustment does to the pixels is covered by the pixel tests; these check the layer, its place in the
/// stack and what reaches the renderer.
/// </summary>
public class LayerAdjustmentTests
{
    /// <summary>A document with a mid-grey layer in it, so an adjustment has something to act on.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Grey(int side)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(new SKColor(200, 60, 40));
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Warm"),
            new LayerTransform(0, 0, side, side), "Warm");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void ANewAdjustmentLayerGoesAboveTheSelectedOneAndHoldsNoPixels()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Exposure, layer.ID);
        Assert.NotNull(made);
        var adjustment = document.Layers.First(candidate => candidate.ID == made);
        Assert.Equal(1, document.Layers.IndexOf(adjustment));
        Assert.NotNull(adjustment.Adjustment);
        Assert.Equal(AdjustmentKind.Exposure, adjustment.Adjustment!.Kind);
        Assert.Null(adjustment.Asset);
        // It covers the whole canvas, as an adjustment layer does.
        Assert.Equal(new LayerTransform(0, 0, 24, 24), adjustment.Transform);
        Assert.Equal("Exposure 1", adjustment.Name);
    }

    [Fact]
    public void TwoOfTheSameKindGetNamesOfTheirOwn()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var first = LayerPlacement.AddAdjustment(document, AdjustmentKind.Grain, layer.ID);
        LayerPlacement.AddAdjustment(document, AdjustmentKind.Grain, first);
        Assert.Equal(new[] { "Warm", "Grain 1", "Grain 2" }, document.Layers.Select(entry => entry.Name));
    }

    [Fact]
    public void ANewAdjustmentLayerSitsInTheFolderTheSelectedOneIsIn()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var folder = LayerPlacement.AddFolder(document, layer.ID);
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Invert, folder);
        Assert.Equal(document.Layers.First(entry => entry.ID == folder).ID,
            document.Layers.First(entry => entry.ID == made).ParentID);
    }

    [Fact]
    public void SettingsAreReadBackAndWrittenBack()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Exposure, layer.ID)!.Value;
        Assert.NotNull(LayerAdjustmentEdits.Settings(document, made));

        var settings = new LayerAdjustment { Kind = AdjustmentKind.Exposure, ExposureSettings = new ExposureSettings { Exposure = 1.5 } };
        Assert.True(LayerAdjustmentEdits.Set(document, made, settings));
        Assert.Equal(1.5, LayerAdjustmentEdits.Settings(document, made)!.Exposure.Exposure);

        // The same settings again is not a change.
        Assert.False(LayerAdjustmentEdits.Set(document, made, settings));
    }

    [Fact]
    public void SettingsTheRendererCannotUseAreRefused()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.GaussianBlur, layer.ID)!.Value;
        var tooWide = new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 900 };
        Assert.False(LayerAdjustmentEdits.Set(document, made, tooWide));
        Assert.Equal(10, LayerAdjustmentEdits.Settings(document, made)!.GaussianRadius);
    }

    [Fact]
    public void ALayerThatIsNotAnAdjustmentTakesNoSettings()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        Assert.Null(LayerAdjustmentEdits.Settings(document, layer.ID));
        Assert.False(LayerAdjustmentEdits.Set(document, layer.ID, new LayerAdjustment { Kind = AdjustmentKind.Invert }));
        Assert.Null(LayerAdjustmentEdits.Settings(document, layer.ID));
    }

    [Fact]
    public void AnAdjustmentLayerChangesWhatTheDocumentRenders()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        using (var before = DocumentRenderer.Render(document))
        {
            Assert.Equal(new SKColor(200, 60, 40), before.GetPixel(12, 12));
        }

        // Greyscale, from the black-and-white mix every channel is weighted into.
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.BlackWhite, layer.ID)!.Value;
        var settings = new LayerAdjustment
        {
            Kind = AdjustmentKind.BlackWhite,
            BlackWhiteSettings = new BlackWhiteSettings { Reds = 100, Yellows = 0, Greens = 0, Cyans = 0, Blues = 0, Magentas = 0 },
        };
        Assert.True(LayerAdjustmentEdits.Set(document, made, settings));
        using var after = DocumentRenderer.Render(document);
        var pixel = after.GetPixel(12, 12);
        Assert.True(pixel.Red == pixel.Green && pixel.Green == pixel.Blue, $"it is still coloured: {pixel}");
        Assert.True(pixel.Red > 0, $"the warm colour came out black: {pixel}");
    }

    [Fact]
    public void AnInvertAdjustmentTurnsThePictureOver()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Invert, layer.ID)!.Value;
        Assert.True(LayerAdjustmentEdits.Set(document, made, new LayerAdjustment { Kind = AdjustmentKind.Invert }));
        using var after = DocumentRenderer.Render(document);
        var pixel = after.GetPixel(12, 12);
        Assert.Equal(55, pixel.Red);
        Assert.Equal(195, pixel.Green);
        Assert.Equal(215, pixel.Blue);
    }

    [Fact]
    public void AnAdjustmentLayerUnderneathChangesNothingOnTopOfIt()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        // A second layer above, and the adjustment between them: what is above is drawn after the
        // adjustment, so it is not adjusted.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(24, 24));
        bitmap.Erase(new SKColor(10, 120, 40));
        var above = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Green"),
            new LayerTransform(0, 0, 24, 24), "Green");
        document.Layers.Add(above);
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Invert, layer.ID)!.Value;
        // The menu puts a new adjustment directly over the selected layer, which is the grey one, so the
        // green one is drawn after it and keeps its colours.
        Assert.True(document.Layers.IndexOf(document.Layers.First(entry => entry.ID == made)) <
                    document.Layers.IndexOf(above));
        using var after = DocumentRenderer.Render(document);
        Assert.Equal(new SKColor(10, 120, 40), after.GetPixel(12, 12));
    }

    [Fact]
    public void HidingAnAdjustmentLayerTakesItsEffectAway()
    {
        var (document, layer) = Grey(24);
        using var _ = document;
        var made = LayerPlacement.AddAdjustment(document, AdjustmentKind.Invert, layer.ID)!.Value;
        Assert.True(LayerAdjustmentEdits.Set(document, made, new LayerAdjustment { Kind = AdjustmentKind.Invert }));
        Assert.True(LayerEdits.SetVisible(document, made, false));
        using var after = DocumentRenderer.Render(document);
        Assert.Equal(new SKColor(200, 60, 40), after.GetPixel(12, 12));
    }
}
