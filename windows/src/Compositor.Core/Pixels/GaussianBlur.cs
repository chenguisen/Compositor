namespace Compositor.Core.Pixels;

/// <summary>
/// A separable Gaussian over a raster, with the pixels off the edge read as the ones at the edge — Core
/// Image's <c>clampedToExtent</c> — so a blurred shape does not fade where it meets the border of the
/// buffer it is in.
/// </summary>
public static class GaussianBlur
{
    /// <summary>
    /// Blurs <paramref name="bytes"/> in place: <paramref name="channels"/> bytes a pixel, one row every
    /// <paramref name="stride"/> bytes. Premultiplied is what a render is held in, and blurring it there
    /// keeps a transparent surround from pulling its colour into the picture.
    /// </summary>
    public static void Clamped(Span<byte> bytes, int width, int height, int channels, int stride, double sigma)
    {
        if (sigma <= 0 || width <= 0 || height <= 0) return;
        var weights = Weights((float)sigma, out var half);
        float[] plane;
        float[] pass;
        try
        {
            plane = new float[width * height * channels];
            pass = new float[plane.Length];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    plane[(y * width + x) * channels + channel] = bytes[y * stride + x * channels + channel];
                }
            }
        }
        for (var channel = 0; channel < channels; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    float total = 0;
                    for (var tap = -half; tap <= half; tap++)
                    {
                        var column = Math.Clamp(x + tap, 0, width - 1);
                        total += weights[tap + half] * plane[(y * width + column) * channels + channel];
                    }
                    pass[(y * width + x) * channels + channel] = total;
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    float total = 0;
                    for (var tap = -half; tap <= half; tap++)
                    {
                        var row = Math.Clamp(y + tap, 0, height - 1);
                        total += weights[tap + half] * pass[(row * width + x) * channels + channel];
                    }
                    bytes[y * stride + x * channels + channel] =
                        (byte)Math.Clamp(Math.Round(total), 0, 255);
                }
            }
        }
    }

    /// <summary>A normalized Gaussian, three standard deviations wide, one tap per pixel.</summary>
    private static float[] Weights(float sigma, out int half)
    {
        half = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var weights = new float[half * 2 + 1];
        float total = 0;
        for (var tap = -half; tap <= half; tap++)
        {
            var weight = MathF.Exp(-(float)(tap * tap) / (2 * sigma * sigma));
            weights[tap + half] = weight;
            total += weight;
        }
        for (var index = 0; index < weights.Length; index++) weights[index] /= total;
        return weights;
    }
}
