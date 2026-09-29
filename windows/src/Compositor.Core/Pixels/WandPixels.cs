namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/WandPixels.c</c>: Magic Wand, Color Range and mask outlining.</summary>
public static class WandPixels
{
    private const int East = 1, South = 2, West = 4, North = 8;

    // Outlines with more pixel edges than this are refused: the path would be too slow to draw.
    private const int EdgeLimit = 8000000;

    private static bool Matches(ReadOnlySpan<byte> p, ReadOnlySpan<int> reference, int tolerance)
    {
        for (int c = 0; c < 4; ++c)
        {
            int d = p[c] - reference[c];
            if (d < -tolerance || d > tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// Magic Wand match over premultiplied RGBA (rows top-down). The reference color is the average over a
    /// (2 * radius + 1)² square around the seed, clipped to the image. A pixel matches when every channel,
    /// alpha included, is within <paramref name="tolerance"/> of it. Contiguous fills 4-connected from the
    /// seed (nothing when the seed itself doesn't match); otherwise every matching pixel. Writes 255 for
    /// selected, 0 elsewhere, into <paramref name="mask"/> (width * height bytes). Returns the number
    /// selected, or -1 when memory runs out.
    /// </summary>
    public static int WandMask(ReadOnlySpan<byte> rgba, int width, int height, int stride,
                               int seedX, int seedY, int radius, int tolerance, bool contiguous, Span<byte> mask)
    {
        if (width == 0 || height == 0) return 0;
        mask.Slice(0, width * height).Clear();
        if (seedX >= width || seedY >= height) return 0;
        int x0 = seedX > radius ? seedX - radius : 0;
        int x1 = seedX + radius < width ? seedX + radius : width - 1;
        int y0 = seedY > radius ? seedY - radius : 0;
        int y1 = seedY + radius < height ? seedY + radius : height - 1;
        Span<ulong> sums = stackalloc ulong[4];
        sums.Clear();
        ulong samples = 0;
        for (int y = y0; y <= y1; ++y)
            for (int x = x0; x <= x1; ++x, ++samples)
                for (int c = 0; c < 4; ++c) sums[c] += rgba[y * stride + x * 4 + c];
        Span<int> reference = stackalloc int[4];
        for (int c = 0; c < 4; ++c) reference[c] = (int)((sums[c] + samples / 2) / samples);

        int count = 0;
        if (!contiguous)
        {
            for (int y = 0; y < height; ++y)
            {
                int row = y * stride;
                int outRow = y * width;
                for (int x = 0; x < width; ++x)
                    if (Matches(rgba.Slice(row + x * 4, 4), reference, tolerance))
                    {
                        mask[outRow + x] = 255;
                        ++count;
                    }
            }
            return count;
        }

        // Scanline flood fill: each popped seed fills its whole horizontal run, then pushes one
        // seed per matching run in the rows directly above and below it.
        int capacity = 4096, top = 1;
        int[] stack;
        try
        {
            stack = new int[capacity * 2];
        }
        catch (OutOfMemoryException)
        {
            return -1;
        }
        stack[0] = seedX;
        stack[1] = seedY;
        while (top != 0)
        {
            --top;
            int x = stack[top * 2], y = stack[top * 2 + 1];
            int row = y * stride;
            int outRow = y * width;
            if (mask[outRow + x] != 0 || !Matches(rgba.Slice(row + x * 4, 4), reference, tolerance)) continue;
            int left = x, right = x;
            while (left > 0 && mask[outRow + left - 1] == 0
                   && Matches(rgba.Slice(row + (left - 1) * 4, 4), reference, tolerance)) --left;
            while (right + 1 < width && mask[outRow + right + 1] == 0
                   && Matches(rgba.Slice(row + (right + 1) * 4, 4), reference, tolerance)) ++right;
            mask.Slice(outRow + left, right - left + 1).Fill(255);
            count += right - left + 1;
            for (int side = 0; side < 2; ++side)
            {
                if (side == 0 ? y == 0 : y + 1 >= height) continue;
                int ny = side == 0 ? y - 1 : y + 1;
                int nearRow = ny * stride;
                int nearOut = ny * width;
                bool inRun = false;
                for (int nx = left; nx <= right; ++nx)
                {
                    bool candidate = mask[nearOut + nx] == 0
                                     && Matches(rgba.Slice(nearRow + nx * 4, 4), reference, tolerance);
                    if (candidate && !inRun)
                    {
                        if (top == capacity)
                        {
                            try
                            {
                                Array.Resize(ref stack, capacity * 4);
                            }
                            catch (OutOfMemoryException)
                            {
                                return -1;
                            }
                            capacity *= 2;
                        }
                        stack[top * 2] = nx;
                        stack[top * 2 + 1] = ny;
                        ++top;
                    }
                    inRun = candidate;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Select &gt; Color Range over premultiplied RGBA. A pixel matches when every color channel is within
    /// <paramref name="fuzziness"/> of one of the <paramref name="includeCount"/> colors in
    /// <paramref name="include"/> and of none in <paramref name="exclude"/> (straight sRGB, 3 bytes each).
    /// Transparent pixels never match. With <paramref name="invert"/>, the pixels that don't match are
    /// selected instead. Writes 255 for selected, 0 elsewhere, into <paramref name="mask"/>, and returns
    /// the number selected.
    /// </summary>
    public static int ColorRangeMask(ReadOnlySpan<byte> rgba, int width, int height, int stride,
                                     ReadOnlySpan<byte> include, int includeCount,
                                     ReadOnlySpan<byte> exclude, int excludeCount,
                                     int fuzziness, bool invert, Span<byte> mask)
    {
        int count = 0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            int outRow = y * width;
            for (int x = 0; x < width; ++x)
            {
                int o = row + x * 4;
                bool matches = false;
                if (rgba[o + 3] != 0)
                {
                    int r = (rgba[o] * 255 + rgba[o + 3] / 2) / rgba[o + 3];
                    int g = (rgba[o + 1] * 255 + rgba[o + 3] / 2) / rgba[o + 3];
                    int b = (rgba[o + 2] * 255 + rgba[o + 3] / 2) / rgba[o + 3];
                    matches = ColorNear(r, g, b, include, includeCount, fuzziness)
                              && !ColorNear(r, g, b, exclude, excludeCount, fuzziness);
                }
                if (invert) matches = !matches;
                mask[outRow + x] = (byte)(matches ? 255 : 0);
                if (matches) ++count;
            }
        }
        return count;
    }

    private static bool ColorNear(int r, int g, int b, ReadOnlySpan<byte> colors, int count, int fuzziness)
    {
        for (int i = 0; i < count; ++i)
        {
            ReadOnlySpan<byte> c = colors.Slice(i * 3, 3);
            if (Math.Abs(r - c[0]) <= fuzziness && Math.Abs(g - c[1]) <= fuzziness && Math.Abs(b - c[2]) <= fuzziness)
                return true;
        }
        return false;
    }

    // Headings, clockwise on screen (y grows downward): east, south, west, north.
    private static int TurnRight(int d) => d == North ? East : d << 1;
    private static int TurnLeft(int d) => d == East ? North : d >> 1;

    /// <summary>
    /// Outline of the nonzero pixels of <paramref name="mask"/>, along pixel edges, as closed loops of
    /// corner points (x, y pairs in pixel-edge coordinates). Outer boundaries run clockwise and holes
    /// counterclockwise in top-left coordinates, so the winding rule fills exactly those pixels.
    /// <paramref name="points"/> receives 2 * <paramref name="pointCount"/> values and <paramref name="loops"/>
    /// each loop's corner count. Returns 0 on success, -1 when memory runs out, and -2 when the outline is
    /// too detailed to be worth drawing.
    /// </summary>
    public static int WandTrace(ReadOnlySpan<byte> mask, int width, int height,
                                out int[] points, out int pointCount, out int[] loops, out int loopCount)
    {
        points = Array.Empty<int>();
        loops = Array.Empty<int>();
        pointCount = 0;
        loopCount = 0;
        if (width == 0 || height == 0) return 0;
        if (width >= int.MaxValue || height >= int.MaxValue) return -1;
        // Each vertex of the (width + 1) × (height + 1) grid records the directed boundary edges
        // leaving it: a selected pixel's unselected sides, walked clockwise around the pixel.
        int stride = width + 1;
        // The C code counts vertices in size_t; here the grid has to fit an int-indexed buffer.
        if ((long)stride * (height + 1) > int.MaxValue) return -1;
        int vertices = stride * (height + 1);
        byte[] outEdges;
        try
        {
            outEdges = new byte[vertices];
        }
        catch (OutOfMemoryException)
        {
            return -1;
        }
        int edges = 0;
        for (int y = 0; y < height; ++y)
        {
            int row = y * width;
            for (int x = 0; x < width; ++x)
            {
                if (mask[row + x] == 0) continue;
                if (y == 0 || mask[(y - 1) * width + x] == 0)
                {
                    outEdges[y * stride + x] |= East;
                    ++edges;
                }
                if (x + 1 == width || mask[row + x + 1] == 0)
                {
                    outEdges[y * stride + x + 1] |= South;
                    ++edges;
                }
                if (y + 1 == height || mask[(y + 1) * width + x] == 0)
                {
                    outEdges[(y + 1) * stride + x + 1] |= West;
                    ++edges;
                }
                if (x == 0 || mask[row + x - 1] == 0)
                {
                    outEdges[(y + 1) * stride + x] |= North;
                    ++edges;
                }
            }
            if (edges > EdgeLimit) return -2;
        }

        var corners = new List<int>(2048);
        var lens = new List<int>(256);
        for (int start = 0; start < vertices; ++start)
        {
            while (outEdges[start] != 0)
            {
                int first = corners.Count / 2, v = start;
                int heading = 0, initial = 0;
                do
                {
                    int bits = outEdges[v];
                    int d;
                    // Where two loops meet at a corner, turning right keeps them apart.
                    if (heading == 0) d = bits & -bits;
                    else if ((bits & TurnRight(heading)) != 0) d = TurnRight(heading);
                    else if ((bits & heading) != 0) d = heading;
                    else if ((bits & TurnLeft(heading)) != 0) d = TurnLeft(heading);
                    else d = bits & -bits;
                    if (d == 0) break;
                    outEdges[v] &= (byte)~d;
                    if (d != heading)
                    {
                        corners.Add(v % stride);
                        corners.Add(v / stride);
                    }
                    if (heading == 0) initial = d;
                    heading = d;
                    v = d == East ? v + 1 : d == West ? v - 1 : d == South ? v + stride : v - stride;
                } while (v != start);
                // The start is a corner unless the loop arrives on the heading it left with.
                if (heading == initial && corners.Count / 2 > first) corners.RemoveRange(first * 2, 2);
                lens.Add(corners.Count / 2 - first);
            }
        }
        try
        {
            points = corners.ToArray();
            loops = lens.ToArray();
        }
        catch (OutOfMemoryException)
        {
            points = Array.Empty<int>();
            loops = Array.Empty<int>();
            return -1;
        }
        pointCount = points.Length / 2;
        loopCount = loops.Length;
        return 0;
    }
}