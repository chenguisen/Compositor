using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Pixels;

/// <summary>The layer's pixels with its effects drawn around them, in layer-local pixels.</summary>
public sealed class EffectRaster : IDisposable
{
    internal EffectRaster(SKBitmap pixels, int offsetX, int offsetY)
    {
        Pixels = pixels;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public SKBitmap Pixels { get; }

    /// <summary>Where the raster's top-left corner sits in the layer's own coordinates.</summary>
    public int OffsetX { get; }

    public int OffsetY { get; }

    public void Dispose() => Pixels.Dispose();
}

/// <summary>
/// Port of the Mac build's CPU layer-effects renderer (<c>LayerEffectsRenderer.render</c> in
/// <c>Document/LayerEffects.swift</c>), which is what that build falls back to when Metal is unavailable.
/// The layer's pixels sit on a canvas grown by the effects' margin, and the effects are drawn in the Mac
/// build's order: drop shadow, outer glow, outside stroke, the pixels, color overlay, inner glow, inner
/// shadow, inside stroke. Nothing here applies the layer's own opacity — that is the compositor's, applied
/// to the whole raster.
/// </summary>
public static class EffectRasterizer
{
    /// <summary>
    /// Draws the enabled effects. The pixels handed in already have the layer's mask applied — that is the
    /// Mac build's order, and it means the caller must not mask the result again. The raster grows to fit
    /// whatever the effects add around the pixels. Null when no effect is enabled; throws when an enabled
    /// effect's settings are out of range, or when the grown raster would exceed the surface limit.
    /// <paramref name="pixels"/> must be premultiplied sRGB RGBA8888.
    /// </summary>
    public static EffectRaster? Render(SKBitmap pixels, LayerEffects effects)
    {
        if (pixels.ColorType != SKColorType.Rgba8888 || pixels.AlphaType == SKAlphaType.Unpremul)
            throw new ArgumentException("Effect pixels must be premultiplied RGBA8888.", nameof(pixels));
        var visible = Visible(effects);
        if (visible.IsEmpty) return null;
        if (!visible.IsValid) throw new ProjectException(ProjectError.Invalid);

        var inset = Margin(visible);
        var width = pixels.Width + inset * 2;
        var height = pixels.Height + inset * 2;
        if (width <= 0 || height <= 0 || (long)width * height > DocumentLimits.MaxSurfacePixels)
            throw new ProjectException(ProjectError.TooLarge);

        var count = width * height;
        byte[] canvas;
        float[] shape, work, temp;
        try
        {
            canvas = new byte[count * 4];
            shape = new float[count];
            work = new float[count];
            temp = new float[count];
        }
        catch (OutOfMemoryException)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        // The shape the effects follow: the layer's alpha, on the grown canvas. Its colour is only wanted
        // where the layer is drawn, and at the grown canvas' own size.
        var source = pixels.GetPixelSpan();
        var stride = pixels.RowBytes;
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = (y + inset) * width + inset;
            var line = y * stride;
            for (var x = 0; x < pixels.Width; x++) shape[row + x] = source[line + x * 4 + 3] / 255f;
        }

        if (visible.Shadow is { } shadow && shadow.Opacity > 0)
        {
            var (dx, dy) = Offset(shadow.Angle, shadow.Distance);
            Shift(shape, work, width, height, dx, dy);
            Blur(work, work, temp, width, height, shadow.Blur);
            Fill(canvas, work, shadow.Red, shadow.Green, shadow.Blue, shadow.Opacity);
        }
        if (visible.OuterGlow is { } glow && glow.Opacity > 0)
        {
            // The shape softened omnidirectionally, less the shape itself: only the outside shows.
            Blur(shape, work, temp, width, height, glow.Size);
            for (var i = 0; i < count; i++) work[i] = Math.Clamp(work[i] * (1 - shape[i]), 0f, 1f);
            Fill(canvas, work, glow.Red, glow.Green, glow.Blue, glow.Opacity);
        }
        // A stroke with no size or no opacity is skipped, but still counts towards the margin, as on the Mac.
        var stroke = visible.Stroke is { } s && s.Size > 0 && s.Opacity > 0 ? s : null;
        if (stroke is { Inside: false }) FillRing(canvas, shape, work, temp, width, height, stroke);

