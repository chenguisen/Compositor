using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>
/// The crop tool's frame: the rectangle it drags out and adjusts, the whole-pixel limits it is held to, and
/// the edges it lines up with while it is dragged. The eight handles around it are the transform inspector's,
/// so a corner is dragged the same way in both.
/// </summary>
public static class CropEdits
{
    /// <summary>
    /// A frame as whole pixels, at least one pixel on a side. The halves go away from zero, as Swift's
    /// <c>rounded()</c> takes them, so a drag lands where the Mac build lands it.
    /// </summary>
    public static SKRectI Snapped(SKRect rect)
    {
        var left = Half(rect.Left);
        var top = Half(rect.Top);
        var right = Half(rect.Right);
        var bottom = Half(rect.Bottom);
        return SKRectI.Create(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static int Half(float value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether a frame is one a crop may be made with. A frame may stretch past the canvas — what is outside
    /// is simply not in the picture — so only its size and how far out it sits are checked.
    /// </summary>
    public static bool Valid(SKRectI rect) =>
        rect.Width is >= 1 and <= DocumentLimits.MaxSide && rect.Height is >= 1 and <= DocumentLimits.MaxSide
        && Math.Abs((long)rect.Left) <= 1_000_000 && Math.Abs((long)rect.Top) <= 1_000_000;

    /// <summary>The frame for a pixel position, so the rest of the arithmetic can reuse the transform handles.</summary>
    public static LayerTransform Box(SKRectI frame) => new(frame.Left, frame.Top, frame.Width, frame.Height);

    /// <summary>The frame a box stands for, held to whole pixels.</summary>
    private static SKRectI Frame(LayerTransform box) =>
        Snapped(SKRect.Create((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height));

    /// <summary>
    /// The frame a drag from <paramref name="start"/> to <paramref name="end"/> makes. A ratio fits the drag
    /// to it; <paramref name="symmetric"/> grows the frame out from where the drag began, as its middle.
    /// </summary>
    public static SKRectI Create(SKPoint start, SKPoint end, double? ratio, bool symmetric)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (ratio is { } wanted and > 0)
        {
            // The longer side of the drag sets the other, so the drag stays inside the ratio.
            if (Math.Abs(dx) > Math.Abs(dy) * wanted) dy = (dy < 0 ? -1 : 1) * (float)(Math.Abs(dx) / wanted);
            else dx = (dx < 0 ? -1 : 1) * (float)(Math.Abs(dy) * wanted);
        }
        if (symmetric)
        {
            return Snapped(SKRect.Create(start.X - Math.Abs(dx), start.Y - Math.Abs(dy),
                Math.Abs(dx) * 2, Math.Abs(dy) * 2));
        }
        return Snapped(SKRect.Create(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy),
            Math.Abs(dx), Math.Abs(dy)));
    }

    /// <summary>
    /// The frame a drag of one handle makes: the opposite corner stays where it is, or with
    /// <paramref name="symmetric"/> the middle does. A ratio keeps the sides in proportion.
    /// </summary>
    public static SKRectI Resize(SKRectI original, TransformHandle handle, SKPoint start, SKPoint point,
        double? ratio, bool symmetric)
    {
        var resized = TransformEdits.Resize(Box(original), handle, start, point,
            lockRatio: ratio is not null, fromCentre: symmetric);
        return Frame(resized);
    }

    /// <summary>The frame moved by however far the pointer has come, at whole pixels.</summary>
    public static SKRectI Move(SKRectI original, SKPoint start, SKPoint point) =>
        Snapped(SKRect.Create(original.Left + point.X - start.X, original.Top + point.Y - start.Y,
            original.Width, original.Height));

    /// <summary>
    /// The frame a new ratio makes of an old one: its width and its left edge stay, and its height follows
    /// the middle it had, as the Mac build's ratio menu does.
    /// </summary>
    public static SKRectI ApplyRatio(SKRectI frame, double ratio)
    {
        if (ratio <= 0) return frame;
        var height = frame.Width / ratio;
        var next = Snapped(SKRect.Create(frame.Left, (float)(frame.MidY - height / 2), frame.Width, (float)height));
        return Valid(next) ? next : frame;
    }

    /// <summary>The ratio a document's own shape stands for, which is what "Original" means.</summary>
    public static double OriginalRatio(CanvasDocument document) =>
        document.Height > 0 ? (double)document.Width / document.Height : 1;

    /// <summary>
    /// What a crop edge lines up with: the canvas edges and the boxes of the layers that hold pixels. Crop
    /// edges do not snap to middles, so a crop cannot be pulled to a centre by accident.
    /// </summary>
    public static (List<double> Xs, List<double> Ys) Targets(CanvasDocument document)
    {
        var xs = new List<double> { 0, document.Width };
        var ys = new List<double> { 0, document.Height };
        var visible = document.EffectiveVisibleIDs();
        foreach (var layer in document.Layers)
        {
            if (layer.IsGroup || layer.Asset is null || !visible.Contains(layer.ID)) continue;
            var box = TransformEdits.Bounds(layer.Transform);
            xs.AddRange([Math.Round(box.Left), Math.Round(box.Right)]);
            ys.AddRange([Math.Round(box.Top), Math.Round(box.Bottom)]);
        }
        return (xs, ys);
    }

    /// <summary>
    /// The frame nudged so the edge being dragged meets a nearby edge — the one on the pointer's side, and
    /// with <paramref name="symmetric"/> its opposite about <paramref name="centre"/>. The lines it landed on
    /// come back too, to draw along.
    /// </summary>
    public static SKRectI Snap(CanvasDocument document, SKRectI frame, SKPoint point, SKPoint centre,
        bool symmetric, double tolerance, out double? lineX, out double? lineY)
    {
        var (xs, ys) = Targets(document);
        lineX = null;
        lineY = null;
        var left = (double)frame.Left;
        var top = (double)frame.Top;
        var right = (double)frame.Right;
        var bottom = (double)frame.Bottom;
        // The dragged edge is the one nearest the pointer.
        if (Math.Abs(point.X - frame.Left) <= Math.Abs(point.X - frame.Right))
        {
            if (Nearest(frame.Left, xs, tolerance) is { } snapped && snapped < right) { left = snapped; lineX = snapped; }
        }
        else if (Nearest(frame.Right, xs, tolerance) is { } snapped && snapped > left) { right = snapped; lineX = snapped; }
        if (Math.Abs(point.Y - frame.Top) <= Math.Abs(point.Y - frame.Bottom))
        {
            if (Nearest(frame.Top, ys, tolerance) is { } snapped && snapped < bottom) { top = snapped; lineY = snapped; }
        }
        else if (Nearest(frame.Bottom, ys, tolerance) is { } snapped && snapped > top) { bottom = snapped; lineY = snapped; }
        if (symmetric)
        {
            // The snapped edge sets the half size; the opposite edge mirrors it about the middle.
            if (lineX is not null)
            {
                var half = point.X >= centre.X ? right - centre.X : centre.X - left;
                if (half >= 0.5) { left = centre.X - half; right = centre.X + half; }
            }
            if (lineY is not null)
            {
                var half = point.Y >= centre.Y ? bottom - centre.Y : centre.Y - top;
                if (half >= 0.5) { top = centre.Y - half; bottom = centre.Y + half; }
            }
        }
        return Snapped(SKRect.Create((float)left, (float)top, (float)(right - left), (float)(bottom - top)));
    }

    /// <summary>
    /// The frame nudged so its nearest edge meets a nearby edge, keeping its size — which is what a frame
    /// being moved does.
    /// </summary>
    public static SKRectI SnapMove(CanvasDocument document, SKRectI frame, double tolerance,
        out double? lineX, out double? lineY)
    {
        var (xs, ys) = Targets(document);
        lineX = null;
        lineY = null;
        var moveX = Shift([frame.Left, frame.Right], xs, tolerance, out var snappedX);
        var moveY = Shift([frame.Top, frame.Bottom], ys, tolerance, out var snappedY);
        lineX = snappedX;
        lineY = snappedY;
        if (moveX == 0 && moveY == 0) return frame;
        return Snapped(SKRect.Create((float)(frame.Left + moveX), (float)(frame.Top + moveY), frame.Width, frame.Height));
    }

    private static double Shift(IReadOnlyList<double> edges, IReadOnlyList<double> targets, double tolerance,
        out double? line)
    {
        line = null;
        double? best = null;
        foreach (var edge in edges)
        {
            foreach (var target in targets)
            {
                var move = target - edge;
                if (Math.Abs(move) > tolerance) continue;
                if (best is { } current && Math.Abs(current) <= Math.Abs(move)) continue;
                best = move;
                line = target;
            }
        }
        return best ?? 0;
    }

    private static double? Nearest(double value, IReadOnlyList<double> targets, double tolerance)
    {
        double? best = null;
        foreach (var target in targets)
        {
            if (Math.Abs(target - value) > tolerance) continue;
            if (best is { } current && Math.Abs(current - value) <= Math.Abs(target - value)) continue;
            best = target;
        }
        return best;
    }
}
