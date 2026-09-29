using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The layer-effects rasterizer against hand-worked pixels: a solid square's stroke, its shadow, a color
/// overlay and the edges of the glows. Expected values come from the formulas the Mac build's CPU renderer
/// uses — a square dilation for a stroke, a Gaussian tail of a step for anything blurred (x/255 here is the
/// fraction of the kernel that falls past the shape's edge) — not from a run of this code.
/// </summary>
public class EffectRasterizerTests
{
    private static SKBitmap OpaqueRed(int width, int height)
    {
        var pixels = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul,
            SKColorSpace.CreateSrgb()));
        pixels.Erase(new SKColor(255, 0, 0));
        return pixels;
    }

    /// <summary>Writes a pixel's premultiplied bytes, which is what the effects are handed.</summary>
    private static void Put(SKBitmap bitmap, int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        var pixels = bitmap.GetPixelSpan();
        var at = y * bitmap.RowBytes + x * 4;
        pixels[at] = red;
        pixels[at + 1] = green;
        pixels[at + 2] = blue;
        pixels[at + 3] = alpha;
    }

    private static void A(SKBitmap bitmap, int x, int y, int red, int green, int blue, int alpha, int tolerance = 1)
    {
        var pixels = bitmap.GetPixelSpan();
        var at = y * bitmap.RowBytes + x * 4;
        var r = pixels[at];
        var g = pixels[at + 1];
        var b = pixels[at + 2];
        var a = pixels[at + 3];
        Assert.True(Math.Abs(r - red) <= tolerance && Math.Abs(g - green) <= tolerance
            && Math.Abs(b - blue) <= tolerance && Math.Abs(a - alpha) <= tolerance,
            $"({x},{y}) is ({r},{g},{b},{a}), not ({red},{green},{blue},{alpha})");
    }

    [Fact]
    public void AWideStrokeKeepsItsWidth()
    {
        using var pixels = OpaqueRed(8, 8);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 5, Inside = false, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(8 + 14, result.Width);   // margin 5 + 2

        // The layer at (7,7)–(15,15), the ring five pixels thick around it, measured along the middle row.
        var row = 11;
        A(result, 1, row, 0, 0, 0, 0, 0);
        A(result, 2, row, 0, 0, 0, 255, 0);
        A(result, 6, row, 0, 0, 0, 255, 0);
        A(result, 7, row, 255, 0, 0, 255, 0);
        A(result, 14, row, 255, 0, 0, 255, 0);
        A(result, 15, row, 0, 0, 0, 255, 0);
        A(result, 19, row, 0, 0, 0, 255, 0);
        A(result, 20, row, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void AShadowFallsAwayFromTheLight()
    {
        using var pixels = OpaqueRed(8, 8);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Shadow = new ShadowEffect { Angle = 0, Distance = 2, Blur = 0, Opacity = 0.5 },
        })!;
        var result = raster.Pixels;

        // Zero degrees is light from the right, so the shadow falls to the left of the layer.
        A(result, 2, 6, 0, 0, 0, 128, 1);
        A(result, 3, 6, 0, 0, 0, 128, 1);
        A(result, 1, 6, 0, 0, 0, 0, 0);
        A(result, 4, 6, 255, 0, 0, 255, 0);
    }

    [Fact]
    public void NothingIsDrawnWithoutAnEnabledEffect()
    {
        using var pixels = OpaqueRed(8, 8);
        Assert.Null(EffectRasterizer.Render(pixels, new LayerEffects()));
        Assert.Null(EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Enabled = false },
            Shadow = new ShadowEffect { Enabled = false },
            ColorOverlay = new ColorOverlayEffect { Enabled = false },
            InnerShadow = new InnerShadowEffect { Enabled = false },
            OuterGlow = new OuterGlowEffect { Enabled = false },
            InnerGlow = new InnerGlowEffect { Enabled = false },
        }));
    }

    [Fact]
    public void TheMarginIsTheLargestAnyOneEffectNeeds()
    {
        using var pixels = OpaqueRed(8, 8);
        // A stroke reaching 100 out, a shadow reaching 20 + 3×20, a glow reaching 3×20. They share one
        // margin, the largest of them, plus the two pixels every grown raster gets.
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 100 },
            Shadow = new ShadowEffect { Angle = 90, Distance = 20, Blur = 20 },
            OuterGlow = new OuterGlowEffect { Size = 20 },
        })!;
        Assert.Equal(8 + 204, raster.Pixels.Width);
        Assert.Equal(8 + 204, raster.Pixels.Height);
        Assert.Equal(-102, raster.OffsetX);
        Assert.Equal(-102, raster.OffsetY);

        // An inside stroke reaches nothing, so only the two pixels remain; a shadow with no blur reaches
        // exactly its distance.
        using var inside = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 4, Inside = true },
        })!;
        Assert.Equal(12, inside.Pixels.Width);
        Assert.Equal(-2, inside.OffsetX);
        using var flat = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Shadow = new ShadowEffect { Distance = 5, Blur = 0 },
        })!;
        Assert.Equal(8 + 14, flat.Pixels.Width);
    }

    [Fact]
    public void AnOutsideStrokeDrawsASquareRingOfItsSize()
    {
        using var pixels = OpaqueRed(8, 8);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = false, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        // 8 + 2 × (2 + 2): the stroke is rounded to two pixels and the raster gets two more.
        Assert.Equal(16, result.Width);
        Assert.Equal(-4, raster.OffsetX);
        Assert.Equal(-4, raster.OffsetY);

        // The layer's own pixels, at (4,4)–(12,12), are untouched.
        A(result, 5, 5, 255, 0, 0, 255, 0);

        // The ring is the shape grown by two, less the shape: two pixels wide, black and opaque.
        A(result, 2, 6, 0, 0, 0, 255, 0);
        A(result, 3, 6, 0, 0, 0, 255, 0);
        A(result, 1, 6, 0, 0, 0, 0, 0);
        A(result, 12, 6, 0, 0, 0, 255, 0);
        A(result, 13, 6, 0, 0, 0, 255, 0);
        A(result, 14, 6, 0, 0, 0, 0, 0);
        A(result, 6, 2, 0, 0, 0, 255, 0);
        A(result, 6, 13, 0, 0, 0, 255, 0);
        A(result, 6, 1, 0, 0, 0, 0, 0);

        // The reach is square: a round one would leave this corner clear.
        A(result, 2, 2, 0, 0, 0, 255, 0);
        A(result, 1, 1, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void AnInsideStrokeSitsOverTheLayersOwnEdge()
    {
        using var pixels = OpaqueRed(8, 8);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(12, result.Width);

        // Rows 0–1 and columns 0–1 of the layer erode away, so the ring is those two pixels in from every
        // edge; the four middle pixels of the layer still show.
        A(result, 2, 2, 0, 0, 0, 255, 0);    // layer (0,0)
        A(result, 4, 2, 0, 0, 0, 255, 0);    // layer (2,0)
        A(result, 3, 4, 0, 0, 0, 255, 0);    // layer (1,2)
        A(result, 4, 4, 255, 0, 0, 255, 0);  // layer (2,2)
        A(result, 7, 7, 255, 0, 0, 255, 0);  // layer (5,5)
        A(result, 8, 7, 0, 0, 0, 255, 0);    // layer (6,5)
        A(result, 7, 8, 0, 0, 0, 255, 0);    // layer (5,6)
        A(result, 9, 9, 0, 0, 0, 255, 0);    // layer (7,7)

        // An inside stroke never reaches outside the layer.
        A(result, 1, 1, 0, 0, 0, 0, 0);
        A(result, 10, 10, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void ADropShadowFallsByItsDistanceAndIsSharpWithoutBlur()
    {
        using var pixels = OpaqueRed(8, 8);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Shadow = new ShadowEffect { Angle = 90, Distance = 2, Blur = 0, Opacity = 0.5 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(16, result.Width);

        A(result, 5, 5, 255, 0, 0, 255, 0);
        // 90 degrees is light from straight above, so the shadow falls straight down: a band below the layer,
        // exactly the layer's width and two pixels tall, at half opacity.
        A(result, 5, 12, 0, 0, 0, 128, 1);
        A(result, 5, 13, 0, 0, 0, 128, 1);
        A(result, 11, 13, 0, 0, 0, 128, 1);
        A(result, 5, 14, 0, 0, 0, 0, 0);
        A(result, 3, 13, 0, 0, 0, 0, 0);
        A(result, 12, 13, 0, 0, 0, 0, 0);
        A(result, 5, 2, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void ADropShadowIsSoftenedByHalfItsBlurAsASigma()
    {
        using var pixels = OpaqueRed(64, 64);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Shadow = new ShadowEffect { Angle = 90, Distance = 0, Blur = 20, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(64 + 124, result.Width);
        var row = 62 + 32;

        // Outside the edge the shadow is a step blurred by a Gaussian of sigma 20/2: the tail past the edge
        // is 0.480 of the kernel one pixel out, 0.147 eleven out (one sigma) and 0.020 twenty-one out.
        A(result, 61, row, 0, 0, 0, 122, 2);
        A(result, 51, row, 0, 0, 0, 37, 2);
        A(result, 41, row, 0, 0, 0, 5, 2);
        // Three sigmas out the kernel has run out entirely.
        A(result, 31, row, 0, 0, 0, 0, 1);
        // Inside the layer the shadow is hidden by the layer's own pixels.
        A(result, 94, row, 255, 0, 0, 255, 0);
    }

    [Fact]
    public void AnOuterGlowIsTheSoftenedShapeOutsideIt()
    {
        using var pixels = OpaqueRed(64, 64);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            OuterGlow = new OuterGlowEffect { Size = 20, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(64 + 124, result.Width);
        var row = 62 + 32;

        // The same tail as the shadow, but white on white here, so only its alpha shows.
        A(result, 61, row, 122, 122, 122, 122, 2);
        A(result, 51, row, 37, 37, 37, 37, 2);
        A(result, 41, row, 5, 5, 5, 5, 2);
        A(result, 31, row, 0, 0, 0, 0, 1);
        // The shape itself is excluded from the glow, so the layer shows through unglowed.
        A(result, 62, row, 255, 0, 0, 255, 0);
        A(result, 63, row, 255, 0, 0, 255, 0);
    }

    [Fact]
    public void AnInnerGlowFadesFromTheBoundaryInward()
    {
        using var pixels = OpaqueRed(64, 64);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            InnerGlow = new InnerGlowEffect { Size = 20, Opacity = 1, Red = 0, Green = 0, Blue = 0 },
        })!;
        var result = raster.Pixels;
        // The glow never leaves the layer, so only the two pixels of inset remain.
        Assert.Equal(64 + 4, result.Width);
        var row = 2 + 32;

        // Its coverage is the shape less the shape blurred inward by sigma 10: at the layer's own edge
        // 1 − 0.520 = 0.480, one sigma in 1 − 0.853 = 0.147, two in 1 − 0.980 = 0.020 of black over red.
        A(result, 2, row, 133, 0, 0, 255, 2);
        A(result, 12, row, 218, 0, 0, 255, 2);
        A(result, 22, row, 250, 0, 0, 255, 2);
        A(result, 34, row, 255, 0, 0, 255, 2);

        // Nothing outside the layer.
        A(result, 1, row, 0, 0, 0, 0, 0);
        A(result, 0, row, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void AnInnerShadowSitsInsideTheEdgeItFallsFrom()
    {
        using var pixels = OpaqueRed(64, 64);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            InnerShadow = new InnerShadowEffect { Angle = 90, Distance = 10, Blur = 0, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        // An inner shadow reaches nothing outside the layer, so only the two pixels of inset remain.
        Assert.Equal(64 + 4, result.Width);

        // The shape moved down by ten, subtracted from the shape: a ten-pixel band inside the top edge.
        A(result, 40, 2, 0, 0, 0, 255, 0);    // layer row 0
        A(result, 40, 11, 0, 0, 0, 255, 0);   // layer row 9
        A(result, 40, 12, 255, 0, 0, 255, 0); // layer row 10, where the moved shape starts
        A(result, 40, 65, 255, 0, 0, 255, 0); // layer row 63
        A(result, 40, 1, 0, 0, 0, 0, 0);      // above the layer, where the shape masks it out
        // The light is above, so the left and right edges are not shaded.
        A(result, 2, 40, 255, 0, 0, 255, 0);
        A(result, 65, 40, 255, 0, 0, 255, 0);
    }

    [Fact]
    public void AColorOverlayReplacesTheColorWhereTheLayerShows()
    {
        using var pixels = OpaqueRed(8, 8);
        Put(pixels, 0, 0, 128, 0, 0, 128);   // a full red at half alpha
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            ColorOverlay = new ColorOverlayEffect { Opacity = 0.5 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(12, result.Width);

        // Half black over opaque red leaves half red.
        A(result, 5, 5, 128, 0, 0, 255, 1);
        // Over the half-alpha pixel the overlay covers only the half the layer shows: 128/255 of the pixel
        // is painted, over a pixel that is already 128/255 of red.
        A(result, 2, 2, 96, 0, 0, 160, 1);
        // Outside the layer there is nothing to paint on.
        A(result, 1, 1, 0, 0, 0, 0, 0);
        A(result, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void TheEffectsFollowTheShapeTheLayerActuallyShows()
    {
        using var pixels = OpaqueRed(8, 8);
        // Only the four middle pixels are opaque; the rest of the layer is transparent.
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if (x is < 3 or > 4 || y is < 3 or > 4) Put(pixels, x, y, 0, 0, 0, 0);
        using var raster = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 1, Inside = false, Opacity = 1 },
        })!;
        var result = raster.Pixels;
        Assert.Equal(8 + 6, result.Width);

        // The ring hugs the 2×2 block at layer (3,3)–(4,4), not the layer's rectangle.
        A(result, 5, 5, 0, 0, 0, 255, 0);    // layer (2,2), one pixel out from the block
        A(result, 7, 5, 0, 0, 0, 255, 0);    // layer (4,2), above the block
        A(result, 6, 6, 255, 0, 0, 255, 0);  // layer (3,3), in the block
        A(result, 4, 4, 0, 0, 0, 0, 0);      // layer (1,1), two pixels out
        A(result, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void ADisabledEffectChangesNothing()
    {
        using var pixels = OpaqueRed(8, 8);
        using var alone = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true, Opacity = 1 },
        })!;
        using var crowded = EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 2, Inside = true, Opacity = 1 },
            Shadow = new ShadowEffect { Enabled = false, Distance = 6, Blur = 4 },
            ColorOverlay = new ColorOverlayEffect { Enabled = false, Opacity = 1 },
            InnerShadow = new InnerShadowEffect { Enabled = false, Distance = 6 },
            OuterGlow = new OuterGlowEffect { Enabled = false, Size = 30 },
            InnerGlow = new InnerGlowEffect { Enabled = false, Size = 30 },
        })!;

        // The switched-off effects left no trace, not even in the margin their settings would have asked for.
        Assert.Equal(alone.Pixels.Width, crowded.Pixels.Width);
        Assert.Equal(alone.Pixels.Height, crowded.Pixels.Height);
        Assert.Equal(alone.Pixels.GetPixelSpan().ToArray(), crowded.Pixels.GetPixelSpan().ToArray());
        Assert.Equal(alone.OffsetX, crowded.OffsetX);
    }

    [Fact]
    public void AnOutOfRangeEffectIsRefused()
    {
        using var pixels = OpaqueRed(8, 8);
        // The Mac build fails the render rather than drawing a stroke it cannot honour.
        var error = Assert.Throws<ProjectException>(() => EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = StrokeEffect.MaxSize + 1 },
        }));
        Assert.Equal(ProjectError.Invalid, error.Error);

        // A switched-off effect is never checked: the Mac build only validates what it draws.
        Assert.Null(EffectRasterizer.Render(pixels, new LayerEffects
        {
            Stroke = new StrokeEffect { Enabled = false, Size = StrokeEffect.MaxSize + 1 },
        }));
    }
}
