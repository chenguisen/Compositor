using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO;

/// <summary>The PNG header fields the loader checks before it decodes anything.</summary>
public readonly record struct PngHeader(int Width, int Height, int BitDepth, int ColorType)
{
    /// <summary>CoreGraphics reports the file's depth; anything over 8 bits is rejected.</summary>
    public bool IsEightBitOrLess => BitDepth <= 8;

    /// <summary>Grayscale without alpha: what a mask has to be.</summary>
    public bool IsGrayscaleWithoutAlpha => ColorType == 0;
}

/// <summary>PNG assets inside a package: 8-bit images, and 8-bit grayscale masks.</summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The image's dimensions and format, read from the header. Only one image per file is supported, as
    /// the Swift build requires of an asset.
    /// </summary>
    public static PngHeader ReadHeader(byte[] data)
    {
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new ProjectException(ProjectError.MissingImage);
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            throw new ProjectException(ProjectError.MissingImage);
        }
        var width = ReadBigEndian(data, 16);
        var height = ReadBigEndian(data, 20);
        if (width <= 0 || height <= 0) throw new ProjectException(ProjectError.MissingImage);
        return new PngHeader(width, height, data[24], data[25]);
    }

    /// <summary>A layer's pixels, decoded to straight-alpha sRGB.</summary>
    public static SKBitmap DecodeImage(PngHeader header, byte[] data)
    {
        EnsureSingleImage(data);
        var info = Bitmaps.ColorInfo(header.Width, header.Height);
        return Decode(data, info);
    }

    /// <summary>A mask's pixels, decoded to 8-bit grayscale.</summary>
    public static SKBitmap DecodeMask(PngHeader header, byte[] data)
    {
        if (!header.IsGrayscaleWithoutAlpha) throw new ProjectException(ProjectError.Invalid);
        EnsureSingleImage(data);
        var bitmap = Decode(data, Bitmaps.MaskInfo(header.Width, header.Height));
        if (!Bitmaps.IsValidMask(bitmap))
        {
            bitmap.Dispose();
            throw new ProjectException(ProjectError.Invalid);
        }
        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? throw new ProjectException(ProjectError.Encode);
    }

    private static SKBitmap Decode(byte[] data, SKImageInfo info)
    {
        using var encoded = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encoded) ?? throw new ProjectException(ProjectError.MissingImage);
        if (codec.Info.Width != info.Width || codec.Info.Height != info.Height) throw new ProjectException(ProjectError.MissingImage);
        var bitmap = Bitmaps.Allocate(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new ProjectException(ProjectError.MissingImage);
        }
        return bitmap;
    }

    /// <summary>An animated PNG holds more than one image, which an asset may not be.</summary>
    private static void EnsureSingleImage(byte[] data)
    {
        using var encoded = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encoded) ?? throw new ProjectException(ProjectError.MissingImage);
        if (codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1)
        {
            throw new ProjectException(ProjectError.MissingImage);
        }
    }

    private static int ReadBigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
