namespace Compositor.Core.Pixels;

/// <summary>Port of the Mac build's motion blur, which Core Image runs as <c>CIMotionBlur</c>.</summary>
public static class MotionPixels
{
    /// <summary>
    /// The radius, in pixels, that Core Image gives a streak of length <paramref name="distance"/>: its
    /// taper is a Gaussian whose spread is about its radius, and an even streak of length d spreads d / √12,
    /// so this radius is made to match that spread.
    /// </summary>
    public static double RadiusPerPixel => 1 / Math.Sqrt(12);

    /// <summary>
    /// The widest streak run out tap by tap. A streak is a line of samples across the picture, so it costs its
    /// length times the pixels; past a spread of eight the shear below is cheaper by two orders of magnitude
    /// and is what the panel's long distances need. Eight is the same crossover the Gaussian blur uses, and for
    /// the same reason: three integer box widths cannot be made to carry the variance of a Gaussian narrower
    /// than that, and a streak a fifth too long is not what a short streak should look like.
    /// </summary>
    private const double ShearAbove = 8;

    /// <summary>
    /// Streaks premultiplied RGBA along an angle, <paramref name="radians"/> counterclockwise from
    /// horizontal as Photoshop measures it — which, in a grid whose rows run downwards, points along
    /// (cos, −sin). Each pixel is a Gaussian-weighted sum of the pixels along that line, spreading with
    /// <paramref name="sigma"/>; anything outside the source is nothing at all. Alpha is streaked too, so a
    /// cut-out edge smears the way the colour does.
    /// </summary>
    public static void Streak(ReadOnlySpan<byte> source, Span<byte> destination,
                              int width, int height, int stride, double sigma, double radians)
    {
        if (sigma <= 0 || width <= 0 || height <= 0 || source.Length < destination.Length) return;
        var dx = Math.Cos(radians);
        var dy = -Math.Sin(radians);
        if (sigma > ShearAbove && Sheared(source, destination, width, height, stride, (float)sigma, dx, dy)) return;
        Tapped(source, destination, width, height, stride, (float)sigma, dx, dy);
    }

    // ---------------------------------------------------------------- The line, sampled

