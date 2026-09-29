namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/HealPixels.c</c>: spot healing.</summary>
public static class HealPixels
{
    private const byte Outside = 0, Ring = 1, Hole = 2;

    // West, east, north, south — the order the C code visits a pixel's four neighbors in.
    private static readonly int[] NeighborDx = { -1, 1, 0, 0 };
    private static readonly int[] NeighborDy = { 0, 0, -1, 1 };

    private static readonly double[] SourceFactors = { 1.05, 1.35, 1.75, 2.25, 2.8 };

    /// <summary>
    /// Half-open bounds of nonzero bytes in a gray bitmap, written to <paramref name="bounds"/> as
    /// x0, y0, x1, y1; all zero when empty.
    /// </summary>
    public static void CoverageBounds(ReadOnlySpan<byte> gray, int width, int height, int stride, Span<int> bounds)
    {
        int x0 = width, y0 = height, x1 = 0, y1 = 0;
        for (int y = 0; y < height; ++y)
        {
            ReadOnlySpan<byte> row = gray.Slice(y * stride);
            for (int x = 0; x < width; ++x)
            {
                if (row[x] == 0) continue;
                if (x < x0) x0 = x;
                if (x + 1 > x1) x1 = x + 1;
                if (y < y0) y0 = y;
                if (y + 1 > y1) y1 = y + 1;
            }
        }
        if (x1 <= x0 || y1 <= y0) x0 = y0 = x1 = y1 = 0;
        bounds[0] = x0;
        bounds[1] = y0;
        bounds[2] = x1;
        bounds[3] = y1;
    }

