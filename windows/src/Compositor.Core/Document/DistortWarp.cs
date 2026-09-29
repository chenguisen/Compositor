using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>Pixels resampled into a shape, and the axis-aligned transform that places them.</summary>
public sealed record WarpedImage(SKBitmap Image, LayerTransform Transform);

/// <summary>
/// Free distortion: a layer's four corners moved one at a time. A layer's transform is a rectangle with an
/// angle, so a shape that is not one has to be resampled into the pixels themselves — which is what the Mac
/// build does too, and what this works out.
/// </summary>
public static class DistortWarp
{
    /// <summary>How small a triangle has to be before the shape has nothing left to draw.</summary>
    private const double SmallestArea = 0.01;

    /// <summary>
    /// A transform's corners, in handle order: top left, top right, bottom right, bottom left. Taken from the
    /// transform's own unit square, so a turned layer's corners come back turned.
    /// </summary>
    public static SKPoint[] Corners(LayerTransform transform)
    {
        var toDocument = BrushEdits.PixelToDocument(transform, 1, 1);
        return
        [
            toDocument.MapPoint(0, 0),
            toDocument.MapPoint(1, 0),
            toDocument.MapPoint(1, 1),
            toDocument.MapPoint(0, 1),
        ];
    }

    /// <summary>
    /// Four corners that are somewhere, with area between them: a shape with a triangle of nothing in it has
    /// nothing to draw. A corner dragged past its neighbours folds the shape over, which is allowed — such a
    /// shape is drawn as two triangles rather than in perspective.
    /// </summary>
    public static bool IsUsable(IReadOnlyList<SKPoint> corners)
    {
        if (corners.Count != 4) return false;
        foreach (var corner in corners)
        {
            if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y)) return false;
            if (Math.Abs(corner.X) > 1_000_000 || Math.Abs(corner.Y) > 1_000_000) return false;
        }
        return Math.Abs(Area(corners[0], corners[1], corners[2])) > SmallestArea
            && Math.Abs(Area(corners[0], corners[2], corners[3])) > SmallestArea;
    }

    /// <summary>A shape a perspective map can take: convex, and wound the same way all the way round.</summary>
    public static bool IsConvex(IReadOnlyList<SKPoint> corners)
    {
        if (!IsUsable(corners)) return false;
        double sign = 0;
        for (var index = 0; index < 4; index++)
        {
            var a = corners[index];
            var b = corners[(index + 1) % 4];
            var c = corners[(index + 2) % 4];
            var cross = (double)(b.X - a.X) * (c.Y - b.Y) - (double)(b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) <= SmallestArea) return false;
            if (sign == 0) sign = cross < 0 ? -1 : 1;
            else if (cross < 0 != sign < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// The pixels of <paramref name="image"/> — a whole layer's, or a mask's — shown through
    /// <paramref name="transform"/>, resampled so that transform's corners land on <paramref name="corners"/>.
    /// The result covers the shape's whole-pixel bounds. Null when the shape is not one that can be drawn, or
    /// when it would need a surface larger than one may be.
    /// </summary>
    public static WarpedImage? Warp(SKBitmap image, LayerTransform transform, IReadOnlyList<SKPoint> corners)
    {
        if (!IsUsable(corners) || image.Width <= 0 || image.Height <= 0) return null;
        var bounds = Bounds(corners);
        if (bounds.Width > DocumentLimits.MaxSide || bounds.Height > DocumentLimits.MaxSide
            || (long)bounds.Width * bounds.Height > DocumentLimits.MaxSurfacePixels)
        {
            return null;
        }
        var placed = new LayerTransform(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            0, transform.FlipX, transform.FlipY, transform.Sampling);
        // A uniform mask covers any shape, so there is nothing to resample.
        if (image.Width == 1 && image.Height == 1) return new WarpedImage(image.Copy(), placed);
        var warped = IsConvex(corners)
            ? InPerspective(image, transform, corners, bounds)
            : InTwoHalves(image, transform, corners, bounds);
        return warped is null ? null : new WarpedImage(warped, placed);
    }


    /// <summary>The rectangle a shape covers, whole pixels outward from it.</summary>
    public static SKRectI Bounds(IReadOnlyList<SKPoint> corners)
    {
        var left = (int)Math.Floor(corners.Min(corner => corner.X));
        var top = (int)Math.Floor(corners.Min(corner => corner.Y));
        var right = (int)Math.Ceiling(corners.Max(corner => corner.X));
        var bottom = (int)Math.Ceiling(corners.Max(corner => corner.Y));
        return SKRectI.Create(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// The picture walked backwards: every output pixel is taken back through the shape's perspective to the
    /// place it came from, and the colour there is read out. Sampling backwards is what keeps the picture
    /// whole — a forward map would leave gaps where no pixel happened to land.
    /// </summary>
    private static SKBitmap? InPerspective(SKBitmap image, LayerTransform transform, IReadOnlyList<SKPoint> corners,
        SKRectI bounds)
    {
        if (Backward(corners) is not { } back) return null;
        SKBitmap? output = null;
        try
        {
            output = FilterSurface.Allocate(bounds.Width, bounds.Height);
            var source = image.GetPixelSpan();
            var target = output.GetPixelSpan();
            var stride = output.RowBytes;
            var nearest = transform.Sampling == LayerSampling.Nearest;
            for (var y = 0; y < bounds.Height; y++)
            {
                for (var x = 0; x < bounds.Width; x++)
                {
                    // Pixel centres, so the shape is sampled where each pixel actually sits.
                    var (u, v) = back(bounds.Left + x + 0.5, bounds.Top + y + 0.5);
                    Sample(source, image.RowBytes, image.Width, image.Height, u, v, transform, nearest,
                        target, y * stride + x * 4);
                }
            }
            return output;
        }
        catch (OutOfMemoryException)
        {
            output?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// A folded shape — a corner pulled past its neighbours — has no perspective that takes the picture to it,
    /// so each half either side of the diagonal is taken there on its own, as two triangles meeting along it.
    /// </summary>
    private static SKBitmap? InTwoHalves(SKBitmap image, LayerTransform transform, IReadOnlyList<SKPoint> corners,
        SKRectI bounds)
    {
        // The diagonal runs from the first corner to the third, so the halves are (0,1,2) and (0,2,3).
        var halves = new[]
        {
            (One: 0, Two: 1, Three: 2),
            (One: 0, Two: 2, Three: 3),
        };
        var maps = new SKMatrix?[2];
        for (var index = 0; index < 2; index++)
        {
            var half = halves[index];
            var source = new[] { Unit(image, transform, half.One), Unit(image, transform, half.Two), Unit(image, transform, half.Three) };
            var target = new[] { Within(corners[half.One], bounds), Within(corners[half.Two], bounds), Within(corners[half.Three], bounds) };
            maps[index] = Triangle(source, target);
            if (maps[index] is null) return null;
        }
        SKBitmap? output = null;
        try
        {
            output = FilterSurface.Allocate(bounds.Width, bounds.Height);
            using var canvas = new SKCanvas(output);
            using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver, IsAntialias = true };
            for (var index = 0; index < 2; index++)
            {
                var half = halves[index];
                using var builder = new SKPathBuilder();
                builder.MoveTo(Within(corners[half.One], bounds));
                builder.LineTo(Within(corners[half.Two], bounds));
                builder.LineTo(Within(corners[half.Three], bounds));
                builder.Close();
                using var clip = builder.Detach();
                var map = maps[index]!.Value;
                canvas.Save();
                // Hard along the shared diagonal, so the halves meet exactly rather than blending twice.
                canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: false);
                canvas.Concat(in map);
                canvas.DrawBitmap(image, SKRect.Create(0, 0, image.Width, image.Height),
                    new SKSamplingOptions(SKFilterMode.Linear), paint);
                canvas.Restore();
            }
            canvas.Flush();
            return output;
        }
        catch (OutOfMemoryException)
        {
            output?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The affine map taking three of the picture's corners to three of the shape's — one half of a folded
    /// shape, both in the output's own pixels.
    /// </summary>
    private static SKMatrix? Triangle(IReadOnlyList<SKPoint> source, IReadOnlyList<SKPoint> target)
    {
        var ux = source[1].X - source[0].X;
        var uy = source[1].Y - source[0].Y;
        var vx = source[2].X - source[0].X;
        var vy = source[2].Y - source[0].Y;
        var determinant = (double)ux * vy - (double)vx * uy;
        if (Math.Abs(determinant) < 1e-9) return null;
        var wx = target[1].X - target[0].X;
        var wy = target[1].Y - target[0].Y;
        var zx = target[2].X - target[0].X;
        var zy = target[2].Y - target[0].Y;
        var a = (wx * vy - zx * uy) / determinant;
        var c = (zx * ux - wx * vx) / determinant;
        var b = (wy * vy - zy * uy) / determinant;
        var d = (zy * ux - wy * vx) / determinant;
        return new SKMatrix
        {
            ScaleX = (float)a,
            SkewX = (float)c,
            SkewY = (float)b,
            ScaleY = (float)d,
            TransX = (float)(target[0].X - (a * source[0].X + c * source[0].Y)),
            TransY = (float)(target[0].Y - (b * source[0].X + d * source[0].Y)),
            // A default SKMatrix has this zero, which is a matrix that divides by nothing: it has to be one.
            Persp2 = 1,
        };
    }

    /// <summary>Where one corner of the unit square is in the picture's own pixels, flips and all.</summary>
    private static SKPoint Unit(SKBitmap image, LayerTransform transform, int corner)
    {
        var (u, v) = corner switch
        {
            0 => (0f, 0f),
            1 => (1f, 0f),
            2 => (1f, 1f),
            _ => (0f, 1f),
        };
        if (transform.FlipX) u = 1 - u;
        if (transform.FlipY) v = 1 - v;
        return new SKPoint(u * image.Width, v * image.Height);
    }

    /// <summary>A document point as its place in the output's own pixels.</summary>
    private static SKPoint Within(SKPoint corner, SKRectI bounds) => new(corner.X - bounds.Left, corner.Y - bounds.Top);

    /// <summary>
    /// The shape's perspective read backwards: a document point to the place it came from on the unit square.
    /// Null when the shape has no perspective at all, which is a shape with no area.
    /// </summary>
    private static Func<double, double, (double U, double V)>? Backward(IReadOnlyList<SKPoint> corners)
    {
        if (Solved(UnitSquare(), corners) is not { } forward) return null;
        var m = Inverted(forward);
        if (m is null) return null;
        return (x, y) => Map(m, x, y);
    }

    /// <summary>
    /// The shape's perspective read forwards: a place on the unit square to where it lands on the document.
    /// Null when the shape has no perspective at all, which is a shape with no area.
    /// </summary>
    private static Func<double, double, (double X, double Y)>? Forward(IReadOnlyList<SKPoint> corners) =>
        Solved(UnitSquare(), corners) is { } m ? (x, y) => Map(m, x, y) : null;

    /// <summary>
    /// Where <paramref name="placement"/>'s corners (handle order) land when the perspective taking
    /// <paramref name="box"/>'s corners to <paramref name="corners"/> is applied to the document around it too.
    /// A layer beside the one whose corner was dragged is carried by the same perspective as the box, so
    /// several layers distort together and keep the shape they had.
    /// </summary>
    public static SKPoint[]? Carried(LayerTransform placement, LayerTransform box, IReadOnlyList<SKPoint> corners)
    {
        if (!IsUsable(corners) || Forward(corners) is not { } map) return null;
        if (!BrushEdits.PixelToDocument(box, 1, 1).TryInvert(out var toBox)) return null;
        var placed = Corners(placement);
        var carried = new SKPoint[placed.Length];
        for (var index = 0; index < placed.Length; index++)
        {
            var unit = toBox.MapPoint(placed[index]);
            var (x, y) = map(unit.X, unit.Y);
            if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
            carried[index] = new SKPoint((float)x, (float)y);
        }
        return IsUsable(carried) ? carried : null;
    }

    /// <summary>The four places a perspective is worked out between: the whole unit square's corners.</summary>
    private static (double X, double Y)[] UnitSquare() => [(0, 0), (1, 0), (1, 1), (0, 1)];

    /// <summary>A point taken through a 3x3 plane map, as that map's own kind of point.</summary>
    private static (double X, double Y) Map(double[] m, double x, double y)
    {
        var w = m[6] * x + m[7] * y + m[8];
        if (Math.Abs(w) < 1e-12) return (double.NaN, double.NaN);
        return ((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
    }

    /// <summary>A 3x3 plane map read the other way, from its adjugate; null when it has no inverse at all.</summary>
    private static double[]? Inverted(double[] forward)
    {
        var determinant = forward[0] * (forward[4] * forward[8] - forward[5] * forward[7])
                        - forward[1] * (forward[3] * forward[8] - forward[5] * forward[6])
                        + forward[2] * (forward[3] * forward[7] - forward[4] * forward[6]);
        if (Math.Abs(determinant) < 1e-12) return null;
        return
        [
            (forward[4] * forward[8] - forward[5] * forward[7]) / determinant,
            (forward[2] * forward[7] - forward[1] * forward[8]) / determinant,
            (forward[1] * forward[5] - forward[2] * forward[4]) / determinant,
            (forward[5] * forward[6] - forward[3] * forward[8]) / determinant,
            (forward[0] * forward[8] - forward[2] * forward[6]) / determinant,
            (forward[2] * forward[3] - forward[0] * forward[5]) / determinant,
            (forward[3] * forward[7] - forward[4] * forward[6]) / determinant,
            (forward[1] * forward[6] - forward[0] * forward[7]) / determinant,
            (forward[0] * forward[4] - forward[1] * forward[3]) / determinant,
        ];
    }

    /// <summary>
    /// The plane perspective taking four points to four others, as the nine numbers of the forward map — the
    /// unit square onto the shape. Solved as the eight unknowns the four corner correspondences give; null when
    /// the points leave it undetermined, which is a shape with no area between them.
    /// </summary>
    private static double[]? Solved((double X, double Y)[] from, IReadOnlyList<SKPoint> to)
    {
        var rows = new double[8, 9];
        for (var index = 0; index < 4; index++)
        {
            var (x, y) = from[index];
            double u = to[index].X, v = to[index].Y;
            rows[index * 2, 0] = x;
            rows[index * 2, 1] = y;
            rows[index * 2, 2] = 1;
            rows[index * 2, 6] = -u * x;
            rows[index * 2, 7] = -u * y;
            rows[index * 2, 8] = u;
            rows[index * 2 + 1, 3] = x;
            rows[index * 2 + 1, 4] = y;
            rows[index * 2 + 1, 5] = 1;
            rows[index * 2 + 1, 6] = -v * x;
            rows[index * 2 + 1, 7] = -v * y;
            rows[index * 2 + 1, 8] = v;
        }
        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
            {
                if (Math.Abs(rows[row, column]) > Math.Abs(rows[pivot, column])) pivot = row;
            }
            if (Math.Abs(rows[pivot, column]) < 1e-12) return null;
            if (pivot != column)
            {
                for (var at = column; at < 9; at++) (rows[column, at], rows[pivot, at]) = (rows[pivot, at], rows[column, at]);
            }
            var scale = rows[column, column];
            for (var at = column; at < 9; at++) rows[column, at] /= scale;
            for (var row = 0; row < 8; row++)
            {
                if (row == column) continue;
                var factor = rows[row, column];
                if (factor == 0) continue;
                for (var at = column; at < 9; at++) rows[row, at] -= factor * rows[column, at];
            }
        }
        var forward = new double[9];
        for (var index = 0; index < 8; index++) forward[index] = rows[index, 8];
        forward[8] = 1;
        return forward;
    }

    /// <summary>The colour at a place on the shape, read out of the picture; nothing when it is off it.</summary>
    private static void Sample(ReadOnlySpan<byte> source, int stride, int width, int height, double u, double v,
        LayerTransform transform, bool nearest, Span<byte> target, int at)
    {
        var x = (transform.FlipX ? 1 - u : u) * width;
        var y = (transform.FlipY ? 1 - v : v) * height;
        if (!(x >= 0) || !(y >= 0) || x >= width || y >= height)
        {
            target[at] = target[at + 1] = target[at + 2] = target[at + 3] = 0;
            return;
        }
        int x0, y0;
        double fx, fy;
        if (nearest)
        {
            x0 = (int)Math.Floor(x);
            y0 = (int)Math.Floor(y);
            fx = fy = 0;
        }
        else
        {
            x0 = (int)Math.Floor(x - 0.5);
            y0 = (int)Math.Floor(y - 0.5);
            fx = x - 0.5 - x0;
            fy = y - 0.5 - y0;
        }
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
                var from = row * stride + column * 4;
                r += weight * source[from];
                g += weight * source[from + 1];
                b += weight * source[from + 2];
                a += weight * source[from + 3];
            }
        }
        target[at] = Bounded(r);
        target[at + 1] = Bounded(g);
        target[at + 2] = Bounded(b);
        target[at + 3] = Bounded(a);
    }

    private static byte Bounded(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static double Area(SKPoint a, SKPoint b, SKPoint c) =>
        (double)(b.X - a.X) * (c.Y - a.Y) - (double)(b.Y - a.Y) * (c.X - a.X);
}
