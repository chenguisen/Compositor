namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/BrushPixels.c</c>: alpha masks and layer byte helpers.</summary>
public static class BrushPixels
{
    /// <summary>
    /// Half-open bounds of nonzero alpha in premultiplied RGBA, written to <paramref name="bounds"/> as
    /// x0, y0, x1, y1. Empty input writes all zeros.
    /// </summary>
    public static void AlphaBounds(ReadOnlySpan<byte> bytes, int width, int height, int stride, Span<int> bounds)
    {
        int left = width, right = 0, top = height, bottom = 0;
        for (int y = 0; y < height; ++y)
        {
            ReadOnlySpan<byte> row = bytes.Slice(y * stride);
            int first = 0;
            while (first < width && row[first * 4 + 3] == 0) ++first;
            if (first == width) continue;
            int last = width;
            while (last > first && row[(last - 1) * 4 + 3] == 0) --last;
            if (first < left) left = first;
            if (last > right) right = last;
            if (y < top) top = y;
            bottom = y + 1;
        }
        bounds[0] = right != 0 ? left : 0;
        bounds[1] = right != 0 ? top : 0;
        bounds[2] = right;
        bounds[3] = bottom;
    }

    /// <summary>Copies the alpha channel of premultiplied RGBA into a single-channel gray bitmap.</summary>
    public static void ExtractAlpha(ReadOnlySpan<byte> rgba, int rgbaStride, Span<byte> gray, int grayStride,
                                    int width, int height)
    {
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
                gray[y * grayStride + x] = rgba[y * rgbaStride + x * 4 + 3];
    }

    /// <summary>Divides each color channel by alpha and sets alpha to 255, for a fully opaque layer.</summary>
    public static void UnpremultiplyOpaque(Span<byte> rgba, int stride, int width, int height)
    {
        for (int y = 0; y < height; ++y)
        {
            int offset = y * stride;
            for (int x = 0; x < width; ++x, offset += 4)
            {
                uint a = rgba[offset + 3];
                for (int c = 0; c < 3; ++c)
                {
                    uint v = a != 0 ? (rgba[offset + c] * 255u + a / 2) / a : 0u;
                    rgba[offset + c] = (byte)(v > 255 ? 255 : v);
                }
                rgba[offset + 3] = 255;
            }
        }
    }

    /// <summary>Multiplies each color channel by a gray coverage bitmap and writes it back as alpha.</summary>
    public static void RestoreAlpha(Span<byte> rgba, int stride, ReadOnlySpan<byte> alpha, int alphaStride,
                                    int width, int height)
    {
        for (int y = 0; y < height; ++y)
        {
            int offset = y * stride;
            for (int x = 0; x < width; ++x, offset += 4)
            {
                uint a = alpha[y * alphaStride + x];
                for (int c = 0; c < 3; ++c) rgba[offset + c] = (byte)((rgba[offset + c] * a + 127) / 255);
                rgba[offset + 3] = (byte)a;
            }
        }
    }
}