        Over(canvas, source, stride, pixels.Width, pixels.Height, inset, width);

        // Over the pixels: a flat color, then a shadow inside the layer's own edges.
        if (visible.ColorOverlay is { } overlay && overlay.Opacity > 0)
            Fill(canvas, shape, overlay.Red, overlay.Green, overlay.Blue, overlay.Opacity);
        if (visible.InnerGlow is { } innerGlow && innerGlow.Opacity > 0)
        {
            Blur(shape, work, temp, width, height, innerGlow.Size);
            for (var i = 0; i < count; i++) work[i] = Math.Clamp(shape[i] * (1 - work[i]), 0f, 1f);
            Fill(canvas, work, innerGlow.Red, innerGlow.Green, innerGlow.Blue, innerGlow.Opacity);
        }
        if (visible.InnerShadow is { } inner && inner.Opacity > 0)
        {
            var (dx, dy) = Offset(inner.Angle, inner.Distance);
            Shift(shape, work, width, height, dx, dy);
            Blur(work, work, temp, width, height, inner.Blur);
            for (var i = 0; i < count; i++) work[i] = Math.Clamp(shape[i] * (1 - work[i]), 0f, 1f);
            Fill(canvas, work, inner.Red, inner.Green, inner.Blue, inner.Opacity);
        }
        // An inside stroke is drawn over the pixels, or they would simply cover it.
        if (stroke is { Inside: true }) FillRing(canvas, shape, work, temp, width, height, stroke);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var raster = new SKBitmap(info);
        if (!raster.ReadyToDraw)
        {
            raster.Dispose();
            throw new ProjectException(ProjectError.TooLarge);
        }
        var target = raster.GetPixelSpan();
        for (var y = 0; y < height; y++)
            canvas.AsSpan(y * width * 4, width * 4).CopyTo(target.Slice(y * raster.RowBytes, width * 4));
        return new EffectRaster(raster, -inset, -inset);
    }

    /// <summary>The effects that are actually drawn: present and not switched off.</summary>
    private static LayerEffects Visible(LayerEffects effects) => new()
    {
        Stroke = effects.Stroke is { } stroke && stroke.IsEnabled ? stroke : null,
        Shadow = effects.Shadow is { } shadow && shadow.IsEnabled ? shadow : null,
        ColorOverlay = effects.ColorOverlay is { } overlay && overlay.IsEnabled ? overlay : null,
        InnerShadow = effects.InnerShadow is { } innerShadow && innerShadow.IsEnabled ? innerShadow : null,
        OuterGlow = effects.OuterGlow is { } outerGlow && outerGlow.IsEnabled ? outerGlow : null,
        InnerGlow = effects.InnerGlow is { } innerGlow && innerGlow.IsEnabled ? innerGlow : null,
    };

    /// <summary>
    /// How far the effects reach past the layer's edge, one shared margin rather than a sum: an outside
    /// stroke its size, a shadow its distance plus three times its blur, an outer glow three times its size,
    /// and two pixels on top for good measure. Inside effects and the color overlay reach nowhere.
    /// </summary>
    private static int Margin(LayerEffects effects)
    {
        var margin = 0.0;
        if (effects.Stroke is { Inside: false } stroke) margin = Math.Max(margin, stroke.Size);
        if (effects.Shadow is { } shadow) margin = Math.Max(margin, shadow.Distance + shadow.Blur * 3);
        if (effects.OuterGlow is { } glow) margin = Math.Max(margin, glow.Size * 3);
        return (int)Math.Ceiling(margin) + 2;
    }

    /// <summary>Where a shadow falls, in layer pixels, y growing downward.</summary>
    private static (float X, float Y) Offset(double angle, double distance)
    {
        var radians = angle * Math.PI / 180;
        return ((float)(-Math.Cos(radians) * distance), (float)(Math.Sin(radians) * distance));
    }

