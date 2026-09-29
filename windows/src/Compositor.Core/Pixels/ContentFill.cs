namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/ContentFill.c</c>: PatchMatch-style content-aware fill.</summary>
public static class ContentFill
{
    private static uint NextRandom(ref uint state)
    {
        unchecked
        {
            state = state * 1664525u + 1013904223u;
            return state;
        }
    }

    private static double Match(ReadOnlySpan<byte> pixels, int stride, ReadOnlySpan<byte> known,
                                int w, int h, int p, int q, int radius)
    {
        int px = p % w, py = p / w, qx = q % w, qy = q / w, count = 0;
        double sum = 0;
        for (int dy = -radius; dy <= radius; ++dy)
            for (int dx = -radius; dx <= radius; ++dx)
            {
                int x = px + dx, y = py + dy, sx = qx + dx, sy = qy + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || sx < 0 || sy < 0 || sx >= w || sy >= h
                    || known[y * w + x] == 0) continue;
                int a = y * stride + x * 4, b = sy * stride + sx * 4;
                for (int c = 0; c < 4; ++c)
                {
                    int d = pixels[a + c] - pixels[b + c];
                    sum += d * d;
                }
                ++count;
            }
        return count != 0 ? sum / count : double.MaxValue;
    }

    /// <summary>
    /// Fills the pixels selected by <paramref name="mask"/> in place, from the unselected opaque pixels
    /// around them. Selected pixels are filled; unselected opaque pixels are the image to match and copy
    /// from; unselected transparent ones are neither. Returns 1 on success and 0 when no source patch
    /// exists; allocation failure throws <see cref="OutOfMemoryException"/> rather than returning -1.
    /// </summary>
    public static int Fill(Span<byte> pixels, int stride, ReadOnlySpan<byte> mask, int maskStride,
                           int width, int height)
    {
        int n = width * height;
        byte[] known, target, valid, queued;
        int[] donors, queue, chosen;
        try
        {
            known = new byte[n];
            target = new byte[n];
            valid = new byte[n];
            queued = new byte[n];
            donors = new int[n];
            queue = new int[n];
            chosen = new int[n];
        }
        catch (OutOfMemoryException)
        {
            return -1;
        }
        int radius = width >= 5 && height >= 5 ? 2 : 0;
        int missing = 0, donorCount = 0, head = 0, tail = 0, scan = 0;
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                int p = y * width + x;
                target[p] = (byte)(mask[y * maskStride + x] != 0 ? 1 : 0);
                known[p] = (byte)(target[p] == 0 && pixels[y * stride + x * 4 + 3] == 255 ? 1 : 0);
                chosen[p] = -1;
                if (target[p] != 0) ++missing;
            }
        if (missing == 0) return 1;
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                int p = y * width + x;
                if (known[p] == 0) continue;
                bool ok = true;
                for (int dy = -radius; dy <= radius && ok; ++dy)
                    for (int dx = -radius; dx <= radius; ++dx)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= width || sy >= height || known[sy * width + sx] == 0)
                        {
                            ok = false;
                            break;
                        }
                    }
                if (ok)
                {
                    valid[p] = 1;
                    donors[donorCount++] = p;
                }
            }
        if (donorCount == 0) return 0;
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                int p = y * width + x;
                if (target[p] != 0
                    && ((x != 0 && known[p - 1] != 0) || (x + 1 < width && known[p + 1] != 0)
                        || (y != 0 && known[p - width] != 0) || (y + 1 < height && known[p + width] != 0)))
                {
                    queue[tail++] = p;
                    queued[p] = 1;
                }
            }
        uint seed = 0x6d2b79f5;
        Span<int> neighbors = stackalloc int[4];
        for (;;)
        {
            while (head < tail)
            {
                int p = queue[head++], x = p % width, y = p / width, best = -1;
                double score = double.MaxValue;
                neighbors[0] = x != 0 ? p - 1 : -1;
                neighbors[1] = x + 1 < width ? p + 1 : -1;
                neighbors[2] = y != 0 ? p - width : -1;
                neighbors[3] = y + 1 < height ? p + width : -1;
                // Propagate coherent source offsets, then refine with randomized patch search.
                for (int k = 0; k < 28; ++k)
                {
                    int q = -1;
                    if (k < 4)
                    {
                        int t = neighbors[k];
                        if (t >= 0) q = (chosen[t] >= 0 ? chosen[t] : t) + (p - t);
                    }
                    else
                    {
                        q = donors[(int)(NextRandom(ref seed) % (uint)donorCount)];
                    }
                    if (q < 0 || q >= n || valid[q] == 0) continue;
                    double s = Match(pixels, stride, known, width, height, p, q, radius);
                    if (best < 0 || s < score)
                    {
                        score = s;
                        best = q;
                    }
                }
                if (best < 0) best = donors[0];
                for (int r = 64; r >= 1; r /= 2)
                {
                    int qx = best % width + (int)(NextRandom(ref seed) % (uint)(2 * r + 1)) - r;
                    int qy = best / width + (int)(NextRandom(ref seed) % (uint)(2 * r + 1)) - r;
                    if (qx < 0 || qy < 0 || qx >= width || qy >= height || valid[qy * width + qx] == 0) continue;
                    int q = qy * width + qx;
                    double s = Match(pixels, stride, known, width, height, p, q, radius);
                    if (s < score)
                    {
                        score = s;
                        best = q;
                    }
                }
                // The donor is copied into the hole. Span.CopyTo reads the receiver as the source, so the
                // donor has to be the receiver.
                pixels.Slice(best / width * stride + best % width * 4, 4).CopyTo(pixels.Slice(y * stride + x * 4, 4));
                known[p] = 1;
                chosen[p] = best;
                for (int k = 0; k < 4; ++k)
                {
                    int q = neighbors[k];
                    if (q >= 0 && target[q] != 0 && known[q] == 0 && queued[q] == 0)
                    {
                        queued[q] = 1;
                        queue[tail++] = q;
                    }
                }
            }
            // A selected area that only transparency touches starts from the best random donor, then spreads.
            while (scan < n && (target[scan] == 0 || known[scan] != 0)) ++scan;
            if (scan >= n) break;
            queue[tail++] = scan;
            queued[scan] = 1;
        }
        return 1;
    }
}
