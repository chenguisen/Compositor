namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/LensPixels.c</c>.</summary>
public static class LensPixels
{
    /// <summary>
    /// Radial lens distortion over premultiplied RGBA (same layout for source and destination). Each
    /// destination pixel samples the source bilinearly at its offset from the image center scaled by
    /// (1 - k * r²), where r is that offset relative to the half-diagonal: k &gt; 0 pulls samples inward
    /// (straightens barrel distortion, corners crop), k &lt; 0 pushes them outward (corners turn
    /// transparent). Pixels outside the source are transparent. k = 0 copies the source exactly.
    /// </summary>
    public static void LensDistort(ReadOnlySpan<byte> source, Span<byte> destination,
                                   int width, int height, int stride, double k)
    {
        double cx = width * 0.5, cy = height * 0.5;
        double halfDiagonal2 = cx * cx + cy * cy;
        for (int y = 0; y < height; ++y)
        {
            double dy = y + 0.5 - cy;
            int destinationRow = y * stride;
            for (int x = 0; x < width; ++x)
            {
                double dx = x + 0.5 - cx;
                double scale = 1.0 - k * (dx * dx + dy * dy) / halfDiagonal2;
                // Source position in pixel-center coordinates.
                double sx = cx + dx * scale - 0.5, sy = cy + dy * scale - 0.5;
                double fx0 = Math.Floor(sx), fy0 = Math.Floor(sy);
                double fx = sx - fx0, fy = sy - fy0;
                int x0 = (int)fx0, y0 = (int)fy0;
                double sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
                for (int j = 0; j < 2; ++j)
                {
                    int row = y0 + j;
                    if (row < 0 || row >= height) continue;
                    double wy = j != 0 ? fy : 1 - fy;
                    if (wy == 0) continue;
                    int line = row * stride;
                    for (int i = 0; i < 2; ++i)
                    {
                        int column = x0 + i;
                        if (column < 0 || column >= width) continue;
                        double weight = wy * (i != 0 ? fx : 1 - fx);
                        if (weight == 0) continue;
                        int p = line + column * 4;
                        sum0 += weight * source[p];
                        sum1 += weight * source[p + 1];
                        sum2 += weight * source[p + 2];
                        sum3 += weight * source[p + 3];
                    }
                }
                int o = destinationRow + x * 4;
                destination[o] = (byte)Math.Round(sum0, MidpointRounding.AwayFromZero);
                destination[o + 1] = (byte)Math.Round(sum1, MidpointRounding.AwayFromZero);
                destination[o + 2] = (byte)Math.Round(sum2, MidpointRounding.AwayFromZero);
                destination[o + 3] = (byte)Math.Round(sum3, MidpointRounding.AwayFromZero);
            }
        }
    }
}
