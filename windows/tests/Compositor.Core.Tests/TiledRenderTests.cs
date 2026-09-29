using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// A canvas drawn in pieces has to come out exactly as it would in one go. This is the test that proves the
/// tiling contract: every pixel of every piece is compared against the same pixel of a whole-canvas render.
/// The document holds a blur adjustment so the halo is exercised, and grain and noise so the patterns that
/// are anchored to the document have to line up across pieces.
/// </summary>
public class TiledRenderTests
{
    private const int Tile = 8;

    private static ImageLayer Solid(SKColor colour, double x, double y, int width, int height,
        double opacity = 1, LayerBlendMode blend = LayerBlendMode.Normal, bool visible = true)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Model.LayerTransform(x, y, width, height, 0, false, false, LayerSampling.HighQuality), "Layer")
        {
            Opacity = opacity,
            BlendMode = blend,
            IsVisible = visible,
        };
    }

    private static ImageLayer Empty(string name, double value, AdjustmentKind kind, double opacity = 1)
    {
        var layer = Solid(SKColors.White, 0, 0, 1, 1);
        layer.Asset!.Dispose();
        layer.Asset = null;
        layer.Opacity = opacity;
        layer.Name = name;
        layer.Adjustment = kind switch
        {
            AdjustmentKind.GaussianBlur => new LayerAdjustment { Kind = kind, BlurRadius = value },
            AdjustmentKind.Grain => new LayerAdjustment
            {
                Kind = kind,
                GrainSettings = new GrainSettings { Amount = value, Size = 1, Roughness = 50, Seed = 7 },
            },
            AdjustmentKind.AddNoise => new LayerAdjustment
            {
                Kind = kind,
                NoiseAmount = value, NoiseSeed = 11, NoiseGaussian = false, NoiseMonochromatic = false,
            },
            _ => new LayerAdjustment { Kind = kind },
        };
        return layer;
    }

    private static Model.LayerMask UniformMask(byte value)
    {
        var bitmap = new SKBitmap(Bitmaps.MaskInfo(1, 1));
        bitmap.Erase(new SKColor(value, value, value));
        return Model.LayerMask.AssetFrom(bitmap);
    }

    private static CanvasDocument Build()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 37, 29);
        document.Layers.Add(Solid(new SKColor(200, 180, 160), 0, 0, 37, 29));
        document.Layers.Add(Empty("Blur", 2, AdjustmentKind.GaussianBlur));
        document.Layers.Add(Empty("Invert", 0, AdjustmentKind.Invert, opacity: 0.5));
        document.Layers.Add(Empty("Grain", 40, AdjustmentKind.Grain));
        document.Layers.Add(Empty("Noise", 25, AdjustmentKind.AddNoise));

        var patch = Solid(new SKColor(220, 60, 40), 3, 4, 9, 7);
        patch.Mask = UniformMask(200);
        patch.Effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Red = 1, Green = 1, Blue = 1, Opacity = 1 },
            Shadow = new ShadowEffect { Angle = 90, Distance = 3, Blur = 2, Opacity = 0.6 },
        };
        document.Layers.Add(patch);

        var folder = Solid(SKColors.White, 0, 0, 1, 1, opacity: 0.7);
        folder.IsGroup = true;
        folder.Asset!.Dispose();
        folder.Asset = null;
        var child = Solid(new SKColor(90, 200, 120), 20, 10, 12, 9, blend: LayerBlendMode.Screen);
        child.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(child);

        var baseLayer = Solid(SKColors.White, 2, 20, 6, 5);
        var clipped = Solid(new SKColor(40, 90, 220), 0, 18, 20, 12);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        document.Layers.Add(Solid(SKColors.Magenta, 0, 0, 37, 29, visible: false));
        return document;
    }

    [Fact]
    public void RenderingInPiecesMatchesRenderingWhole()
    {
        using var document = Build();
        using var whole = DocumentRenderer.Render(document);
        Assert.Equal(37, whole.Width);
        Assert.Equal(29, whole.Height);

        var pieces = 0;
        for (var y = 0; y < document.Height; y += Tile)
        {
            for (var x = 0; x < document.Width; x += Tile)
            {
                var region = SKRectI.Create(x, y, Math.Min(Tile, document.Width - x), Math.Min(Tile, document.Height - y));
                using var piece = DocumentRenderer.RenderRegion(document, region);
                Assert.Equal(region.Width, piece.Width);
                Assert.Equal(region.Height, piece.Height);
                for (var row = 0; row < region.Height; row++)
                {
                    for (var column = 0; column < region.Width; column++)
                    {
                        var expected = whole.GetPixel(region.Left + column, region.Top + row);
                        var actual = piece.GetPixel(column, row);
                        Assert.True(expected == actual,
                            $"({region.Left + column},{region.Top + row}) whole {expected} but piece {actual}");
                    }
                }
                pieces++;
            }
        }
        Assert.Equal(20, pieces);
    }

    [Fact]
    public void TheTiledPngWriterWritesWhatTheCompositorRenders()
    {
        using var document = Build();
        using var reference = DocumentRenderer.Render(document);
        var path = Path.Combine(Path.GetTempPath(), $"compositor-writer-{Guid.NewGuid():N}.png");
        try
        {
            // A tile small enough to force several bands, so the writer's row stitching is exercised.
            TiledPngWriter.Write(document, path, tileSize: 8);
            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(reference.Width, decoded.Width);
            Assert.Equal(reference.Height, decoded.Height);
            for (var y = 0; y < reference.Height; y++)
            {
                for (var x = 0; x < reference.Width; x++)
                {
                    var expected = reference.GetPixel(x, y);
                    var actual = decoded.GetPixel(x, y);
                    Assert.True(expected == actual, $"({x},{y}) rendered {expected} but the PNG holds {actual}");
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APieceOutsideTheCanvasComesBackEmpty()
    {
        using var document = Build();
        using var piece = DocumentRenderer.RenderRegion(document, SKRectI.Create(37, 29, 5, 5));
        Assert.Equal(5, piece.Width);
        Assert.Equal(0, piece.GetPixel(2, 2).Alpha);
    }

    [Fact]
    public void APieceLargerThanTheCanvasIsPaddedWithNothing()
    {
        using var document = Build();
        using var piece = DocumentRenderer.RenderRegion(document, SKRectI.Create(-4, -4, 45, 40));
        Assert.Equal(45, piece.Width);
        Assert.Equal(40, piece.Height);
        Assert.Equal(0, piece.GetPixel(0, 0).Alpha);
        // The canvas's own corner sits four pixels in, and it has been painted.
        Assert.True(piece.GetPixel(4, 4).Alpha > 0);
        // The canvas is 37x29 and sits four pixels in, so its last painted pixel is at (40, 32).
        Assert.True(piece.GetPixel(40, 32).Alpha > 0);
        Assert.Equal(0, piece.GetPixel(40, 33).Alpha);
    }
}
