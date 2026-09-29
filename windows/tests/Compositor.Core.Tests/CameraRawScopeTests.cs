using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The Camera Raw panel's scope: three 256-bin ribbons and a vectorscope, counted from the graded pixels of
/// the picture the panel is showing. What is checked here is what the drawing reads — where a picture's weight
/// lands, that an empty picture counts as nothing, and that the count comes from the grade rather than from
/// the layer as it was.
/// </summary>
public class CameraRawScopeTests
{
    /// <summary>A layer of one flat colour, large enough to fill the frame.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Flat(SKColor colour, int side = 40)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(colour);
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
            new LayerTransform(0, 0, side, side), "Flat");
        document.Layers.Add(layer);
        return (document, layer);
    }

    /// <summary>The weight a ribbon holds, which is the whole picture for a flat one.</summary>
    private static double Total(double[] ribbon) => ribbon.Sum();

    /// <summary>The level most of the picture sits at, which is the bin holding the most weight.</summary>
    private static int Busiest(double[] ribbon) => Array.IndexOf(ribbon, ribbon.Max());

    [Fact]
    public void EveryChannelIsCountedSeparately()
    {
        var (document, layer) = Flat(new SKColor(10, 120, 250));
        using var _ = document;
        using var work = Bitmaps.Premultiplied(layer.Asset!.Image);
        var scope = CameraRawScope.OfPremultiplied(work);
        // Each ribbon holds every pixel at that channel's own level, so the three read differently even though
        // every pixel is the same colour.
        Assert.Equal(1600, Total(scope.Red), 3);
        Assert.Equal(1600, Total(scope.Green), 3);
        Assert.Equal(1600, Total(scope.Blue), 3);
        Assert.Equal(10, Busiest(scope.Red));
        Assert.Equal(120, Busiest(scope.Green));
        Assert.Equal(250, Busiest(scope.Blue));
    }

    [Fact]
    public void APictureWithNothingInItCountsAsNothing()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
        bitmap.Erase(SKColors.Transparent);
        using var empty = ImportedImage.Create(bitmap, "Empty");
        using var work = Bitmaps.Premultiplied(empty.Image);
        var scope = CameraRawScope.OfPremultiplied(work);
        Assert.Equal(0, scope.Peak);
        Assert.Equal(0, scope.ScopePeak);
        Assert.All(scope.Red, count => Assert.Equal(0, count));
        Assert.All(scope.Vectorscope, count => Assert.Equal(0, count));
    }

    [Fact]
    public void EachHueIsPlottedWhereItsAnglePutsIt()
    {
        // The scope's two axes are hue and saturation, so a fully saturated colour sits at its own angle
        // three quarters of the way out. Red is the angle zero, green a third of the way round and blue two
        // thirds, which is where these cells are.
        foreach (var (colour, column, row, name) in new[]
                 {
                     (SKColors.Red, 62, 32, "red"),
                     (SKColors.Green, 16, 58, "green"),
                     (SKColors.Blue, 16, 5, "blue"),
                 })
        {
            var (document, layer) = Flat(colour);
            using var _ = document;
            using var work = Bitmaps.Premultiplied(layer.Asset!.Image);
            var scope = CameraRawScope.OfPremultiplied(work);
            var at = Busiest(scope.Vectorscope);
            var x = at % CameraRawScope.ScopeSide;
            var y = at / CameraRawScope.ScopeSide;
            Assert.True(Math.Abs(x - column) <= 1 && Math.Abs(y - row) <= 1,
                $"{name} is plotted at {x},{y} rather than {column},{row}");
        }
    }

    [Fact]
    public void AGreyPictureHasNoHueSoItIsNotPlottedAtAll()
    {
        var (document, layer) = Flat(new SKColor(128, 128, 128));
        using var _ = document;
        using var work = Bitmaps.Premultiplied(layer.Asset!.Image);
        var scope = CameraRawScope.OfPremultiplied(work);
        Assert.Equal(0, scope.ScopePeak);
        // The ribbons still hold it: a grey is a level on every channel, whatever the vectorscope makes of it.
        Assert.Equal(1600, Total(scope.Red), 3);
    }

    [Fact]
    public void ALargePictureIsCountedFromAReducedCopy()
    {
        // The count is a shape rather than a measurement, so a picture longer than the limit is reduced first.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(3000, 1500));
        bitmap.Erase(new SKColor(200, 40, 40));
        using var wide = ImportedImage.Create(bitmap, "Wide");
        using var work = Bitmaps.Premultiplied(wide.Image);
        var scope = CameraRawScope.OfPremultiplied(work);
        var reduced = (long)CameraRawScope.SampleLimit * Math.Round(1500.0 * CameraRawScope.SampleLimit / 3000);
        Assert.Equal(reduced, Total(scope.Red), reduced * 0.02);
        Assert.Equal(200, Busiest(scope.Red));
    }

    [Fact]
    public void ThePeakCapsAnIsolatedSpikeSoAFlatBackgroundCannotFlattenTheRest()
    {
        // Nine tenths of the picture is one level and the rest is spread over forty others. Drawn to the true
        // peak the spread would be a line along the floor; the cap leaves it standing four times the typical.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(100, 100));
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var level = x < 90 ? 20 : 100 + y % 40;
                bitmap.SetPixel(x, y, new SKColor((byte)level, (byte)level, (byte)level));
            }
        }
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Spike"),
            new LayerTransform(0, 0, 100, 100), "Spike");
        document.Layers.Add(layer);
        using var work = Bitmaps.Premultiplied(layer.Asset!.Image);
        var scope = CameraRawScope.OfPremultiplied(work);
        var spike = scope.Red.Max();
        Assert.True(spike > 8000, $"the flat background is not the spike it was meant to be: {spike}");
        Assert.True(scope.Peak > 0, "the peak came out at nothing");
        Assert.True(scope.Peak < spike / 20,
            $"the peak {scope.Peak} was not brought well below the spike {spike}");
    }

    [Fact]
    public void TheScopeCountsTheGradeThePanelIsShowing()
    {
        var (document, layer) = Flat(new SKColor(128, 128, 128));
        using var _ = document;
        using (var straight = Bitmaps.Premultiplied(layer.Asset!.Image))
        {
            Assert.Equal(128, Busiest(CameraRawScope.OfPremultiplied(straight).Red));
        }
        // Five stops darker: the scope must move with the grade, since it describes what the panel is showing.
        var settings = new CameraRawSettings { Exposure = -5 };
        var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        using var holder = preview;
        CameraRawScope? scope = null;
        Assert.True(preview!.Show(target => CameraRawEdits.Preview(target, layer.ID, settings,
            shadows: false, highlights: false, sharpenMask: false, out scope)));
        Assert.NotNull(scope);
        Assert.True(Busiest(scope!.Red) < 20, $"the graded picture did not move down the ribbon: {Busiest(scope.Red)}");
    }

    [Fact]
    public void TheOverlayIsNotCountedBecauseTheScopeIsOfTheGrade()
    {
        // Clipped shadows are painted blue over the picture, but the scope counts the grade before the paint:
        // a black picture with the shadow view on counts as black, not as blue. The paint did happen, so the
        // two are not the same thing.
        var (document, layer) = Flat(SKColors.Black);
        using var _ = document;
        var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        using var holder = preview;
        CameraRawScope? scope = null;
        Assert.True(preview!.Show(target => CameraRawEdits.Preview(target, layer.ID, new CameraRawSettings(),
            shadows: true, highlights: false, sharpenMask: false, out scope)));
        Assert.NotNull(scope);
        Assert.Equal(0, Busiest(scope!.Blue));
        // What the panel paints is on the preview's own copy of the layer, which is the picture the canvas
        // draws; the layer the document holds is untouched, as nothing here is committed.
        var shown = preview.Document.Layers.First(one => one.ID == layer.ID);
        Assert.True(shown.Asset!.Image.GetPixel(5, 5).Blue > 150, "the shadow overlay was never painted");
        Assert.True(layer.Asset!.Image.GetPixel(5, 5).Blue < 150, "the overlay reached the document's own layer");
    }

    [Fact]
    public void TheGeometryIsPartOfWhatThePanelShows()
    {
        // A turn is the geometry amount whose effect on a flat picture can be read off the scope: the warp
        // turns the picture inside its own grid, so its corners are cut away and less of it is left.
        var (document, layer) = Flat(SKColors.White, 60);
        using var _ = document;
        var settings = new CameraRawSettings
        {
            Geometry = new CameraRawGeometrySettings { Rotate = 20 },
        };
        Assert.True(settings.AdjustsGeometry);
        var preview = FilterPreview.Begin(document, layer.ID);
        Assert.NotNull(preview);
        using var holder = preview;
        CameraRawScope? scope = null;
        Assert.True(preview!.Show(target => CameraRawEdits.Preview(target, layer.ID, settings,
            shadows: true, highlights: false, sharpenMask: false, out scope)));
        Assert.NotNull(scope);
        var left = Total(scope!.Red);
        Assert.True(left < 3600, $"the turn did not change the picture the scope counted: {left} of 3600");
        Assert.True(left > 2000, $"too much of the picture was lost: {left} of 3600");
    }
}