    /// <summary>
    /// A stroke's ring: the shape grown (or shrunk) by the stroke's size, less the shape itself. The reach is
    /// square, not round — a round one eats into the corners of a rectangle and reads as a wobbly edge.
    /// </summary>
    private static void FillRing(byte[] canvas, float[] shape, float[] work, float[] temp,
                                 int width, int height, StrokeEffect stroke)
    {
        var reach = Math.Max(1, (int)Math.Round(stroke.Size, MidpointRounding.AwayFromZero));
        Extreme(shape, work, temp, width, height, reach, smallest: stroke.Inside);
        for (var i = 0; i < shape.Length; i++)
            work[i] = stroke.Inside ? Math.Max(0f, shape[i] - work[i]) : Math.Max(0f, work[i] - shape[i]);
        Fill(canvas, work, stroke.Red, stroke.Green, stroke.Blue, stroke.Opacity);
    }

    /// <summary>Paints an effect's color through its coverage at its opacity, source-over.</summary>
    private static void Fill(byte[] canvas, float[] coverage, double red, double green, double blue, double opacity)
    {
        var r = (float)red;
        var g = (float)green;
        var b = (float)blue;
        var strength = (float)opacity;
        for (var i = 0; i < coverage.Length; i++)
        {
            var alpha = strength * Math.Clamp(coverage[i], 0f, 1f);
            if (!(alpha > 0)) continue;
            var under = 1 - alpha;
            var at = i * 4;
            canvas[at] = ToByte(r * alpha + canvas[at] / 255f * under);
            canvas[at + 1] = ToByte(g * alpha + canvas[at + 1] / 255f * under);
            canvas[at + 2] = ToByte(b * alpha + canvas[at + 2] / 255f * under);
            canvas[at + 3] = ToByte(alpha + canvas[at + 3] / 255f * under);
        }
    }

    /// <summary>The layer's own pixels drawn over what the effects behind it have laid down so far.</summary>
    private static void Over(byte[] canvas, ReadOnlySpan<byte> source, int stride, int sizeX, int sizeY,
                             int inset, int canvasWidth)
    {
        for (var y = 0; y < sizeY; y++)
        {
            var from = y * stride;
            var to = ((y + inset) * canvasWidth + inset) * 4;
            for (var x = 0; x < sizeX; x++, from += 4, to += 4)
            {
                var alpha = source[from + 3];
                if (alpha == 0) continue;
                if (alpha == 255)
                {
                    source.Slice(from, 4).CopyTo(canvas.AsSpan(to, 4));
                    continue;
                }
                var under = 1 - alpha / 255f;
                canvas[to] = ToByte(source[from] / 255f + canvas[to] / 255f * under);
                canvas[to + 1] = ToByte(source[from + 1] / 255f + canvas[to + 1] / 255f * under);
                canvas[to + 2] = ToByte(source[from + 2] / 255f + canvas[to + 2] / 255f * under);
                canvas[to + 3] = ToByte(alpha / 255f + canvas[to + 3] / 255f * under);
            }
        }
    }

    /// <summary>
    /// The shape moved by a shadow's offset, sampled between pixels so a shadow slides rather than jumping in
    /// whole steps; past the canvas there is nothing. The offset is a float so that the hair of a right angle
    /// (cos 90° is 6e-17, not 0) is lost in the rounding instead of clamping the last row and column away.
    /// </summary>
    private static void Shift(float[] source, float[] target, int width, int height, float dx, float dy)
    {
        for (var y = 0; y < height; y++)
        {
            var sy = y - dy;
            for (var x = 0; x < width; x++)
            {
                var sx = x - dx;
                if (sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1)
                {
                    target[y * width + x] = 0;
                    continue;
                }
                var x0 = (int)MathF.Floor(sx);
                var y0 = (int)MathF.Floor(sy);
                var x1 = Math.Min(x0 + 1, width - 1);
                var y1 = Math.Min(y0 + 1, height - 1);
                var fx = sx - x0;
                var fy = sy - y0;
                var top = source[y0 * width + x0] + (source[y0 * width + x1] - source[y0 * width + x0]) * fx;
                var bottom = source[y1 * width + x0] + (source[y1 * width + x1] - source[y1 * width + x0]) * fx;
                target[y * width + x] = top + (bottom - top) * fy;
            }
        }
    }

