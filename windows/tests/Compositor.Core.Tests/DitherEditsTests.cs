using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Filter ▸ Dither over a layer's own pixels. The kernel is covered by the pixel tests; these check the
/// pipeline around it — chunky pixels, the dot shape, colours, selection — and that each look does what it
/// says on the picture.
/// </summary>
public class DitherEditsTests
{
    /// <summary>A layer of every tone from black at the left to white at the right.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Ramp(int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = (byte)Math.Round((width <= 1 ? 0 : (double)x / (width - 1)) * 255);
                bitmap.SetPixel(x, y, new SKColor(level, level, level));
            }
        }
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Ramp"),
            new LayerTransform(0, 0, width, height), "Ramp");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static int Tones(ImageLayer layer) =>
        Enumerable.Range(0, layer.Asset!.Width)
            .Select(x => layer.Asset!.Image.GetPixel(x, layer.Asset.Height / 2).Red)
            .Distinct()
            .Count();

    [Fact]
    public void DefaultsAreTheLookDitherOpensWithAndAreWithinRange()
    {
        var settings = new DitherSettings().Normalized();
        Assert.Equal(2, settings.PixelSize);
        Assert.Equal(2, settings.Levels);
        Assert.Equal(100, settings.Diffusion);
        Assert.Equal(45, settings.Angle);
        Assert.Equal(" .:-=+*#%@", settings.Characters);
        Assert.True(settings.LightOnDark);
    }

    [Fact]
    public void OutOfRangeAmountsAreBroughtBackIntoRange()
    {
        var settings = new DitherSettings
        {
            PixelSize = 900, Levels = 1, CellSize = 500, Diffusion = -40, Angle = 400,
            Density = 900, Contrast = -900, DarkRed = 4,
        }.Normalized();
        Assert.Equal(32, settings.PixelSize);
        Assert.Equal(2, settings.Levels);
        Assert.Equal(64, settings.CellSize);
        Assert.Equal(0, settings.Diffusion);
        Assert.Equal(90, settings.Angle);
        Assert.Equal(100, settings.Density);
        Assert.Equal(-100, settings.Contrast);
        Assert.Equal(1, settings.DarkRed);
    }

    [Fact]
    public void AtkinsonReducesTheRampToInkAndPaper()
    {
        var (document, layer) = Ramp(80, 20);
        using var _ = document;
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Atkinson, new DitherSettings()));
        // Two tones is one bit: every pixel is one of the two colours, and the ramp's ends are among them.
        var across = Enumerable.Range(0, 80).Select(x => layer.Asset!.Image.GetPixel(x, 10).Red).Distinct().OrderBy(v => v).ToList();
        Assert.Equal(2, across.Count);
        Assert.Equal(0, across[0]);
        Assert.Equal(255, across[1]);
    }

    [Fact]
    public void MoreTonesKeepMoreTones()
    {
        var (two, twoLayer) = Ramp(80, 20);
        using var _two = two;
        Assert.True(DitherEdits.Apply(two, twoLayer.ID, DitherStyle.Bayer8, new DitherSettings { Levels = 2 }));

        var (eight, eightLayer) = Ramp(80, 20);
        using var _eight = eight;
        Assert.True(DitherEdits.Apply(eight, eightLayer.ID, DitherStyle.Bayer8, new DitherSettings { Levels = 8 }));

        Assert.True(Tones(eightLayer) > Tones(twoLayer),
            $"{Tones(eightLayer)} tones is not more than {Tones(twoLayer)}");
    }

    [Fact]
    public void EveryLookChangesTheRampAndTheyAreNotAllTheSame()
    {
        var looks = new List<byte[]>();
        foreach (var style in Enum.GetValues<DitherStyle>())
        {
            var (document, layer) = Ramp(64, 16);
            using var _ = document;
            Assert.True(DitherEdits.Apply(document, layer.ID, style, new DitherSettings()), $"{style} did nothing");
            var row = new byte[64];
            for (var x = 0; x < 64; x++) row[x] = layer.Asset!.Image.GetPixel(x, 8).Red;
            // Each look leaves more than one tone behind, and leaves its own mark on the ramp.
            Assert.True(row.Distinct().Count() > 1, $"{style} left one tone");
            looks.Add(row);
        }
        for (var index = 1; index < looks.Count; index++)
        {
            Assert.False(looks[index].SequenceEqual(looks[index - 1]),
                $"{Enum.GetValues<DitherStyle>()[index]} looks the same as the one before it");
        }
    }

    [Fact]
    public void ChunkyPixelsMakeEachDitheredPixelABlockOfTheLayersOwn()
    {
        var (document, layer) = Ramp(64, 16);
        using var _ = document;
        var settings = new DitherSettings { PixelSize = 4, Diffusion = 0, Density = 0, Contrast = 0 };
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.FloydSteinberg, settings));
        // Every four-by-four square is one dithered pixel, so it is one colour.
        for (var y = 0; y < 16; y += 4)
        {
            for (var x = 0; x < 64; x += 4)
            {
                var corner = layer.Asset!.Image.GetPixel(x, y);
                for (var j = 0; j < 4; j++)
                    for (var i = 0; i < 4; i++)
                        Assert.Equal(corner, layer.Asset!.Image.GetPixel(x + i, y + j));
            }
        }
    }

    [Fact]
    public void TheDotShapeLeavesTheGapColourInTheCornersOfEachBlock()
    {
        // A flat white layer, so every block is inked and the dot's edge is the only thing being looked at.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(32, 16));
        bitmap.Erase(SKColors.White);
        using var document = new CanvasDocument(Guid.NewGuid(), 32, 16);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "White"),
            new LayerTransform(0, 0, 32, 16), "White");
        document.Layers.Add(layer);

        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Atkinson,
            new DitherSettings { PixelSize = 8, PixelShape = DitherPixelShape.Dot }));
        // The block's middle is the dot and its corner is the gap between dots.
        Assert.Equal(255, layer.Asset!.Image.GetPixel(4, 4).Red);
        Assert.Equal(0, layer.Asset.Image.GetPixel(0, 0).Red);
        Assert.Equal(0, layer.Asset.Image.GetPixel(7, 7).Red);
    }

    [Fact]
    public void TheDotShapesGapIsTheDarkColourWhenTwoAreUsed()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(32, 16));
        bitmap.Erase(SKColors.White);
        using var document = new CanvasDocument(Guid.NewGuid(), 32, 16);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "White"),
            new LayerTransform(0, 0, 32, 16), "White");
        document.Layers.Add(layer);

        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Atkinson, new DitherSettings
        {
            PixelSize = 8,
            PixelShape = DitherPixelShape.Dot,
            Colors = DitherColors.TwoColors,
            DarkRed = 0, DarkGreen = 0, DarkBlue = 0.5,
        }));
        Assert.Equal(new SKColor(0, 0, 128), layer.Asset!.Image.GetPixel(0, 0));
        Assert.Equal(new SKColor(255, 255, 255), layer.Asset.Image.GetPixel(4, 4));
    }

    [Fact]
    public void TwoColoursAreUsedInsteadOfInkAndPaper()
    {
        var (document, layer) = Ramp(64, 16);
        using var _ = document;
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Atkinson, new DitherSettings
        {
            Colors = DitherColors.TwoColors,
            DarkRed = 0, DarkGreen = 0, DarkBlue = 0.5,
            LightRed = 1, LightGreen = 0, LightBlue = 0,
        }));
        // Nothing is white any more: the light tone is the colour that was asked for.
        for (var x = 0; x < 64; x++)
        {
            var colour = layer.Asset!.Image.GetPixel(x, 8);
            Assert.True((colour.Red == 255 && colour.Green == 0) || (colour.Red == 0 && colour.Blue == 128),
                $"an unexpected colour at {x}: {colour}");
        }
    }

    [Fact]
    public void TheOriginalColoursAreKeptWhenTheyAreAskedFor()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(new SKColor(180, 90, 40));
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Warm"),
            new LayerTransform(0, 0, 40, 20), "Warm");
        document.Layers.Add(layer);
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.FloydSteinberg,
            new DitherSettings { Colors = DitherColors.Original, Density = 30 }));

        // Each channel is dithered on its own, so the picture is made of primaries; what must survive is the
        // average the eye reads, which is still the warm colour it started as.
        double red = 0, green = 0, blue = 0;
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var colour = layer.Asset!.Image.GetPixel(x, y);
                red += colour.Red;
                green += colour.Green;
                blue += colour.Blue;
            }
        }
        Assert.True(red > green && green > blue, $"the average is {red:0} {green:0} {blue:0}");
        Assert.True(red / 800 > 100 && blue / 800 < 40, $"the average is {red / 800:0} {green / 800:0} {blue / 800:0}");
        // And it is not simply the flat colour left alone.
        Assert.True(layer.Asset!.Image.GetPixel(0, 0) != new SKColor(180, 90, 40));
    }

    [Fact]
    public void AsciiPutsMoreThanOneToneInACell()
    {
        var (document, layer) = Ramp(120, 40);
        using var _ = document;
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Ascii, new DitherSettings { TextSize = 14 }));
        // A character is drawn in cells about fourteen pixels tall and eight wide, and its strokes are finer
        // than the pixel grid, so a cell holds both the ink and the paper.
        var tones = Enumerable.Range(0, 120)
            .SelectMany(x => Enumerable.Range(0, 40).Select(y => layer.Asset!.Image.GetPixel(x, y).Red))
            .Distinct()
            .Count();
        Assert.True(tones > 2, $"the characters were not drawn: only {tones} tones");
    }

    [Fact]
    public void DitherInsideASelectionLeavesTheRestAlone()
    {
        var (document, layer) = Ramp(64, 16);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(0, 0, 32, 16));
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Atkinson, new DitherSettings()));
        // The right half, outside the selection, is still the ramp it was.
        for (var x = 32; x < 64; x++)
        {
            var level = (byte)Math.Round((double)x / 63 * 255);
            Assert.Equal(level, layer.Asset!.Image.GetPixel(x, 8).Red);
        }
        // And the selected half is not.
        Assert.True(Enumerable.Range(0, 32).Any(x => layer.Asset!.Image.GetPixel(x, 8).Red != Math.Round((double)x / 63 * 255)),
            "the selected half was not dithered");
    }

    [Fact]
    public void TransparentPixelsAndAlphaAreLeftStanding()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(new SKColor(200, 200, 200, 128));
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                bitmap.SetPixel(x, y, SKColors.Transparent);
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Half"),
            new LayerTransform(0, 0, 40, 20), "Half");
        document.Layers.Add(layer);
        Assert.True(DitherEdits.Apply(document, layer.ID, DitherStyle.Atkinson, new DitherSettings()));
        Assert.Equal(0, layer.Asset!.Image.GetPixel(10, 10).Alpha);
        Assert.Equal(128, layer.Asset.Image.GetPixel(30, 10).Alpha);
    }

    [Fact]
    public void ALayerWithNoPixelsIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        Assert.False(DitherEdits.Apply(document, blank.ID, DitherStyle.Atkinson, new DitherSettings()));
    }
}
