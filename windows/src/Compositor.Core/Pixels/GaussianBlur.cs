namespace Compositor.Core.Pixels;

/// <summary>
/// A separable Gaussian over a raster, with the pixels off the edge read as the ones at the edge — Core
/// Image's <c>clampedToExtent</c> — so a blurred shape does not fade where it meets the border of the
/// buffer it is in.
/// <para>
/// A true Gaussian spends a tap on every pixel out to three standard deviations, and its cost is that width
/// times the buffer it runs over. The Blur panel's radius reaches 250, and a filter is given 3 * radius of
/// room on every side, so a 2000 x 1169 picture is blurred as a 3504 x 2673 buffer through a 1501-tap
/// kernel: four channels, two passes, 113 billion multiplies. That measured at four minutes, which is the
/// whole window frozen, since a filter preview runs on the thread that draws it.
/// </para>
/// <para>
/// Anything wider than <see cref="ExactHalf"/> is therefore run as three box passes instead, sized so their
/// combined variance — and so their apparent spread — is the Gaussian's. The same picture then blurs in well
/// under a second. The difference from the exact kernel is under four levels of 255 in the ramp of an edge
/// and under six where a plane's own border fades, which is what <c>GaussianBlurTests</c> holds it to; the
/// shapes of the two are otherwise the same, so nothing downstream — a filter's margin, a feather, a bloom's
/// spread — has to know which of them ran. The boxes reach no further than the exact kernel does, so the
/// room a blur is given is still enough for it. That last part is not a nicety: a piece of a tiled render is
/// blurred with a halo of exactly that room around it, and a kernel reaching past the halo would put a seam
/// down the piece's edge.
/// </para>
/// </summary>
public static class GaussianBlur
{
    /// <summary>
    /// Half the width of the widest kernel run tap by tap: 49 taps, which is a sigma of eight. Below that
    /// the exact kernel costs little — the buffer has grown by only that much — and the brush, a feathered
    /// selection and the small effects all blur through it exactly.
    /// </summary>
    private const int ExactHalf = 24;

    /// <summary>
    /// Blurs <paramref name="bytes"/> in place: <paramref name="channels"/> bytes a pixel, one row every
    /// <paramref name="stride"/> bytes. Premultiplied is what a render is held in, and blurring it there
    /// keeps a transparent surround from pulling its colour into the picture.
    /// </summary>
    public static void Clamped(Span<byte> bytes, int width, int height, int channels, int stride, double sigma) =>
        Bytes(bytes, width, height, channels, stride, sigma, zeroPadded: false);

    /// <summary>
    /// The same blur over a buffer whose border is the border of everything there is: pixels outside read as
    /// nothing at all, so the picture fades out where it ends rather than smearing to the edge and stopping
    /// there. This is the Blur adjustment's, which the Mac build runs over the grid the layer's pixels were
    /// padded out onto, uncropped and unclamped, and cuts back down afterwards.
    /// </summary>
    public static void ZeroPadded(Span<byte> bytes, int width, int height, int channels, int stride, double sigma) =>
        Bytes(bytes, width, height, channels, stride, sigma, zeroPadded: true);

    private static void Bytes(Span<byte> bytes, int width, int height, int channels, int stride, double sigma,
                              bool zeroPadded)
    {
        if (!(sigma > 0) || width <= 0 || height <= 0 || channels <= 0) return;
        var half = Half(sigma);
        var exact = half <= ExactHalf;
        var weights = exact ? Weights((float)sigma, half) : [];
        var sizes = exact ? [] : Sizes(sigma);
        // A zero-padded blur wider than the exact kernel is run on a plane grown by the same three sigma the
        // exact kernel would have needed. Each box pass pads its own border, and what it drops there is a
        // loss the next pass compounds — the passes leave the plane's outermost row at a seventh of what the
        // Gaussian leaves, where a flat field should fade to half at its border and not to a tenth. Over room
        // of its own the boxes read the zeros the border is meant to be and the loss cannot happen. Three
        // sigma is enough: what the composite still reaches past that is a tenth of a level of 255.
        var pad = zeroPadded && !exact ? half : 0;
        var columns = width + pad * 2;
        var lines = height + pad * 2;
        float[] plane;
        float[] scratch;
        try
        {
            plane = new float[columns * lines];
            scratch = new float[columns * lines];
        }
        catch (OutOfMemoryException)
        {
            return;
        }
        for (var channel = 0; channel < channels; channel++)
        {
            // The blur leaves its own values in the room it was given, and a second channel would read them
            // there as though the picture carried on past its border: the room is put back to nothing first.
            if (channel > 0 && pad > 0) Clear(plane, columns, lines, pad);
            for (var y = 0; y < height; y++)
            {
                var from = y * stride + channel;
                var to = (y + pad) * columns + pad;
                for (var x = 0; x < width; x++) plane[to + x] = bytes[from + x * channels];
            }
            if (exact) Exact(plane, scratch, columns, lines, weights, half, zeroPadded);
            else Boxed(plane, scratch, columns, lines, sizes, zeroPadded);
            for (var y = 0; y < height; y++)
            {
                var to = y * stride + channel;
                var from = (y + pad) * columns + pad;
                for (var x = 0; x < width; x++)
                {
                    bytes[to + x * channels] = (byte)Math.Clamp(
                        MathF.Round(plane[from + x], MidpointRounding.AwayFromZero), 0f, 255f);
                }
            }
        }
    }

