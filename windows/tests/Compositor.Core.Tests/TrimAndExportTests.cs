using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Trimming the canvas back to its content, and writing the picture out.</summary>
public class TrimAndExportTests
{
    private static ImageLayer Patch(SKColor colour, double x, double y, int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Patch"),
            new Model.LayerTransform(x, y, width, height), "Patch");
    }

    /// <summary>A 20 by 20 canvas holding an opaque 8 by 6 patch at 5, 7.</summary>
    private static CanvasDocument Document(int width = 20, int height = 20)
    {
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        document.Layers.Add(Patch(new SKColor(220, 60, 40), 5, 7, 8, 6));
        return document;
    }

    [Fact]
    public void TrimmingTakesTheTransparentBorderAway()
    {
        using var document = Document();
        Assert.True(TrimEdits.Trim(document, new TrimOptions()));

        Assert.Equal(8, document.Width);
        Assert.Equal(6, document.Height);
        // The patch keeps its pixels and only moves, as a crop does.
        Assert.Equal(0, document.Layers[0].Transform.X);
        Assert.Equal(0, document.Layers[0].Transform.Y);
        Assert.Equal(8, document.Layers[0].Asset!.Width);
    }

    [Fact]
    public void TrimmingOnlySomeEdgesLeavesTheOthersAlone()
    {
        using var document = Document();
        Assert.True(TrimEdits.Trim(document, new TrimOptions(Top: false, Bottom: false)));

        Assert.Equal(8, document.Width);
        // The height is untouched, so the patch keeps the place it had down the canvas.
        Assert.Equal(20, document.Height);
        Assert.Equal(0, document.Layers[0].Transform.X);
        Assert.Equal(7, document.Layers[0].Transform.Y);
    }

    [Fact]
    public void ACanvasWithNothingOnItHasNothingToTrimTo()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        Assert.False(TrimEdits.Trim(document, new TrimOptions()));
        Assert.Equal(20, document.Width);
    }

    [Fact]
    public void ACanvasAlreadyTightIsLeftAlone()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 8, 6);
        using var _ = document;
        document.Layers.Add(Patch(SKColors.Red, 0, 0, 8, 6));
        Assert.False(TrimEdits.Trim(document, new TrimOptions()));
    }

    [Fact]
    public void TrimmingToTheCornerColourTakesTheMatchingBorderAway()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        // A white ground with a red patch on it: trimming to the top left colour takes the white away.
        document.Layers.Add(Patch(SKColors.White, 0, 0, 20, 20));
        document.Layers.Add(Patch(new SKColor(220, 60, 40), 5, 7, 8, 6));

        Assert.True(TrimEdits.Trim(document, new TrimOptions(BasedOn: TrimBasedOn.TopLeftPixelColor)));

        Assert.Equal(8, document.Width);
        Assert.Equal(6, document.Height);
        Assert.Equal(-5, document.Layers[0].Transform.X);
        Assert.Equal(0, document.Layers[1].Transform.X);
    }

    [Fact]
    public void TrimmingIsOneUndoStep()
    {
        using var document = Document();
        var history = new DocumentHistory();

        history.Begin("Trim", document, document.Layers[0].ID);
        Assert.True(TrimEdits.Trim(document, new TrimOptions()));
        history.End(document, document.Layers[0].ID);

        Assert.Equal(8, document.Width);
        var restored = history.Undo();
        Assert.NotNull(restored);
        Assert.Equal(20, restored!.Value.Document!.Width);
        Assert.Equal(5, restored.Value.Document!.Layers[0].Transform.X);
    }

    [Fact]
    public void APngIsWrittenAtAnySizeAndReadsBack()
    {
        using var document = Document(32, 24);
        var path = Path.Combine(Path.GetTempPath(), $"compositor-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(ImageWriter.Write(document, path));
            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(32, decoded.Width);
            Assert.Equal(24, decoded.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AJpegIsWrittenAtTheRightSizeWithTheRightColour()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 32, 24);
        document.Layers.Add(Patch(new SKColor(220, 60, 40), 0, 0, 32, 24));
        var path = Path.Combine(Path.GetTempPath(), $"compositor-{Guid.NewGuid():N}.jpg");
        try
        {
            Assert.True(ImageWriter.Write(document, path));
            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(32, decoded.Width);
            Assert.Equal(24, decoded.Height);
            // JPEG is lossy, so the colour is close rather than exact.
            var pixel = decoded.GetPixel(16, 12);
            Assert.InRange(pixel.Red, 208, 232);
            Assert.InRange(pixel.Green, 48, 72);
            Assert.InRange(pixel.Blue, 28, 52);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheQualityAJpegIsWrittenAtChangesTheFile()
    {
        // A picture with detail to lose: the quality has nothing to work with on a flat patch.
        using var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(64, 64));
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 3 % 256), (byte)(y * 5 % 256), (byte)((x + y) * 7 % 256)));
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Detail"),
            new Model.LayerTransform(0, 0, 64, 64), "Detail"));

        var low = Path.Combine(Path.GetTempPath(), $"compositor-low-{Guid.NewGuid():N}.jpg");
        var high = Path.Combine(Path.GetTempPath(), $"compositor-high-{Guid.NewGuid():N}.jpg");
        try
        {
            Assert.True(ImageWriter.Write(document, low, quality: 20));
            Assert.True(ImageWriter.Write(document, high, quality: 95));
            Assert.True(new FileInfo(high).Length > new FileInfo(low).Length,
                $"95 came out at {new FileInfo(high).Length} and 20 at {new FileInfo(low).Length}");
            // Both are JPEGs of the document's size.
            foreach (var path in new[] { low, high })
            {
                using var decoded = SKBitmap.Decode(path);
                Assert.NotNull(decoded);
                Assert.Equal(64, decoded.Width);
                Assert.Equal(64, decoded.Height);
            }
        }
        finally
        {
            File.Delete(low);
            File.Delete(high);
        }
    }

    [Fact]
    public void AFormatThatIsNotWrittenIsRefused()
    {
        using var document = Document(8, 8);
        Assert.False(ImageWriter.Knows("picture.gif"));
        Assert.False(ImageWriter.Knows("picture.bmp"));
        Assert.False(ImageWriter.Write(document, "picture.gif"));
        Assert.True(ImageWriter.Knows("picture.PNG"));
        Assert.True(ImageWriter.Knows("picture.jpeg"));
    }
}
