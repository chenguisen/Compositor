using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>The eight scale handles around a box, and the grip that turns it.</summary>
public enum TransformHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,

    /// <summary>The grip above the top edge, which turns the box about its middle.</summary>
    Rotate,
}

/// <summary>
/// The transform inspector's arithmetic: where the handles sit, which one a click has hold of, and the box a
/// drag works out. Every drag is measured from the transform the drag began with rather than compounding, so
/// dragging back and forth returns the layer to where it started.
/// </summary>
public static class TransformEdits
{
    /// <summary>How far above the top edge the turning grip sits, in screen pixels.</summary>
    public const double RotateGrip = 28;

    /// <summary>How close a click has to be, in screen pixels, to take hold of a handle or an edge.</summary>
    public const double Grab = 10;

    /// <summary>The eight scale handles as units of the box, in the order of <see cref="TransformHandle"/>.</summary>
    private static readonly (double X, double Y)[] Units =
    [
        (0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1), (0.5, 1), (0, 1), (0, 0.5),
    ];

    /// <summary>Where one of the eight scale handles sits on the document.</summary>
    public static SKPoint Position(LayerTransform box, TransformHandle handle)
    {
        var (x, y) = Units[(int)handle];
        return box.Point(x, y);
    }

    /// <summary>
    /// Where the turning grip sits: <paramref name="reach"/> document pixels out from the middle of the top
    /// edge, at right angles to it, so it keeps its distance on screen at any zoom.
    /// </summary>
    public static SKPoint RotatePosition(LayerTransform box, double reach)
    {
        var top = box.Point(0.5, 0);
        return new SKPoint(top.X + (float)(Math.Sin(box.Radians) * reach),
            top.Y - (float)(Math.Cos(box.Radians) * reach));
    }