    /// <summary>Puts a padded plane's room back to nothing, the data in its middle left alone.</summary>
    private static void Clear(float[] plane, int columns, int lines, int pad)
    {
        for (var y = 0; y < pad; y++)
        {
            Array.Clear(plane, y * columns, columns);
            Array.Clear(plane, (lines - 1 - y) * columns, columns);
        }
        for (var y = pad; y < lines - pad; y++)
        {
            Array.Clear(plane, y * columns, pad);
            Array.Clear(plane, y * columns + columns - pad, pad);
        }
    }

    /// <summary>How far the kernel reaches either side of a pixel, three standard deviations.</summary>
    private static int Half(double sigma) => Math.Max(1, (int)Math.Ceiling(sigma * 3));

    /// <summary>A normalized Gaussian, one tap per pixel, three standard deviations wide.</summary>
    private static float[] Weights(float sigma, int half)
    {
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

    // ---------------------------------------------------------------- The exact kernel

    /// <summary>
    /// The blend weights: rows first, then columns, the rows landing in <paramref name="scratch"/> so the
    /// columns can read them and write the plane itself, which is where the caller's pixels are.
    /// </summary>
    private static void Exact(float[] plane, float[] scratch, int width, int height, float[] weights, int half,
                              bool zeroPadded)
    {
        WeightedRows(plane, scratch, width, height, weights, half, zeroPadded);
        WeightedColumns(scratch, plane, width, height, weights, half, zeroPadded);
    }

    private static void WeightedRows(float[] source, float[] target, int width, int height, float[] weights,
                                     int half, bool zeroPadded)
    {
        for (var y = 0; y < height; y++)
        {
            var line = y * width;
            for (var x = 0; x < width; x++)
            {
                var total = 0f;
                if (zeroPadded && (x < half || x + half >= width))
                {
                    for (var tap = -half; tap <= half; tap++)
                    {
                        var column = x + tap;
                        if ((uint)column < (uint)width) total += weights[tap + half] * source[line + column];
                    }
                }
                else
                {
                    for (var tap = -half; tap <= half; tap++)
                        total += weights[tap + half] * source[line + Math.Clamp(x + tap, 0, width - 1)];
                }
                target[line + x] = total;
            }
        }
    }

    /// <summary>
    /// Columns a band at a time: every row the taps read is walked across the band, so the rows stay in
    /// cache instead of being fetched again for each column of a column-at-a-time walk.
    /// </summary>
    private static void WeightedColumns(float[] source, float[] target, int width, int height, float[] weights,
                                        int half, bool zeroPadded)
    {
        const int Band = 64;
        var sums = new float[Band];
        for (var start = 0; start < width; start += Band)
        {
            var count = Math.Min(Band, width - start);
            for (var y = 0; y < height; y++)
            {
                for (var index = 0; index < count; index++) sums[index] = 0;
                for (var tap = -half; tap <= half; tap++)
                {
                    var row = y + tap;
                    if (zeroPadded && (uint)row >= (uint)height) continue;
                    var weight = weights[tap + half];
                    var line = Math.Clamp(row, 0, height - 1) * width + start;
                    for (var index = 0; index < count; index++) sums[index] += weight * source[line + index];
                }
                var targetLine = y * width + start;
                for (var index = 0; index < count; index++) target[targetLine + index] = sums[index];
            }
        }
    }

    // ---------------------------------------------------------------- The boxes

    /// <summary>
    /// The box widths that stand in for a Gaussian of this spread: three, sized to the room a blur is given
    /// and as close to the Gaussian's variance as integers allow. Motion Blur asks here too, so a streak and
    /// a blur of the same spread are made of the same boxes, and a streak reaches no further along its line
    /// than an edge's ramp does — which is what a filter's margin and a tiled render's halo are measured by.
    /// </summary>
    internal static int[] Sizes(double sigma) => Boxes((float)sigma, Half(sigma) + 2);

    /// <summary>
    /// Three odd box widths whose combined spread is as close to the Gaussian's as the room a blur is given
    /// allows. Three, not four, because of what a pass reaches: a box of width w reaches half of w either
    /// side, so three of them reach no further than the three sigma the exact kernel reaches — which is the
    /// room a filter is given around a layer and the halo a piece of a tiled render is drawn with — where
    /// four would reach half a sigma past it and leave a seam down the edge of every piece. For a given total
    /// of radii, radii that are as equal as integers allow spread widest, so each total is split as evenly as
    /// it goes and the total whose spread lands closest to the Gaussian's is the one taken; the widths come
    /// out within two pixels of each other.
    /// </summary>
    private static int[] Boxes(float sigma, int room)
    {
        const int Count = 3;
        var best = new int[Count];
        for (var index = 0; index < Count; index++) best[index] = 1;
        var closest = double.MaxValue;
        for (var total = Count; total <= room; total++)
        {
            var share = total / Count;
            var spare = total - share * Count;
            var sizes = new int[Count];
            var variance = 0.0;
            for (var index = 0; index < Count; index++)
            {
                var radius = share + (index < spare ? 1 : 0);
                sizes[index] = radius * 2 + 1;
                variance += (sizes[index] * sizes[index] - 1) / 12.0;
            }
            var gap = Math.Abs(Math.Sqrt(variance) - sigma);
            if (gap >= closest) continue;
            closest = gap;
            best = sizes;
        }
        return best;
    }

    /// <summary>
    /// Three box passes across and three down, which together are the Gaussian. In place, and without a copy
    /// at the end of it: three passes across leave the rows in the scratch plane, so the three down start
    /// there and leave the columns in the plane the caller's pixels are in. Each pass keeps a running total,
    /// so a box costs the same whatever its width and the blur no longer costs anything for being wide.
    /// </summary>
    private static void Boxed(float[] plane, float[] scratch, int width, int height, int[] sizes, bool zeroPadded)
    {
        for (var pass = 0; pass < sizes.Length; pass++)
        {
            var radius = (sizes[pass] - 1) / 2;
            if (pass % 2 == 0) BoxRows(plane, scratch, width, height, radius, zeroPadded);
            else BoxRows(scratch, plane, width, height, radius, zeroPadded);
        }
        for (var pass = 0; pass < sizes.Length; pass++)
        {
            var radius = (sizes[pass] - 1) / 2;
            if (pass % 2 == 0) BoxColumns(scratch, plane, width, height, radius, zeroPadded);
            else BoxColumns(plane, scratch, width, height, radius, zeroPadded);
        }
    }

    private static void BoxRows(float[] source, float[] target, int width, int height, int radius, bool zeroPadded)
    {
        var inverse = 1f / (2 * radius + 1);
        for (var y = 0; y < height; y++)
        {
            var line = y * width;
            var total = 0f;
            if (zeroPadded)
            {
                var last = Math.Min(radius, width - 1);
                for (var tap = 0; tap <= last; tap++) total += source[line + tap];
                target[line] = total * inverse;
                for (var x = 1; x < width; x++)
                {
                    var added = x + radius;
                    if (added < width) total += source[line + added];
                    var dropped = x - 1 - radius;
                    if (dropped >= 0) total -= source[line + dropped];
                    target[line + x] = total * inverse;
                }
                continue;
            }
            for (var tap = -radius; tap <= radius; tap++) total += source[line + Math.Clamp(tap, 0, width - 1)];
            target[line] = total * inverse;
            for (var x = 1; x < width; x++)
            {
                total += source[line + Math.Min(x + radius, width - 1)];
                total -= source[line + Math.Max(x - 1 - radius, 0)];
                target[line + x] = total * inverse;
            }
        }
    }

    /// <summary>Columns a band at a time, as the exact kernel's are, and for the same reason.</summary>
    private static void BoxColumns(float[] source, float[] target, int width, int height, int radius, bool zeroPadded)
    {
        const int Band = 64;
        var inverse = 1f / (2 * radius + 1);
        var sums = new float[Band];
        for (var start = 0; start < width; start += Band)
        {
            var count = Math.Min(Band, width - start);
            for (var index = 0; index < count; index++)
            {
                var x = start + index;
                var total = 0f;
                if (zeroPadded)
                {
                    var last = Math.Min(radius, height - 1);
                    for (var tap = 0; tap <= last; tap++) total += source[tap * width + x];
                }
                else
                {
                    for (var tap = -radius; tap <= radius; tap++)
                        total += source[Math.Clamp(tap, 0, height - 1) * width + x];
                }
                sums[index] = total;
                target[x] = total * inverse;
            }
            for (var y = 1; y < height; y++)
            {
                var added = y + radius;
                var dropped = y - 1 - radius;
                var addedLine = zeroPadded
                    ? added < height ? added * width + start : -1
                    : Math.Min(added, height - 1) * width + start;
                var droppedLine = zeroPadded
                    ? dropped >= 0 ? dropped * width + start : -1
                    : Math.Max(dropped, 0) * width + start;
                var targetLine = y * width + start;
                for (var index = 0; index < count; index++)
                {
                    var total = sums[index];
                    if (addedLine >= 0) total += source[addedLine + index];
                    if (droppedLine >= 0) total -= source[droppedLine + index];
                    sums[index] = total;
                    target[targetLine + index] = total * inverse;
                }
            }
        }
    }
}