    /// <summary>
    /// A separable Gaussian, <paramref name="blur"/> being the setting as written: the Mac build hands it to
    /// Core Image as a sigma of half that. Three sigma wide, normalized, and clamped at the edges, which is
    /// what <c>clampedToExtent</c> gives it. A sigma under a hundredth is no blur at all, as on the Mac.
    /// Writing the rows to <paramref name="temp"/> first lets source and target be the same plane.
    /// </summary>
    private static void Blur(float[] source, float[] target, float[] temp, int width, int height, double blur)
    {
        var sigma = (float)(blur / 2);
        if (!(sigma > 0.01f))
        {
            source.AsSpan(0, width * height).CopyTo(target);
            return;
        }
        var weights = Weights(sigma, out var half);
        for (var y = 0; y < height; y++)
        {
            var line = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                if (x >= half && x + half < width)
                {
                    for (var k = -half; k <= half; k++) sum += weights[k + half] * source[line + x + k];
                }
                else
                {
                    for (var k = -half; k <= half; k++)
                        sum += weights[k + half] * source[line + Math.Clamp(x + k, 0, width - 1)];
                }
                temp[line + x] = sum;
            }
        }
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var sum = 0f;
                if (y >= half && y + half < height)
                {
                    var index = (y - half) * width + x;
                    for (var k = -half; k <= half; k++, index += width) sum += weights[k + half] * temp[index];
                }
                else
                {
                    for (var k = -half; k <= half; k++)
                        sum += weights[k + half] * temp[Math.Clamp(y + k, 0, height - 1) * width + x];
                }
                target[y * width + x] = sum;
            }
        }
    }

    private static float[] Weights(float sigma, out int half)
    {
        half = Math.Max(1, (int)MathF.Round(3 * sigma, MidpointRounding.AwayFromZero));
        var weights = new float[half * 2 + 1];
        var total = 0f;
        for (var k = -half; k <= half; k++)
        {
            var weight = MathF.Exp(-(k * k) / (2 * sigma * sigma));
            weights[k + half] = weight;
            total += weight;
        }
        for (var i = 0; i < weights.Length; i++) weights[i] /= total;
        return weights;
    }

    /// <summary>
    /// The largest (or smallest) value within <paramref name="reach"/> on each side of every pixel, a row
    /// pass then a column pass, so the cost does not grow with the reach. Past an edge there is nothing, which
    /// counts as zero: a dilation stops at the canvas and an erosion bites in from it.
    /// </summary>
    private static void Extreme(float[] source, float[] target, float[] pass, int width, int height,
                                int reach, bool smallest)
    {
        var queue = new int[Math.Max(width, height)];
        Sweep(source, pass, reach, smallest, queue, lines: height, count: width, lineStep: width, elementStep: 1);
        Sweep(pass, target, reach, smallest, queue, lines: width, count: height, lineStep: 1, elementStep: width);
    }

    /// <summary>
    /// One pass of a sliding-window extreme, a monotonic deque of the indices whose values are still
    /// candidates. Each index enters and leaves the deque at most once, so a wide reach costs no more than a
    /// narrow one — Core Image's own morphology filters stall on a wide stroke.
    /// </summary>
    private static void Sweep(float[] input, float[] output, int reach, bool smallest, int[] queue,
                              int lines, int count, int lineStep, int elementStep)
    {
        var radius = Math.Max(0, reach);
        for (var line = 0; line < lines; line++)
        {
            var start = line * lineStep;
            int head = 0, tail = 0, next = 0;
            for (var center = 0; center < count; center++)
            {
                var limit = Math.Min(count - 1, center + radius);
                while (next <= limit)
                {
                    var value = input[start + next * elementStep];
                    while (tail > head)
                    {
                        var previous = input[start + queue[tail - 1] * elementStep];
                        if (smallest ? previous < value : previous > value) break;
                        tail--;
                    }
                    queue[tail] = next;
                    tail++;
                    next++;
                }
                while (head < tail && queue[head] < center - radius) head++;
                var outside = center < radius || center + radius >= count;
                output[start + center * elementStep] =
                    smallest && outside ? 0f : input[start + queue[head] * elementStep];
            }
        }
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp(MathF.Round(value * 255f, MidpointRounding.AwayFromZero), 0f, 255f);
}