    /// <summary>
    /// Which handle a click takes hold of, within <paramref name="tolerance"/> document pixels: a handle
    /// first, then the edge between two of them — which is that edge's middle handle, so the whole side can
    /// be dragged.
    /// </summary>
    public static TransformHandle? HandleAt(LayerTransform box, SKPoint point, double tolerance, double grip)
    {
        if (Near(point, RotatePosition(box, grip), tolerance)) return TransformHandle.Rotate;
        for (var index = 0; index < Units.Length; index++)
        {
            if (Near(point, Position(box, (TransformHandle)index), tolerance)) return (TransformHandle)index;
        }
        // The edges, as pairs of corners going round the box: top, right, bottom, left.
        foreach (var (start, end, middle) in new[]
                 {
                     (TransformHandle.TopLeft, TransformHandle.TopRight, TransformHandle.Top),
                     (TransformHandle.TopRight, TransformHandle.BottomRight, TransformHandle.Right),
                     (TransformHandle.BottomRight, TransformHandle.BottomLeft, TransformHandle.Bottom),
                     (TransformHandle.BottomLeft, TransformHandle.TopLeft, TransformHandle.Left),
                 })
        {
            var a = Position(box, start);
            var b = Position(box, end);
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0) continue;
            var along = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
            if (along is < 0 or > 1) continue;
            var across = Math.Sqrt(Math.Pow(point.X - a.X - along * dx, 2) + Math.Pow(point.Y - a.Y - along * dy, 2));
            if (across <= tolerance) return middle;
        }
        return null;
    }

    /// <summary>The four corners of the box on the document, from its top left round to its bottom left.</summary>
    public static SKPoint[] Corners(LayerTransform box) =>
    [
        Position(box, TransformHandle.TopLeft),
        Position(box, TransformHandle.TopRight),
        Position(box, TransformHandle.BottomRight),
        Position(box, TransformHandle.BottomLeft),
    ];

    /// <summary>The upright box a rotated rectangle covers.</summary>
    public static SKRect Bounds(LayerTransform box)
    {
        var corners = Corners(box);
        var minX = corners.Min(corner => corner.X);
        var minY = corners.Min(corner => corner.Y);
        var maxX = corners.Max(corner => corner.X);
        var maxY = corners.Max(corner => corner.Y);
        return new SKRect(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// A move of the whole box. Shift keeps the drag on one axis, the longer one winning, as the Mac build
    /// keeps it.
    /// </summary>
    public static LayerTransform Move(LayerTransform original, double dx, double dy, bool axisLock = false)
    {
        if (axisLock)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        var moved = original with { X = original.X + dx, Y = original.Y + dy };
        return moved.IsValid ? moved : original;
    }

    /// <summary>
    /// The box a scale handle drags out: the handle follows the pointer while the opposite corner — or, with
    /// <paramref name="fromCentre"/>, the middle — stays put. Dragging a handle past the far side turns the
    /// layer over rather than stopping at nothing, as a negative scale would. <paramref name="lockRatio"/>
    /// keeps the sides in proportion; Shift is what asks for that, and Shift also releases it, as in the Mac
    /// build.
    /// </summary>
    public static LayerTransform Resize(LayerTransform original, TransformHandle handle, SKPoint start,
        SKPoint point, bool lockRatio, bool fromCentre)
    {
        if (handle == TransformHandle.Rotate) return original;
        var (hx, hy) = Units[(int)handle];
        var anchorX = fromCentre ? 0.5 : 1 - hx;
        var anchorY = fromCentre ? 0.5 : 1 - hy;
        var anchor = original.Point(anchorX, anchorY);
        // The handle's own place at the start of the drag, plus how far the pointer has come: a grab never
        // jumps the box, and the drag is measured from where the drag began.
        var initial = original.Point(hx, hy);
        var dx = initial.X + point.X - start.X - anchor.X;
        var dy = initial.Y + point.Y - start.Y - anchor.Y;
        // Moving towards the middle covers half the size on each side, so it counts double.
        var span = fromCentre ? 2.0 : 1.0;
        var cos = Math.Cos(original.Radians);
        var sin = Math.Sin(original.Radians);
        var localX = (dx * cos + dy * sin) * span;
        var localY = (-dx * sin + dy * cos) * span;
        // Which way this handle lies from the middle: an edge handle has one of these at zero.
        var sx = hx * 2 - 1;
        var sy = hy * 2 - 1;
        var rawWidth = sx == 0 ? original.Width : localX * sx;
        var rawHeight = sy == 0 ? original.Height : localY * sy;
        var mirroredX = rawWidth < 0;
        var mirroredY = rawHeight < 0;
        var width = Math.Max(1, Math.Abs(rawWidth));
        var height = Math.Max(1, Math.Abs(rawHeight));
        if (lockRatio)
        {
            double factor;
            if (sx == 0) factor = height / original.Height;
            else if (sy == 0) factor = width / original.Width;
            else
            {
                // The drag projected onto the box's own diagonal keeps the sides in proportion.
                factor = Math.Max(1 / Math.Min(original.Width, original.Height),
                    (localX * sx * original.Width + localY * sy * original.Height)
                    / (original.Width * original.Width + original.Height * original.Height));
            }
            width = original.Width * factor;
            height = original.Height * factor;
        }
        var sized = original with
        {
            Width = width,
            Height = height,
            FlipX = mirroredX ? !original.FlipX : original.FlipX,
            FlipY = mirroredY ? !original.FlipY : original.FlipY,
        };
        // Turned over, the box lies on the other side of the anchor.
        var offsetX = (0.5 - anchorX) * width * (mirroredX ? -1 : 1);
        var offsetY = (0.5 - anchorY) * height * (mirroredY ? -1 : 1);
        var centreX = anchor.X + offsetX * cos - offsetY * sin;
        var centreY = anchor.Y + offsetX * sin + offsetY * cos;
        var placed = sized with { X = centreX - width / 2, Y = centreY - height / 2 };
        return placed.IsValid ? placed : original;
    }

    /// <summary>
    /// The turn a drag works out, measured about the box's middle from where the drag began.
    /// <paramref name="steps"/> keeps it to whole multiples of fifteen degrees, as Shift does.
    /// </summary>
    public static LayerTransform Rotate(LayerTransform original, SKPoint start, SKPoint point, bool steps)
    {
        var centreX = original.CenterX;
        var centreY = original.CenterY;
        var delta = Math.Atan2(point.Y - centreY, point.X - centreX)
            - Math.Atan2(start.Y - centreY, start.X - centreX);
        var rotation = original.Rotation + delta * 180 / Math.PI;
        if (steps) rotation = Math.Round(rotation / 15) * 15;
        var turned = original with { Rotation = rotation };
        return turned.IsValid ? turned : original;
    }

    /// <summary>
    /// What a moving layer lines up with: the canvas edges and middle, the canvas guides, and the boxes of
    /// the other layers that hold pixels.
    /// </summary>
    public static (List<double> Xs, List<double> Ys) SnapTargets(CanvasDocument document,
        IReadOnlyCollection<Guid> moving)
    {
        var xs = new List<double> { 0, document.Width, document.Width / 2.0 };
        var ys = new List<double> { 0, document.Height, document.Height / 2.0 };
        foreach (var guide in document.Guides)
        {
            if (guide.Axis == GuideAxis.Vertical) xs.Add(guide.Position);
            else ys.Add(guide.Position);
        }
        var visible = document.EffectiveVisibleIDs();
        foreach (var layer in document.Layers)
        {
            if (layer.IsGroup || layer.Asset is null || moving.Contains(layer.ID) || !visible.Contains(layer.ID)) continue;
            var box = Bounds(layer.Transform);
            xs.AddRange([Math.Round(box.Left), Math.Round(box.MidX), Math.Round(box.Right)]);
            ys.AddRange([Math.Round(box.Top), Math.Round(box.MidY), Math.Round(box.Bottom)]);
        }
        return (xs, ys);
    }

    /// <summary>
    /// The box nudged so its nearest edge or middle meets a nearby target, each axis on its own and only
    /// within <paramref name="tolerance"/> document pixels. The two lines it landed on come back too, to
    /// draw along.
    /// </summary>
    public static LayerTransform Snap(CanvasDocument document, LayerTransform draft, IReadOnlyCollection<Guid> moving,
        double tolerance, out double? lineX, out double? lineY)
    {
        var (xs, ys) = SnapTargets(document, moving);
        var snap = TransformSnap.Offset(Bounds(draft), xs, ys, tolerance);
        lineX = snap.LineX;
        lineY = snap.LineY;
        if (snap.X == 0 && snap.Y == 0) return draft;
        var snapped = draft with { X = draft.X + snap.X, Y = draft.Y + snap.Y };
        return snapped.IsValid ? snapped : draft;
    }

    private static bool Near(SKPoint point, SKPoint other, double tolerance)
    {
        var dx = point.X - other.X;
        var dy = point.Y - other.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }
}

/// <summary>
/// Moving a layer snaps its edges and middle to the canvas, the guides and the other layers. The pull is a
/// fixed distance on screen, so it feels the same at any zoom, and small enough to slide past without a
/// fight.
/// </summary>
public static class TransformSnap
{
    /// <summary>How close, in screen pixels, a line comes before it snaps.</summary>
    public const double Distance = 10;

    /// <summary>
    /// The move that puts whichever of the box's left, middle or right lands nearest one of
    /// <paramref name="xs"/> on it, and the same vertically — each axis on its own, and only within
    /// <paramref name="tolerance"/>. The lines it landed on come back too.
    /// </summary>
    public static (double X, double Y, double? LineX, double? LineY) Offset(SKRect box, IReadOnlyList<double> xs,
        IReadOnlyList<double> ys, double tolerance)
    {
        var horizontal = Shift([box.Left, box.MidX, box.Right], xs, tolerance);
        var vertical = Shift([box.Top, box.MidY, box.Bottom], ys, tolerance);
        return (horizontal.Move, vertical.Move, horizontal.Target, vertical.Target);
    }

    /// <summary>The smallest move that puts one of <paramref name="from"/> onto one of <paramref name="to"/>.</summary>
    private static (double Move, double? Target) Shift(IReadOnlyList<double> from, IReadOnlyList<double> to,
        double tolerance)
    {
        (double Move, double? Target)? best = null;
        foreach (var value in from)
        {
            foreach (var target in to)
            {
                var move = target - value;
                if (Math.Abs(move) > tolerance) continue;
                if (best is { } current && Math.Abs(current.Move) <= Math.Abs(move)) continue;
                best = (move, target);
            }
        }
        return best is { } found ? (found.Move, found.Target) : (0, null);
    }
}
