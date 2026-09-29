using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO.PSD;

/// <summary>
/// Unpacks Photoshop layer channels (Adobe's 2019 specification, Image Data: compression 0 raw and 1
/// PackBits) and lays them out as the bitmaps the model holds.
/// </summary>
internal static class PsdChannelCoder
{
    /// <summary>One channel's plane, cut to the part of the layer the bitmap will hold.</summary>
    public static byte[] Decode(int compression, int width, int height, ReadOnlySpan<byte> data, bool largeDocument,
        PsdCrop? crop)
    {
        if (width <= 0 || height <= 0) return [];
        if (crop is { } cut)
        {
            if (cut.X < 0 || cut.Y < 0 || cut.Width < 0 || cut.Height < 0
                || cut.X + cut.Width > width || cut.Y + cut.Height > height)
            {
                throw PsdFailure.Truncated;
            }
            if (cut.Width <= 0 || cut.Height <= 0) return [];
        }
        return compression switch
        {
            0 => Raw(width, height, data, crop),
            1 => PackBits(width, height, data, largeDocument, crop),
            _ => throw PsdFailure.Compression,
        };
    }

    private static byte[] Raw(int width, int height, ReadOnlySpan<byte> data, PsdCrop? crop)
    {
        var expected = width * height;
        if (data.Length < expected) throw PsdFailure.Truncated;
        var cut = crop ?? new PsdCrop(0, 0, width, height);
        var plane = new byte[cut.Width * cut.Height];
        for (var row = 0; row < cut.Height; row++)
        {
            var source = (cut.Y + row) * width + cut.X;
            data.Slice(source, cut.Width).CopyTo(plane.AsSpan(row * cut.Width));
        }
        return plane;
    }

    private static byte[] PackBits(int width, int height, ReadOnlySpan<byte> data, bool largeDocument, PsdCrop? crop)
    {
        var offset = 0;
        var counts = RowCounts(data, ref offset, height, largeDocument);
        return PackRows(width, height, data, counts, ref offset, crop);
    }

    /// <summary>The byte count of each row, which one PackBits block shares across all its channels.</summary>
    public static int[] RowCounts(ReadOnlySpan<byte> data, ref int offset, int rows, bool largeDocument)
    {
        var counts = new int[rows];
        for (var row = 0; row < rows; row++)
        {
            counts[row] = largeDocument ? Count32(data, ref offset) : Count16(data, ref offset);
        }
        return counts;
    }

    /// <summary>
    /// One plane of rows already counted, from where the previous plane stopped. A merged image shares one
    /// count table across its channels, so the plane says where its rows begin in it.
    /// </summary>
    public static byte[] PackRows(int width, int height, ReadOnlySpan<byte> data, int[] counts, ref int offset,
        PsdCrop? crop, int firstRow = 0)
    {
        var cut = crop ?? new PsdCrop(0, 0, width, height);
        var plane = new byte[cut.Width * cut.Height];
        for (var row = 0; row < height; row++)
        {
            var end = offset + counts[firstRow + row];
            if (end > data.Length) throw PsdFailure.Truncated;
            if (row >= cut.Y && row < cut.Y + cut.Height)
            {
                UnpackRow(data, ref offset, end, width, plane.AsSpan((row - cut.Y) * cut.Width, cut.Width), cut.X);
            }
            // Each row starts where its own byte count says, so a row may be padded without losing the next.
            offset = end;
        }
        return plane;
    }

    /// <summary>One PackBits row: runs of the width given, of which the crop's columns are kept.</summary>
    private static void UnpackRow(ReadOnlySpan<byte> data, ref int offset, int end, int width, Span<byte> destination,
        int startX)
    {
        var written = 0;
        while (written < width)
        {
            if (offset >= end) throw PsdFailure.Truncated;
            var count = (sbyte)data[offset++];
            if (count >= 0)
            {
                var run = count + 1;
                if (written + run > width || offset + run > end) throw PsdFailure.Truncated;
                Keep(destination, startX, written, data.Slice(offset, run));
                offset += run;
                written += run;
            }
            else if (count != -128)
            {
                // -128 is a no-op the Macintosh routine emits; every other negative run repeats the next byte.
                var run = 1 - count;
                if (written + run > width || offset >= end) throw PsdFailure.Truncated;
                var value = data[offset++];
                var from = Math.Max(written, startX);
                var to = Math.Min(written + run, startX + destination.Length);
                if (to > from) destination.Slice(from - startX, to - from).Fill(value);
                written += run;
            }
        }
    }

    private static void Keep(Span<byte> destination, int startX, int written, ReadOnlySpan<byte> source)
    {
        var from = Math.Max(written, startX);
        var to = Math.Min(written + source.Length, startX + destination.Length);
        if (to > from) source.Slice(from - written, to - from).CopyTo(destination.Slice(from - startX));
    }

    private static int Count16(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 2 > data.Length) throw PsdFailure.Truncated;
        var count = data[offset] << 8 | data[offset + 1];
        offset += 2;
        return count;
    }

    private static int Count32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 4 > data.Length) throw PsdFailure.Truncated;
        var count = (int)((uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3]);
        offset += 4;
        return count;
    }

    /// <summary>A layer's pixels: straight (unpremultiplied) sRGB, top row first.</summary>
    public static SKBitmap Rgba(int width, int height, byte[]? red, byte[]? green, byte[]? blue, byte[]? alpha)
    {
        var bitmap = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        var pixels = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
        {
            var source = y * width;
            var destination = y * stride;
            for (var x = 0; x < width; x++)
            {
                var at = destination + x * 4;
                pixels[at] = red is null ? (byte)0 : red[source + x];
                pixels[at + 1] = green is null ? (byte)0 : green[source + x];
                pixels[at + 2] = blue is null ? (byte)0 : blue[source + x];
                // A layer with no transparency channel is opaque.
                pixels[at + 3] = alpha is null ? (byte)255 : alpha[source + x];
            }
        }
        return bitmap;
    }

    /// <summary>An 8-bit grayscale mask: white reveals, black hides.</summary>
    public static SKBitmap Gray(int width, int height, byte[] gray)
    {
        var bitmap = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
        var pixels = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
        {
            gray.AsSpan(y * width, width).CopyTo(pixels.Slice(y * stride, width));
        }
        return bitmap;
    }
}

/// <summary>The errors reading a Photoshop file reports, as the rest of the importer does.</summary>
internal static class PsdFailure
{
    public static ImportException Truncated => new(ImportError.Unreadable);

    public static ImportException Version => new(ImportError.Unsupported,
        "This Photoshop file uses a format version Compositor can't read.");

    public static ImportException ColorMode => new(ImportError.Unsupported,
        "Only 8-bit RGB Photoshop files can be imported.");

    public static ImportException Compression => new(ImportError.Unsupported,
        "This Photoshop file uses a layer compression method that isn't supported.");

    public static ImportException TooLarge => new(ImportError.TooLarge);
}
