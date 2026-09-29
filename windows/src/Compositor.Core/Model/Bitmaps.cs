using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>Pixel formats and the small copies the panel draws.</summary>
public static class Bitmaps
{
    /// <summary>
    /// The format a layer's PNG holds: straight (unpremultiplied) sRGB, so a decode and re-encode keeps
    /// every byte. PNG stores straight alpha, so asking for anything else would shift color under it.
    /// </summary>
    public static SKImageInfo ColorInfo(int width, int height) =>
        new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());

    /// <summary>A mask is 8-bit grayscale without alpha: white reveals, black hides.</summary>
    public static SKImageInfo MaskInfo(int width, int height) =>
        new(width, height, SKColorType.Gray8, SKAlphaType.Opaque);

    public static SKBitmap Allocate(SKImageInfo info)
    {
        var bitmap = new SKBitmap(info);
        if (!bitmap.ReadyToDraw) throw new InvalidOperationException($"Could not allocate a {info.Width}x{info.Height} bitmap.");
        return bitmap;
    }

    /// <summary>A mask image: 8-bit grayscale, no alpha, and not itself a mask (Skia has no such flag).</summary>
    public static bool IsValidMask(SKBitmap image) =>
        !image.IsNull && image.ColorType == SKColorType.Gray8 && image.AlphaType == SKAlphaType.Opaque
        && image.Width > 0 && image.Height > 0;

    public static SKBitmap Thumbnail(SKBitmap source)
    {
        var (width, height) = ImportedImage.ThumbnailSize(source.Width, source.Height);
        return Scale(source, width, height);
    }

    public static SKBitmap Scale(SKBitmap source, int width, int height)
    {
        if (width == source.Width && height == source.Height)
        {
            var copy = new SKBitmap(source.Info);
            source.CopyTo(copy);
            return copy;
        }
        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType, source.ColorSpace);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? throw new InvalidOperationException("Could not scale a bitmap.");
    }

    /// <summary>A uniform 1x1 mask, so painting decides when to allocate full-resolution pixels.</summary>
    public static SKBitmap SolidMask(bool revealing)
    {
        var bitmap = Allocate(MaskInfo(1, 1));
        // The mask has one channel, and Skia takes a colour's luminance for it: a red-only colour would
        // come out at 54, not white, so an all-reveal mask would hide four fifths of the layer.
        var value = revealing ? (byte)255 : (byte)0;
        bitmap.Erase(new SKColor(value, value, value));
        return bitmap;
    }

    /// <summary>A gray copy of `source` at `width`x`height`, white where the source's alpha is.</summary>
    public static SKBitmap DrawAsMask(SKBitmap source, int width, int height)
    {
        using var surface = SKSurface.Create(MaskInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Src,
            ColorFilter = SKColorFilter.CreateColorMatrix(AlphaToGray),
        };
        var destination = SKRect.Create(0, 0, width, height);
        canvas.DrawBitmap(source, destination, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Flush();
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static readonly float[] AlphaToGray =
    [
        0, 0, 0, 1, 0,
        0, 0, 0, 1, 0,
        0, 0, 0, 1, 0,
        0, 0, 0, 1, 0,
    ];
}