    private static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16; x *= 0x7feb352dU;
            x ^= x >> 15; x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }
    }

    private static double Unit(uint key) => (Hash(key) >> 8) / 16777216.0;

    /// <summary>
    /// Mean squared difference between the ring around the spot and the ring around the patch offset by
    /// (dx, dy). Infinite when the patch would overlap the spot or leave the image.
    /// </summary>
    private static double Score(ReadOnlySpan<byte> rgba, int stride, ReadOnlySpan<byte> role,
                                int wx0, int wy0, int ww, int wh, int dx, int dy, int width, int height)
    {
        if (Math.Abs(dx) < ww && Math.Abs(dy) < wh) return double.PositiveInfinity;
        if (wx0 + dx < 0 || wy0 + dy < 0 || wx0 + ww + dx > width || wy0 + wh + dy > height)
            return double.PositiveInfinity;
        double sum = 0;
        int n = 0;
        for (int y = 0; y < wh; ++y)
        {
            for (int x = 0; x < ww; ++x)
            {
                if (role[y * ww + x] != Ring) continue;
                int t = (wy0 + y) * stride + (wx0 + x) * 4;
                int s = (wy0 + y + dy) * stride + (wx0 + x + dx) * 4;
                for (int c = 0; c < 4; ++c)
                {
                    double d = rgba[t + c] - rgba[s + c];
                    sum += d * d;
                }
                ++n;
            }
        }
        return n != 0 ? sum / n : double.PositiveInfinity;
    }

    /// <summary>
    /// Solves for smooth values over hole pixels, fixed to the ring values around them. A coarser copy is
    /// solved first and used as the starting point, so large spots settle in few passes.
    /// </summary>
    private static void Solve(float[] value, byte[] role, int w, int h, int depth)
    {
        int iterations = 300;
        if (w > 32 && h > 32 && depth < 16)
        {
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            float[]? coarse = null;
            byte[]? coarseRole = null;
            try
            {
                coarse = new float[cw * ch * 4];
                coarseRole = new byte[cw * ch];
            }
            catch (OutOfMemoryException)
            {
                coarse = null;
                coarseRole = null;
            }
            if (coarse != null && coarseRole != null)
            {
                var knownSum = new float[4];
                var holeSum = new float[4];
                for (int y = 0; y < ch; ++y)
                {
                    for (int x = 0; x < cw; ++x)
                    {
                        int known = 0, hole = 0;
                        Array.Clear(knownSum);
                        Array.Clear(holeSum);
                        for (int j = 0; j < 2; ++j)
                        {
                            for (int i = 0; i < 2; ++i)
                            {
                                int fx = x * 2 + i, fy = y * 2 + j;
                                if (fx >= w || fy >= h) continue;
                                int p = fy * w + fx;
                                if (role[p] == Ring)
                                {
                                    ++known;
                                    for (int c = 0; c < 4; ++c) knownSum[c] += value[p * 4 + c];
                                }
                                else if (role[p] == Hole)
                                {
                                    ++hole;
                                    for (int c = 0; c < 4; ++c) holeSum[c] += value[p * 4 + c];
                                }
                            }
                        }
                        int q = y * cw + x;
                        if (known != 0)
                        {
                            coarseRole[q] = Ring;
                            for (int c = 0; c < 4; ++c) coarse[q * 4 + c] = knownSum[c] / known;
                        }
                        else if (hole != 0)
                        {
                            coarseRole[q] = Hole;
                            for (int c = 0; c < 4; ++c) coarse[q * 4 + c] = holeSum[c] / hole;
                        }
                    }
                }
                Solve(coarse, coarseRole, cw, ch, depth + 1);
                for (int y = 0; y < h; ++y)
                    for (int x = 0; x < w; ++x)
                    {
                        int p = y * w + x, q = y / 2 * cw + x / 2;
                        if (role[p] == Hole && coarseRole[q] == Hole)
                            Array.Copy(coarse, q * 4, value, p * 4, 4);
                    }
                iterations = 40;
            }
        }
        const float omega = 1.8f;
        var sum = new float[4];
        for (int it = 0; it < iterations; ++it)
        {
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    int p = y * w + x;
                    if (role[p] != Hole) continue;
                    Array.Clear(sum);
                    int n = 0;
                    for (int k = 0; k < 4; ++k)
                    {
                        int cx = x + NeighborDx[k], cy = y + NeighborDy[k];
                        if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue;
                        int q = cy * w + cx;
                        if (role[q] == Outside) continue;
                        for (int c = 0; c < 4; ++c) sum[c] += value[q * 4 + c];
                        ++n;
                    }
                    if (n == 0) continue;
                    for (int c = 0; c < 4; ++c)
                        value[p * 4 + c] += omega * (sum[c] / n - value[p * 4 + c]);
                }
            }
        }
    }

    /// <summary>
    /// Spot healing, in place, over premultiplied RGBA. <paramref name="coverage"/> (width * height bytes,
    /// 0–255) marks what to heal. Mode 0 (Content-Aware) copies texture from the nearby patch whose
    /// surrounding ring of pixels best matches the ring around the spot; mode 1 (Create Texture) fills
    /// smoothly from the spot's edges and adds grain matching the detail around it; mode 2 (Proximity
    /// Match) is like 0 but takes the closest good patch. Copied texture is blended so it meets the
    /// surrounding tone exactly (a membrane fill), and the result replaces the original by
    /// coverage × opacity. Returns 0 on success, or -1 when memory runs out.
    /// </summary>
    public static int SpotHeal(Span<byte> rgba, ReadOnlySpan<byte> coverage, int width, int height, int stride,
                               float opacity, int mode, uint seed)
    {
        Span<int> bounds = stackalloc int[4];
        CoverageBounds(coverage, width, height, width, bounds);
        if (bounds[2] <= bounds[0]) return 0;
        int bw = bounds[2] - bounds[0], bh = bounds[3] - bounds[1];
        int size = bw > bh ? bw : bh;
        int ring = size / 8;
        if (ring < 2) ring = 2;
        if (ring > 16) ring = 16;
        // Work box: the spot plus its ring, clipped to the image.
        int wx0 = bounds[0] - ring < 0 ? 0 : bounds[0] - ring;
        int wy0 = bounds[1] - ring < 0 ? 0 : bounds[1] - ring;
        int wx1 = bounds[2] + ring > width ? width : bounds[2] + ring;
        int wy1 = bounds[3] + ring > height ? height : bounds[3] + ring;
        int ww = wx1 - wx0, wh = wy1 - wy0, wn = ww * wh;

        byte[] role;
        byte[] near;
        int[] prefix;
        float[] value;
        try
        {
            role = new byte[wn];
            near = new byte[wn];
            prefix = new int[(ww > wh ? ww : wh) + 1];
            value = new float[wn * 4];
        }
        catch (OutOfMemoryException)
        {
            return -1;
        }
        for (int y = 0; y < wh; ++y)
            for (int x = 0; x < ww; ++x)
                role[y * ww + x] = coverage[(wy0 + y) * width + (wx0 + x)] != 0 ? Hole : Outside;
        // The ring: pixels within `ring` of the spot (a square dilation, row pass then column pass).
        for (int y = 0; y < wh; ++y)
        {
            prefix[0] = 0;
            for (int x = 0; x < ww; ++x) prefix[x + 1] = prefix[x] + (role[y * ww + x] == Hole ? 1 : 0);
            for (int x = 0; x < ww; ++x)
            {
                int lo = x - ring < 0 ? 0 : x - ring;
                int hi = x + ring + 1 > ww ? ww : x + ring + 1;
                near[y * ww + x] = (byte)(prefix[hi] - prefix[lo] > 0 ? 1 : 0);
            }
        }
        for (int x = 0; x < ww; ++x)
        {
            prefix[0] = 0;
            for (int y = 0; y < wh; ++y) prefix[y + 1] = prefix[y] + (near[y * ww + x] != 0 ? 1 : 0);
            for (int y = 0; y < wh; ++y)
            {
                int lo = y - ring < 0 ? 0 : y - ring;
                int hi = y + ring + 1 > wh ? wh : y + ring + 1;
                if (role[y * ww + x] == Outside && prefix[hi] - prefix[lo] > 0) role[y * ww + x] = Ring;
            }
        }
        int ringCount = 0;
        for (int p = 0; p < wn; ++p) ringCount += role[p] == Ring ? 1 : 0;
        if (ringCount == 0) return 0;

        // Source patch for Content-Aware and Proximity Match.
        int ox = 0, oy = 0;
        bool haveSource = false;
        if (mode != 1)
        {
            int count = mode == 2 ? 2 : 5;
            double best = double.PositiveInfinity;
            for (int f = 0; f < count; ++f)
            {
                for (int a = 0; a < 24; ++a)
                {
                    double angle = a * Math.PI / 12.0;
                    int dx = (int)Math.Round(Math.Cos(angle) * SourceFactors[f] * ww, MidpointRounding.AwayFromZero);
                    int dy = (int)Math.Round(Math.Sin(angle) * SourceFactors[f] * wh, MidpointRounding.AwayFromZero);
                    double score = Score(rgba, stride, role, wx0, wy0, ww, wh, dx, dy, width, height);
                    if (!double.IsFinite(score)) continue;
                    score *= mode == 2 ? 1.0 + 0.6 * f : 1.0 + 0.1 * f; // nearer patches win ties
                    if (score < best)
                    {
                        best = score;
                        ox = dx;
                        oy = dy;
                    }
                }
            }
            if (double.IsFinite(best))
            {
                // Fine-tune the alignment so repeating texture lines up.
                int cx = ox, cy = oy;
                double refined = Score(rgba, stride, role, wx0, wy0, ww, wh, cx, cy, width, height);
                for (int j = -3; j <= 3; ++j)
                    for (int i = -3; i <= 3; ++i)
                    {
                        double score = Score(rgba, stride, role, wx0, wy0, ww, wh, cx + i, cy + j, width, height);
                        if (score < refined)
                        {
                            refined = score;
                            ox = cx + i;
                            oy = cy + j;
                        }
                    }
                haveSource = true;
            }
        }

        // Membrane: the edge difference between the original and the patch (or the original itself
        // for a smooth fill), spread across the spot.
        var mean = new double[4];
        var detail = new double[3];
        for (int y = 0; y < wh; ++y)
        {
            for (int x = 0; x < ww; ++x)
            {
                int p = y * ww + x;
                if (role[p] != Ring)
                {
                    Array.Clear(value, p * 4, 4);
                    continue;
                }
                int ix = wx0 + x, iy = wy0 + y;
                int t = iy * stride + ix * 4;
                int s = haveSource ? (iy + oy) * stride + (ix + ox) * 4 : 0;
                for (int c = 0; c < 4; ++c)
                {
                    value[p * 4 + c] = rgba[t + c] - (haveSource ? rgba[s + c] : 0);
                    mean[c] += value[p * 4 + c];
                }
                if (!haveSource)
                {
                    // Fine detail around the spot: each pixel against the average of its neighbors.
                    for (int c = 0; c < 3; ++c)
                    {
                        double around = 0;
                        int n = 0;
                        for (int k = 0; k < 4; ++k)
                        {
                            int ax = ix + NeighborDx[k], ay = iy + NeighborDy[k];
                            if (ax < 0 || ay < 0 || ax >= width || ay >= height) continue;
                            around += rgba[ay * stride + ax * 4 + c];
                            ++n;
                        }
                        if (n != 0)
                        {
                            double d = rgba[t + c] - around / n;
                            detail[c] += d * d;
                        }
                    }
                }
            }
        }
        for (int c = 0; c < 4; ++c) mean[c] /= ringCount;
        for (int p = 0; p < wn; ++p)
            if (role[p] == Hole)
                for (int c = 0; c < 4; ++c) value[p * 4 + c] = (float)mean[c];
        Solve(value, role, ww, wh, 0);
        for (int c = 0; c < 3; ++c) detail[c] = Math.Sqrt(detail[c] / ringCount) * 0.9;

        var outv = new double[4];
        for (int y = 0; y < wh; ++y)
        {
            for (int x = 0; x < ww; ++x)
            {
                int p = y * ww + x;
                if (role[p] != Hole) continue;
                int ix = wx0 + x, iy = wy0 + y;
                int t = iy * stride + ix * 4;
                int s = haveSource ? (iy + oy) * stride + (ix + ox) * 4 : 0;
                double amount = coverage[iy * width + ix] / 255.0 * opacity;
                double grain = 0;
                if (!haveSource)
                {
                    uint key = Hash(seed ^ Hash((uint)(iy * width + ix)));
                    double u1 = Unit(key), u2 = Unit(key ^ 0x68e31da4U);
                    grain = Math.Sqrt(-2.0 * Math.Log(1.0 - u1)) * Math.Cos(2.0 * Math.PI * u2);
                }
                for (int c = 0; c < 4; ++c)
                {
                    double healed = (haveSource ? rgba[s + c] : 0) + value[p * 4 + c]
                                    + (c < 3 ? grain * detail[c] : 0);
                    outv[c] = rgba[t + c] + (healed - rgba[t + c]) * amount;
                }
                double alpha = outv[3] < 0 ? 0 : outv[3] > 255 ? 255 : outv[3];
                byte alphaByte = (byte)Math.Round(alpha, MidpointRounding.AwayFromZero);
                rgba[t + 3] = alphaByte;
                for (int c = 0; c < 3; ++c)
                {
                    double v = outv[c] < 0 ? 0 : outv[c] > alphaByte ? alphaByte : outv[c];
                    rgba[t + c] = (byte)Math.Round(v, MidpointRounding.AwayFromZero);
                }
            }
        }
        return 0;
    }
}
