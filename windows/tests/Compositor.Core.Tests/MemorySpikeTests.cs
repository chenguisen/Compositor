using System.Diagnostics;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// What this machine does with the canvas sizes the format allows. The everyday sizes run every time, so a
/// regression in cost shows up as a failure; the sizes that are known not to fit are behind
/// COMPOSITOR_SPIKE=1 so no regular run tries to allocate gigabytes.
/// </summary>
public class MemorySpikeTests
{
    private static ImageLayer Solid(SKColor colour, double x, double y, int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Model.LayerTransform(x, y, width, height, 0, false, false, LayerSampling.HighQuality), "Layer");
    }

    private static CanvasDocument Document(int canvasWidth, int canvasHeight, int layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), canvasWidth, canvasHeight);
        for (var index = 0; index < layers; index++)
        {
            var shade = (byte)(30 + index * 20);
            // A patch a third of the canvas on each side, starting inside the canvas.
            document.Layers.Add(Solid(new SKColor(shade, (byte)(255 - shade), 128),
                index * 7, index * 11, canvasWidth / 3 + 1, canvasHeight / 3 + 1));
        }
        return document;
    }

    [Fact]
    public void ATypicalCompositeFitsAndDoesNotRunAway()
    {
        var process = Process.GetCurrentProcess();
        var before = process.WorkingSet64;
        var clock = Stopwatch.StartNew();
        using var document = Document(4000, 3000, 8);
        using var result = DocumentRenderer.Render(document);
        clock.Stop();
        process.Refresh();
        var grew = (process.WorkingSet64 - before) / (1024 * 1024);

        // A 4000x3000 canvas is 45 MiB; the layer patches are a ninth of that. Anything past a few hundred
        // MiB means buffers are not being handed back.
        Assert.True(grew < 400, $"an 8-layer 4000x3000 composite grew the working set by {grew} MiB");
        Assert.True(clock.ElapsedMilliseconds < 10_000, $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(255, result.GetPixel(100, 100).Alpha);
    }

    /// <summary>
    /// The format allows a canvas 30,000 pixels on a side, which is a 3.6 GiB buffer, and this machine
    /// cannot allocate one. Tiling is what will make that size reachable; until it lands, this records
    /// the gap rather than letting it be rediscovered.
    /// </summary>
    [Fact]
    public void ACanvasAtTheFormatsSideLimitDoesNotFitInOneBuffer()
    {
        var info = new SKImageInfo(30_000, 30_000, SKColorType.Rgba8888, SKAlphaType.Premul);
        // 3.6 GB, or 3433 MiB — more than this machine will hand over in one piece.
        Assert.Equal(3433, 30_000L * 30_000 * 4 / (1024 * 1024));
        Assert.ThrowsAny<Exception>(() =>
        {
            using var bitmap = new SKBitmap(info);
            Assert.True(bitmap.ReadyToDraw);
        });
    }

    [Fact]
    public void ReportTiledExportOfAHugeCanvas()
    {
        if (Environment.GetEnvironmentVariable("COMPOSITOR_SPIKE") != "1") return;
        // 30,000 x 8,000 is 960 MiB as one RGBA buffer, and the format allows a canvas that big. Nothing
        // ever holds it: the writer renders a band of tiles at a time.
        using var document = new CanvasDocument(Guid.NewGuid(), 30_000, 8_000);
        document.Layers.Add(Solid(new SKColor(30, 40, 60), 14_000, 3_500, 600, 400));
        var blur = Solid(SKColors.White, 0, 0, 1, 1);
        blur.Asset!.Dispose();
        blur.Asset = null;
        blur.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 2 };
        document.Layers.Add(blur);

        var path = Path.Combine(Path.GetTempPath(), $"compositor-huge-{Guid.NewGuid():N}.png");
        var process = Process.GetCurrentProcess();
        var before = process.WorkingSet64;
        var clock = Stopwatch.StartNew();
        try
        {
            TiledPngWriter.Write(document, path, tileSize: 1024);
            clock.Stop();
            process.Refresh();
            var header = File.ReadAllBytes(path).AsSpan(0, 33);
            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            File.WriteAllText(@"C:\chenguisen\code_repos\Compositor\windows\memory.txt",
                $"tiled export of 30000x8000: {new FileInfo(path).Length / 1024} KiB on disk, " +
                $"header {width}x{height}, elapsed {clock.ElapsedMilliseconds} ms, " +
                $"working set grew {(process.WorkingSet64 - before) / (1024 * 1024)} MiB " +
                $"(a single buffer would be 960 MiB)\n");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