    /// <summary>
    /// The honest kernel: a tap every pixel along the line out to three standard deviations, weighted by the
    /// Gaussian and read bilinearly, with anything outside the picture read as nothing. This is what the Mac
    /// build's Core Image filter does, and it is the reference the sheared kernel is held to.
    /// <para>
    /// A tap stands on the pixel's <em>middle</em>, which is where Core Image puts it: half a pixel in from
    /// the pixel's own index. Sampling at the index instead would stand the whole streak half a pixel off and
    /// blend the picture's first row with the nothing above it, which is a horizontal blur drawing a soft
    /// edge down a picture it should have left alone.
    /// </para>
    /// <para>
    /// One neighbourhood, four channels: the four pixels a tap stands on are fetched once and weighted into
    /// all of them, which is what keeps a short streak, where this kernel is still the one that runs, from
    /// costing four times what it needs to.
    /// </para>
    /// </summary>
    private static void Tapped(ReadOnlySpan<byte> source, Span<byte> destination,
                              int width, int height, int stride, float sigma, double dx, double dy)
    {
        var count = (int)Math.Ceiling(sigma * 3);
        var weights = new float[count * 2 + 1];
        double total = 0;
        for (var tap = -count; tap <= count; tap++)
        {
            var weight = Math.Exp(-(tap * (double)tap) / (2 * sigma * sigma));
            weights[tap + count] = (float)weight;
            total += weight;
        }
        for (var tap = 0; tap < weights.Length; tap++) weights[tap] = (float)(weights[tap] / total);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var tap = -count; tap <= count; tap++)
                {
                    var weight = weights[tap + count];
                    if (weight == 0) continue;
                    var sx = x + 0.5 + dx * tap;
                    var sy = y + 0.5 + dy * tap;
                    var left = (int)Math.Floor(sx - 0.5);
                    var top = (int)Math.Floor(sy - 0.5);
                    var fx = sx - 0.5 - left;
                    var fy = sy - 0.5 - top;
                    var across = left < 0 || left + 1 >= width;
                    var down = top < 0 || top + 1 >= height;
                    if (!across && !down)
                    {
                        var at = top * stride + left * 4;
                        r += weight * Blend(source, at, stride, fx, fy, 0);
                        g += weight * Blend(source, at, stride, fx, fy, 1);
                        b += weight * Blend(source, at, stride, fx, fy, 2);
                        a += weight * Blend(source, at, stride, fx, fy, 3);
                        continue;
                    }
                    // A tap on the picture's edge reads a pixel at a time, with what is outside as nothing.
                    var sample = Bilinear(source, stride, width, height, sx, sy);
                    r += weight * sample.Red;
                    g += weight * sample.Green;
                    b += weight * sample.Blue;
                    a += weight * sample.Alpha;
                }
                var to = y * stride + x * 4;
                destination[to] = Clamp(r);
                destination[to + 1] = Clamp(g);
                destination[to + 2] = Clamp(b);
                destination[to + 3] = Clamp(a);
            }
        }
    }

    /// <summary>One channel of the four pixels a tap stands on, weighted by where the tap fell between them.</summary>
    private static double Blend(ReadOnlySpan<byte> source, int at, int stride, double fx, double fy, int channel)
    {
        double upper = source[at + channel] + (source[at + 4 + channel] - source[at + channel]) * fx;
        double lower = source[at + stride + channel]
            + (source[at + stride + 4 + channel] - source[at + stride + channel]) * fx;
        return upper + (lower - upper) * fy;
    }

    private static (double Red, double Green, double Blue, double Alpha) Bilinear(
        ReadOnlySpan<byte> source, int stride, int width, int height, double x, double y)
    {
        var x0 = (int)Math.Floor(x - 0.5);
        var y0 = (int)Math.Floor(y - 0.5);
        var fx = x - 0.5 - x0;
        var fy = y - 0.5 - y0;
        double r = 0, g = 0, b = 0, a = 0;
        for (var j = 0; j < 2; j++)
        {
            var row = y0 + j;
            if (row < 0 || row >= height) continue;
            var wy = j != 0 ? fy : 1 - fy;
            if (wy == 0) continue;
            for (var i = 0; i < 2; i++)
            {
                var column = x0 + i;
                if (column < 0 || column >= width) continue;
                var weight = wy * (i != 0 ? fx : 1 - fx);
                if (weight == 0) continue;
                var at = row * stride + column * 4;
                r += weight * source[at];
                g += weight * source[at + 1];
                b += weight * source[at + 2];
                a += weight * source[at + 3];
            }
        }
        return (r, g, b, a);
    }

    private static byte Clamp(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    // ---------------------------------------------------------------- The line, run along

    /// <summary>
    /// The same blur at the cost of the picture rather than the streak's length: the frame is sheared until
    /// the line runs along its rows, three box passes walk those rows with a running total, and the picture is
    /// sheared back. Three boxes of the same sizes a Gaussian of that spread would be given, so a streak and a
    /// blur of equal reach look alike.
    /// <para>
    /// A shear only moves content along one axis, and the frame has to be tall enough to hold what it moves:
    /// shearing vertically grows it by the slope times the width, shearing horizontally by the inverse times
    /// the height. The smaller of the two is taken, which is why a line that is nearer upright is blurred down
    /// the other axis — the picture is turned on its side for the duration rather than padded to twice itself.
    /// False when it could not be done, so the caller can run the line out honestly instead.
    /// </para>
    /// </summary>
    private static bool Sheared(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height,
                                int stride, float sigma, double dx, double dy)
    {
        var slope = dy / dx;
        var tall = double.IsFinite(slope) ? Math.Abs(slope) * (width - 1) : double.PositiveInfinity;
        var wide = Math.Abs(dx / dy) * (height - 1);
        // Along the frame's rows the line takes a step of (1, slope), which is 1/|dx| of a pixel: a box of a
        // radius measured in frame cells is that much wider than the same radius along the line, so the boxes
        // are asked for the spread the line wants scaled to the cells they walk.
        var along = wide >= tall ? Math.Abs(dx) : Math.Abs(dy);
        if (!(sigma * along > ShearAbove)) return false;
        var cells = (float)(sigma * along);
        if (!(wide >= tall)) return Turned(source, destination, width, height, stride, cells, dx, dy);
        return Along(source, destination, width, height, stride, cells, slope);
    }

    /// <summary>The line is nearer upright than flat: the picture is turned on its side, blurred, turned back.</summary>
    private static bool Turned(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height,
                               int stride, float cells, double dx, double dy)
    {
        byte[] turned;
        byte[] back;
        try
        {
            turned = new byte[width * stride];
            back = new byte[width * stride];
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
        Transpose(source, turned, width, height, stride);
        // Turned, the line runs along the rows: what was upright is now flat, with the axes swapped.
        if (!Along(turned, back, height, width, width * 4, cells, dx / dy)) return false;
        Transpose(back, destination, height, width, width * 4);
        return true;
    }

    /// <summary>Rows exchanged for columns, so a line that ran down the picture now runs along it.</summary>
    private static void Transpose(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var line = y * stride;
            for (var x = 0; x < width; x++)
            {
                var from = line + x * 4;
                var to = (x * height + y) * 4;
                destination[to] = source[from];
                destination[to + 1] = source[from + 1];
                destination[to + 2] = source[from + 2];
                destination[to + 3] = source[from + 3];
            }
        }
    }

    /// <summary>
    /// The picture sheared by <paramref name="slope"/> into <paramref name="frame"/>, blurred along the frame's
    /// rows, and sheared back into the destination. The frame's row for a pixel of the line through (x, y) is
    /// (y − slope·x), so the line is one row of the frame and walking that row is walking the line.
    /// </summary>
    private static bool Along(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int stride,
                              float cells, double slope)
    {
        var sizes = GaussianBlur.Sizes(cells);
        // The room the passes need along the line, so that each pass's own ends are the composite's and not a
        // loss the next pass compounds: without it a run of three boxes leaves a third of the picture at its
        // own edge where the Gaussian leaves a half. The picture sits in the middle of a frame that wide.
        var room = 0;
        foreach (var size in sizes) room += (size - 1) / 2;
        var shift = slope * (width - 1);
        var top = (int)Math.Ceiling(Math.Max(0, shift));
        var lines = height + (int)Math.Ceiling(Math.Abs(shift));
        var row = width + room * 2;
        byte[] frame;
        byte[] scratch;
        try
        {
            frame = new byte[lines * row * 4];
            scratch = new byte[lines * row * 4];
        }
        catch (OutOfMemoryException)
        {
            return false;
        }

        // Every column of the picture moved along it by the slope, one frame row at a time, sampled between
        // the picture's own rows. What falls outside the picture is nothing, which is what the line reads
        // there — and so is the room itself, which is the line running on past the picture's end.
        for (var column = 0; column < row; column++)
        {
            var x = column - room;
            var inside = (uint)x < (uint)width;
            var source4 = x * 4;
            for (var line = 0; line < lines; line++)
            {
                var at = (line * row + column) * 4;
                if (!inside)
                {
                    frame[at] = frame[at + 1] = frame[at + 2] = frame[at + 3] = 0;
                    continue;
                }
                var y = line - top + slope * x;
                var upper = (int)Math.Floor(y);
                var f = (float)(y - upper);
                for (var channel = 0; channel < 4; channel++)
                {
                    var above = (uint)upper < (uint)height ? source[upper * stride + source4 + channel] : 0;
                    var below = (uint)(upper + 1) < (uint)height ? source[(upper + 1) * stride + source4 + channel] : 0;
                    frame[at + channel] = (byte)MathF.Round(above + (below - above) * f, MidpointRounding.AwayFromZero);
                }
            }
        }

        // Three box passes along the rows, which together are the Gaussian the streak is. Three is odd, so the
        // last of them writes the scratch: the blurred picture is read from there.
        var from = frame;
        var to = scratch;
        foreach (var size in sizes)
        {
            BoxRows(from, to, row, lines, (size - 1) / 2);
            (from, to) = (to, from);
        }

        // Back the other way: the frame row the pixel's own line sits on, read between the frame's rows.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var line = y + top - slope * x;
                var upper = (int)Math.Floor(line);
                var f = (float)(line - upper);
                var to4 = y * stride + x * 4;
                var column = (x + room) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var above = (uint)upper < (uint)lines ? from[upper * row * 4 + column + channel] : 0;
                    var below = (uint)(upper + 1) < (uint)lines ? from[(upper + 1) * row * 4 + column + channel] : 0;
                    destination[to4 + channel] =
                        (byte)MathF.Round(above + (below - above) * f, MidpointRounding.AwayFromZero);
                }
            }
        }
        return true;
    }

    /// <summary>
    /// One box pass along the frame's rows, a running total a channel at a time: a pixel past the end of a row
    /// reads as nothing and still counts in the divisor, so a row's ends fade the way the picture's own do.
    /// </summary>
    private static void BoxRows(ReadOnlySpan<byte> source, Span<byte> target, int width, int lines, int radius)
    {
        var window = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            var at = line * width * 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var total = 0;
                var first = Math.Min(radius, width - 1);
                for (var tap = 0; tap <= first; tap++) total += source[at + tap * 4 + channel];
                target[at + channel] = (byte)((total + window / 2) / window);
                for (var x = 1; x < width; x++)
                {
                    var added = x + radius;
                    if (added < width) total += source[at + added * 4 + channel];
                    var dropped = x - 1 - radius;
                    if (dropped >= 0) total -= source[at + dropped * 4 + channel];
                    target[at + x * 4 + channel] = (byte)((total + window / 2) / window);
                }
            }
        }
    }
}
