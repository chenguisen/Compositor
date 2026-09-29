using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>What trimming looks for at the edges.</summary>
public enum TrimBasedOn
{
    TransparentPixels,
    TopLeftPixelColor,
    BottomRightPixelColor,
}

/// <summary>
/// Which edges to take away, and how far a pixel may differ and still count as border.
/// <para>
/// A class rather than a struct on purpose: <c>new TrimOptions()</c> on a record struct zero-initializes it
/// instead of applying the parameter defaults, which would quietly turn "trim every edge" into "trim none".
/// </para>
/// </summary>
public sealed record TrimOptions(
    TrimBasedOn BasedOn = TrimBasedOn.TransparentPixels,
    bool Top = true,
    bool Bottom = true,
    bool Left = true,
    bool Right = true,
    byte Tolerance = 0)
{
    public bool TrimsAny => Top || Bottom || Left || Right;
}

/// <summary>
/// Trimming the canvas back to what is actually drawn on it. What counts as content is decided from the
/// flattened document, because that is what the eye sees; the crop itself is then the ordinary one, so every
/// layer keeps its own pixels and only moves.
/// </summary>
public static class TrimEdits
{
    /// <summary>The rectangle that would be left after trimming, or null when nothing would be.</summary>
    public static SKRectI? TrimRect(SKBitmap flattened, TrimOptions options)
    {
        var width = flattened.Width;
        var height = flattened.Height;
        if (!options.TrimsAny || width <= 0 || height <= 0) return null;
        var pixels = flattened.GetPixelSpan();
        var stride = width * 4;

        // Both answers are half-open bounds — left, top, right, bottom — with right and bottom excluded.
        var (left, top, right, bottom) = options.BasedOn == TrimBasedOn.TransparentPixels
            ? Transparent(pixels, width, height, stride)
            : Coloured(pixels, width, height, stride, options);
        if (left is null) return null;

        var minX = options.Left ? left.Value : 0;
        var minY = options.Top ? top!.Value : 0;
        var maxX = options.Right ? right!.Value : width;
        var maxY = options.Bottom ? bottom!.Value : height;
        return maxX > minX && maxY > minY ? SKRectI.Create(minX, minY, maxX - minX, maxY - minY) : null;
    }

    /// <summary>Trims the canvas to its content, as one edit. False when it is already as tight as it goes.</summary>
    public static bool Trim(CanvasDocument document, TrimOptions options)
    {
        if (!options.TrimsAny) return false;
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels) return false;
        using var flattened = DocumentRenderer.Render(document);
        if (TrimRect(flattened, options) is not { } rect) return false;
        // Trimming to the whole canvas is the same as not trimming it.
        if (rect.Left == 0 && rect.Top == 0 && rect.Width == document.Width && rect.Height == document.Height) return false;
        return CanvasEdits.Crop(document, rect);
    }

    /// <summary>Where the pixels that are not fully transparent start and end.</summary>
    private static (int? Left, int? Top, int? Right, int? Bottom) Transparent(ReadOnlySpan<byte> pixels,
        int width, int height, int stride)
    {
        Span<int> bounds = stackalloc int[4];
        BrushPixels.AlphaBounds(pixels, width, height, stride, bounds);
        // Nothing but transparency: there is no content to trim the canvas back to.
        return bounds[2] == 0 ? (null, null, null, null) : (bounds[0], bounds[1], bounds[2], bounds[3]);
    }

    /// <summary>
    /// Where the pixels that differ from the sampled corner start and end: each edge gives way while the whole
    /// row or column along it still matches the sample.
    /// </summary>
    private static (int? Left, int? Top, int? Right, int? Bottom) Coloured(ReadOnlySpan<byte> pixels,
        int width, int height, int stride, TrimOptions options)
    {
        var corner = options.BasedOn == TrimBasedOn.TopLeftPixelColor ? 0 : (height - 1) * stride + (width - 1) * 4;
        var sample = pixels.Slice(corner, 4);
        var tolerance = (int)options.Tolerance;

        var left = 0;
        while (left < width && ColumnMatches(pixels, left, width, height, stride, sample, tolerance)) left++;
        var right = width - 1;
        while (right >= left && ColumnMatches(pixels, right, width, height, stride, sample, tolerance)) right--;
        var top = 0;
        while (top < height && RowMatches(pixels, top, width, height, stride, sample, tolerance)) top++;
        var bottom = height - 1;
        while (bottom >= top && RowMatches(pixels, bottom, width, height, stride, sample, tolerance)) bottom--;
        // The whole picture is the sampled colour, so trimming would leave nothing.
        if (left >= width || top >= height || right < left || bottom < top) return (null, null, null, null);

        return (options.Left ? left : 0, options.Top ? top : 0,
            options.Right ? right + 1 : width, options.Bottom ? bottom + 1 : height);
    }

    private static bool ColumnMatches(ReadOnlySpan<byte> pixels, int x, int width, int height, int stride,
        ReadOnlySpan<byte> sample, int tolerance)
    {
        for (var y = 0; y < height; y++)
        {
            if (!Matches(pixels, x, y, stride, sample, tolerance)) return false;
        }
        return true;
    }

    private static bool RowMatches(ReadOnlySpan<byte> pixels, int y, int width, int height, int stride,
        ReadOnlySpan<byte> sample, int tolerance)
    {
        for (var x = 0; x < width; x++)
        {
            if (!Matches(pixels, x, y, stride, sample, tolerance)) return false;
        }
        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> pixels, int x, int y, int stride, ReadOnlySpan<byte> sample,
        int tolerance)
    {
        var offset = y * stride + x * 4;
        for (var channel = 0; channel < 4; channel++)
        {
            if (Math.Abs(pixels[offset + channel] - sample[channel]) > tolerance) return false;
        }
        return true;
    }
}
