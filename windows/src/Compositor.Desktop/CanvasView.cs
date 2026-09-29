using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using SelectionMode = Compositor.Core.Document.SelectionMode;

namespace Compositor.Desktop;

/// <summary>The selection tools the pointer can hold: one shape each, plus the wand's single click.</summary>
public enum SelectionTool
{
    None,
    Rectangle,
    Ellipse,
    Lasso,
    Polygon,
    Wand,
}

/// <summary>
/// The document on screen. It composites only the part of the canvas it is showing, through
/// <see cref="DocumentRenderer.RenderRegion"/>, which is what lets a canvas far bigger than one buffer
/// still be looked at.
/// </summary>
public sealed class CanvasView : Control
{
    /// <summary>
    /// How many document pixels one screenful may composite. Zooming out past this cannot be done at 1:1,
    /// so the view stops there rather than asking for a buffer the machine will not give it.
    /// </summary>
    private const long ViewportPixelLimit = 32L * 1024 * 1024;

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x1E));
    private static readonly IBrush Paper = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x33));

    /// <summary>The transform box: solid white, so it reads against any picture.</summary>
    private static readonly Pen TransformPen = new() { Brush = Brushes.White, Thickness = 1 };

    /// <summary>What a crop is about to take away.</summary>
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

    /// <summary>A line a drag has snapped to: cyan, as Photoshop shows them.</summary>
    private static readonly Pen SnapPen = new() { Brush = Brushes.Cyan, Thickness = 1 };

    private CanvasDocument? _document;
    private SKPoint _origin;
    private double _zoom = 1;
    private Point? _dragging;

    /// <summary>The stroke being drawn, in document pixels, until the pointer comes back up.</summary>
    private readonly List<SKPoint> _stroke = [];
    private bool _painting;

    /// <summary>When set, dragging paints instead of panning.</summary>
    public bool PaintEnabled { get; set; }

    /// <summary>Which selection tool the pointer is holding; None leaves it panning.</summary>
    public SelectionTool Selection { get; set; }

    /// <summary>Handed the box a marquee drag ended on, in document pixels, and how it meets the selection.</summary>
    public Action<SKRectI, SelectionMode>? MarqueeFinished { get; set; }

    /// <summary>Handed the outline a lasso or a polygonal lasso closed, in document pixels.</summary>
    public Action<IReadOnlyList<SKPoint>, SelectionMode>? LassoFinished { get; set; }

    /// <summary>Handed a click of the wand, in document pixels.</summary>
    public Action<SKPoint, SelectionMode>? WandClicked { get; set; }

    /// <summary>When set, Alt-clicking reports where a Clone Stamp stroke should copy from.</summary>
    public bool SampleSourceOnClick { get; set; }

    /// <summary>Handed the point an Alt-click landed on, in document pixels.</summary>
    public Action<SKPoint>? CloneSourceClicked { get; set; }

    /// <summary>When set, clicking reports the colour under the pointer instead of painting.</summary>
    public bool EyedropperOnClick { get; set; }

    /// <summary>Handed the point the eyedropper was clicked at, in document pixels.</summary>
    public Action<SKPoint>? EyedropperClicked { get; set; }

    /// <summary>When set, the crop frame below is drawn and can be dragged about.</summary>
    public bool CropEnabled { get; set; }

    /// <summary>The crop frame, in document pixels; null leaves the whole canvas as the frame.</summary>
    public SKRectI? CropBox { get; set; }

    /// <summary>The frame a crop drag has worked out, and where the pointer is, for the app to snap.</summary>
    public Action<SKRectI, SKPoint>? CropChanged { get; set; }

    /// <summary>A double-click inside the frame asks for the crop to be made.</summary>
    public Action? CropCommitted { get; set; }

    private enum CropDrag
    {
        None,
        Create,
        Move,
        Resize,
    }

    private CropDrag _cropDragging;
    private TransformHandle? _cropHandle;
    private SKRectI _cropOriginal;
    private SKPoint _cropStart;

    /// <summary>When set, clicking reports where a new text layer should go.</summary>
    public bool TypeOnClick { get; set; }

    /// <summary>Handed the point the Type tool was clicked at, in document pixels.</summary>
    public Action<SKPoint>? TextClicked { get; set; }

    /// <summary>When set, the box below is drawn with its handles and can be dragged about.</summary>
    public bool TransformEnabled { get; set; }

    /// <summary>The box the transform handles sit around, in document pixels.</summary>
    public LayerTransform? TransformBox { get; set; }

    /// <summary>Whether a scale drag keeps the sides in proportion unless Shift says otherwise.</summary>
    public bool TransformLockRatio { get; set; }

    /// <summary>The pointer took hold of the box: the whole drag is one undo step.</summary>
    public Action? TransformStarted { get; set; }

    /// <summary>The box a drag has worked out, for the app to put on the layer.</summary>
    public Action<LayerTransform>? TransformChanged { get; set; }

    public Action? TransformFinished { get; set; }

    /// <summary>Lines to draw along while a drag is snapped to something.</summary>
    public (double? X, double? Y) SnapLines { get; set; }

    private TransformHandle? _handle;
    private LayerTransform _dragOriginal;
    private SKPoint _dragStart;

    private bool _transformDragging;
    private bool _selecting;
    private SKPoint _selectionAnchor;
    private SKRectI? _selectionBox;

    /// <summary>The outline being dragged or clicked out right now, in document pixels.</summary>
    private readonly List<SKPoint> _lasso = [];

    /// <summary>Where the pointer is, so an open polygonal lasso can show the line it would add.</summary>
    private SKPoint? _lassoPointer;
    private SelectionMode _draftMode = SelectionMode.Replace;

    public BrushSettings Brush { get; set; } = new();

    /// <summary>Handed the finished stroke, in document pixels.</summary>
    public Action<IReadOnlyList<SKPoint>>? StrokeFinished { get; set; }

    public CanvasView()
    {
        ClipToBounds = true;
    }

    public CanvasDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            Fit();
        }
    }

    public double Zoom => _zoom;

    /// <summary>Whether the view had to stop zooming out because one screenful would be too big to draw.</summary>
    public bool ZoomedOutAsFarAsItGoes { get; private set; }

    /// <summary>Shows the whole canvas, as far out as one screenful may composite.</summary>
    public void Fit()
    {
        if (_document is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var across = Bounds.Width / _document.Width;
        var down = Bounds.Height / _document.Height;
        SetZoom(Math.Min(across, down));
        _origin = new SKPoint(
            (float)((_document.Width - Bounds.Width / _zoom) / 2),
            (float)((_document.Height - Bounds.Height / _zoom) / 2));
        InvalidateVisual();
    }

    public void ActualSize()
    {
        var centre = _document is null
            ? new SKPoint()
            : new SKPoint(_origin.X + (float)(Bounds.Width / _zoom / 2), _origin.Y + (float)(Bounds.Height / _zoom / 2));
        SetZoom(1);
        _origin = new SKPoint((float)(centre.X - Bounds.Width / 2), (float)(centre.Y - Bounds.Height / 2));
        InvalidateVisual();
    }

    public void ZoomBy(double factor)
    {
        var anchor = new SKPoint(_origin.X + (float)(Bounds.Width / _zoom / 2), _origin.Y + (float)(Bounds.Height / _zoom / 2));
        var before = anchor;
        SetZoom(_zoom * factor);
        // Keep the same document point under the middle of the view.
        _origin = new SKPoint((float)(before.X - Bounds.Width / _zoom / 2), (float)(before.Y - Bounds.Height / _zoom / 2));
        InvalidateVisual();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.01, 32);
        if (_document is null) return;
        // One screenful must not ask for more than the limit; stop zooming out when it would.
        var needed = (long)(Bounds.Width / _zoom) * (long)(Bounds.Height / _zoom);
        ZoomedOutAsFarAsItGoes = false;
        if (needed > ViewportPixelLimit)
        {
            var allowed = Math.Sqrt(Bounds.Width * (double)Bounds.Height / ViewportPixelLimit);
            _zoom = Math.Max(_zoom, allowed);
            ZoomedOutAsFarAsItGoes = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.DrawRectangle(Backdrop, null, new Rect(0, 0, size.Width, size.Height));
        if (_document is not { } document) return;

        var left = (int)Math.Floor(_origin.X);
        var top = (int)Math.Floor(_origin.Y);
        var right = (int)Math.Ceiling(_origin.X + size.Width / _zoom);
        var bottom = (int)Math.Ceiling(_origin.Y + size.Height / _zoom);
        var region = SKRectI.Intersect(SKRectI.Create(left, top, right - left, bottom - top),
            SKRectI.Create(0, 0, document.Width, document.Height));
        if (region.Width <= 0 || region.Height <= 0) return;

        using var rendered = DocumentRenderer.RenderRegion(document, region);
        using var image = ToImage(rendered);
        var destination = new Rect(
            (region.Left - _origin.X) * _zoom,
            (region.Top - _origin.Y) * _zoom,
            region.Width * _zoom,
            region.Height * _zoom);
        context.DrawRectangle(Paper, null, destination);
        context.DrawImage(image, destination);
        DrawSelection(context);
        DrawStroke(context);
    }

    /// <summary>Whether a crop drag is under way, so the tool is not reset under it.</summary>
    public bool CropDragging => _cropDragging is not CropDrag.None;

    /// <summary>Whether the crop frame is being moved whole rather than by an edge, which snaps differently.</summary>
    public bool CropMoving => _cropDragging == CropDrag.Move;

    /// <summary>The ratio a crop drag is held to, or null for a free frame; the app sets it.</summary>
    public double? CropRatio { get; set; }

    private SKRectI WholeCanvas() => _document is { } document
        ? SKRectI.Create(0, 0, document.Width, document.Height)
        : SKRectI.Create(0, 0, 1, 1);

    /// <summary>Whether a document point is inside a frame, which is what tells a move from a new frame.</summary>
    private static bool Inside(SKRectI frame, SKPoint point) =>
        point.X >= frame.Left && point.X < frame.Right && point.Y >= frame.Top && point.Y < frame.Bottom;

    /// <summary>Lets go of an outline that is being drawn, as Escape does in the Mac build.</summary>
    public void CancelDraft()
    {
        _selecting = false;
        ClearDraft();
        InvalidateVisual();
    }

    /// <summary>
    /// Takes hold of a handle, or of the box itself, if the click landed on one. The drag is measured from
    /// where it began rather than compounded, so the app can hold the box it started with.
    /// </summary>
    private bool beginTransformDrag(LayerTransform box, SKPoint point, KeyModifiers modifiers)
    {
        var handle = TransformEdits.HandleAt(box, point, TransformEdits.Grab / _zoom, TransformEdits.RotateGrip / _zoom);
        if (handle is null && !box.Contains(point)) return false;
        _transformDragging = true;
        _handle = handle;
        _dragOriginal = box;
        _dragStart = point;
        TransformStarted?.Invoke();
        return true;
    }

    /// <summary>Closes an open polygonal lasso and hands it to the app.</summary>
    private void CompleteSelection()
    {
        var points = _lasso.ToList();
        var mode = _draftMode;
        ClearDraft();
        InvalidateVisual();
        LassoFinished?.Invoke(points, mode);
    }

    private void ClearDraft()
    {
        _lasso.Clear();
        _lassoPointer = null;
        _selectionBox = null;
        _draftMode = SelectionMode.Replace;
    }

    /// <summary>Whether a new sample is far enough from the last to be worth keeping, in document pixels.</summary>
    private bool Moved(SKPoint point) =>
        _lasso.Count == 0 || Math.Abs(point.X - _lasso[^1].X) + Math.Abs(point.Y - _lasso[^1].Y) >= 0.5f;

    /// <summary>Whether a click is on the point a polygon started from, within eight screen pixels.</summary>
    private bool Near(Point screen, SKPoint document)
    {
        var start = ToScreen(document);
        return Math.Abs(screen.X - start.X) <= 8 && Math.Abs(screen.Y - start.Y) <= 8;
    }

    /// <summary>
    /// How a new shape meets the selection already there: Option takes away, Shift adds, otherwise it
    /// replaces, as the Mac build reads the modifiers.
    /// </summary>
    private static SelectionMode ModeOf(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Alt) ? SelectionMode.Subtract
        : modifiers.HasFlag(KeyModifiers.Shift) ? SelectionMode.Add
        : SelectionMode.Replace;

    /// <summary>The outline of what is selected, and of the shape being dragged or clicked out.</summary>
    private void DrawSelection(DrawingContext context)
    {
        var pen = new Pen
        {
            Brush = Brushes.White,
            Thickness = 1,
            DashStyle = new DashStyle([4.0, 4.0], 0),
        };
        DrawDraft(context, pen);
        DrawCrop(context);
        DrawTransform(context);
        if (_document?.Selection.Path is not { } path || path.IsEmpty) return;
        foreach (var contour in Contours(path))
        {
            for (var index = 1; index < contour.Count; index++)
            {
                context.DrawLine(pen, ToScreen(contour[index - 1]), ToScreen(contour[index]));
            }
        }
    }

    /// <summary>
    /// The crop frame: what will be kept is left clear, what will go is dimmed, and the eight handles say
    /// where it can be dragged.
    /// </summary>
    private void DrawCrop(DrawingContext context)
    {
        if (!CropEnabled || _document is not { } document) return;
        var frame = CropBox ?? WholeCanvas();
        var pen = new Pen { Brush = Brushes.White, Thickness = 1 };
        var corner = ToScreen(new SKPoint(frame.Left, frame.Top));
        var right = corner.X + frame.Width * _zoom;
        var bottom = corner.Y + frame.Height * _zoom;
        // What goes is dimmed, in four bands around what stays.
        var canvas = new Rect(0, 0, Bounds.Width, Bounds.Height);
        foreach (var band in new[]
                 {
                     new Rect(canvas.X, canvas.Y, canvas.Width, Math.Max(0, corner.Y - canvas.Y)),
                     new Rect(canvas.X, bottom, canvas.Width, Math.Max(0, canvas.Bottom - bottom)),
                     new Rect(canvas.X, corner.Y, Math.Max(0, corner.X - canvas.X), Math.Max(0, bottom - corner.Y)),
                     new Rect(right, corner.Y, Math.Max(0, canvas.Right - right), Math.Max(0, bottom - corner.Y)),
                 })
        {
            context.FillRectangle(DimBrush, band);
        }
        context.DrawRectangle(null, pen, new Rect(corner.X, corner.Y, frame.Width * _zoom, frame.Height * _zoom));
        foreach (var handle in new[]
                 {
                     TransformHandle.TopLeft, TransformHandle.Top, TransformHandle.TopRight, TransformHandle.Right,
                     TransformHandle.BottomRight, TransformHandle.Bottom, TransformHandle.BottomLeft, TransformHandle.Left,
                 })
        {
            var at = ToScreen(TransformEdits.Position(CropEdits.Box(frame), handle));
            context.DrawRectangle(null, pen, new Rect(at.X - 3, at.Y - 3, 6, 6));
        }
    }

    /// <summary>The transform box: its outline, its eight handles, the grip that turns it, and the lines a
    /// drag has snapped to.</summary>
    private void DrawTransform(DrawingContext context)
    {
        if (SnapLines is ({ } lineX, _))
        {
            var top = ToScreen(new SKPoint((float)lineX, 0));
            context.DrawLine(SnapPen, top, new Point(top.X, Bounds.Height));
        }
        if (SnapLines is (_, { } lineY))
        {
            var left = ToScreen(new SKPoint(0, (float)lineY));
            context.DrawLine(SnapPen, left, new Point(Bounds.Width, left.Y));
        }
        if (!TransformEnabled || TransformBox is not { } box) return;
        var corners = TransformEdits.Corners(box);
        for (var index = 0; index < corners.Length; index++)
        {
            context.DrawLine(TransformPen, ToScreen(corners[index]),
                ToScreen(corners[(index + 1) % corners.Length]));
        }
        foreach (var handle in Enum.GetValues<TransformHandle>())
        {
            var at = handle == TransformHandle.Rotate
                ? TransformEdits.RotatePosition(box, TransformEdits.RotateGrip / _zoom)
                : TransformEdits.Position(box, handle);
            var centre = ToScreen(at);
            context.DrawRectangle(null, TransformPen,
                new Rect(centre.X - 3, centre.Y - 3, 6, 6));
        }
    }

    /// <summary>The shape being drawn right now, before it becomes a selection.</summary>
    private void DrawDraft(DrawingContext context, Pen pen)
    {
        if (Selection == SelectionTool.Polygon)
        {
            if (_lasso.Count > 0) DrawPolyline(context, pen, _lasso, _lassoPointer);
            return;
        }
        if (!_selecting) return;
        if (Selection == SelectionTool.Lasso)
        {
            DrawPolyline(context, pen, _lasso, null);
            return;
        }
        if (_selectionBox is not { } box || box.Width <= 0 || box.Height <= 0) return;
        var corner = ToScreen(new SKPoint(box.Left, box.Top));
        var width = box.Width * _zoom;
        var height = box.Height * _zoom;
        if (Selection == SelectionTool.Ellipse)
        {
            context.DrawEllipse(null, pen, new Point(corner.X + width / 2, corner.Y + height / 2), width / 2, height / 2);
            return;
        }
        context.DrawRectangle(null, pen, new Rect(corner.X, corner.Y, width, height));
    }

    private void DrawPolyline(DrawingContext context, Pen pen, List<SKPoint> points, SKPoint? to)
    {
        for (var index = 1; index < points.Count; index++)
        {
            context.DrawLine(pen, ToScreen(points[index - 1]), ToScreen(points[index]));
        }
        if (to is { } last && points.Count > 0) context.DrawLine(pen, ToScreen(points[^1]), ToScreen(last));
    }

    /// <summary>
    /// An outline as polylines: Skia measures the path and hands back points along it, about two screen
    /// pixels apart, so a lasso or an ellipse is drawn as closely as the screen can show it.
    /// </summary>
    private List<List<SKPoint>> Contours(SKPath path)
    {
        var step = (float)Math.Max(0.25, 2 / Math.Max(_zoom, 0.01));
        var contours = new List<List<SKPoint>>();
        using var measure = new SKPathMeasure();
        measure.SetPath(path, forceClosed: true);
        do
        {
            var length = measure.Length;
            if (length <= 0) continue;
            var points = new List<SKPoint> { measure.GetPosition(0) };
            for (var at = step; at < length && points.Count < 20_000; at += step)
            {
                points.Add(measure.GetPosition(at));
            }
            points.Add(measure.GetPosition(length));
            contours.Add(points);
        }
        while (measure.NextContour());
        return contours;
    }

    /// <summary>The whole pixels between two points, whichever way round they are.</summary>
    private static SKRectI Between(SKPoint from, SKPoint to)
    {
        var left = (int)Math.Floor(Math.Min(from.X, to.X));
        var top = (int)Math.Floor(Math.Min(from.Y, to.Y));
        var right = (int)Math.Ceiling(Math.Max(from.X, to.X));
        var bottom = (int)Math.Ceiling(Math.Max(from.Y, to.Y));
        return SKRectI.Create(left, top, right - left, bottom - top);
    }

    /// <summary>The stroke so far, drawn as a line of the brush's width while the pointer is down.</summary>
    private void DrawStroke(DrawingContext context)
    {
        if (!_painting || _stroke.Count < 2) return;
        var colour = Color.FromArgb(170, (byte)(Brush.Red * 255), (byte)(Brush.Green * 255), (byte)(Brush.Blue * 255));
        var pen = new Pen(new SolidColorBrush(colour), Math.Max(1, Brush.Diameter * _zoom), lineCap: PenLineCap.Round);
        for (var index = 1; index < _stroke.Count; index++)
        {
            context.DrawLine(pen, ToScreen(_stroke[index - 1]), ToScreen(_stroke[index]));
        }
    }

    private Point ToScreen(SKPoint document) =>
        new((document.X - _origin.X) * _zoom, (document.Y - _origin.Y) * _zoom);

    private SKPoint ToDocument(Point screen) =>
        new((float)(_origin.X + screen.X / _zoom), (float)(_origin.Y + screen.Y / _zoom));

    /// <summary>A copy of a rendered piece in the order the screen wants: blue before red, premultiplied.</summary>
    private static WriteableBitmap ToImage(SKBitmap source)
    {
        var target = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = target.Lock())
        {
            var pixels = source.GetPixelSpan();
            unsafe
            {
                var start = (byte*)locked.Address;
                for (var y = 0; y < source.Height; y++)
                {
                    var from = pixels.Slice(y * source.Width * 4, source.Width * 4);
                    var to = new Span<byte>(start + y * locked.RowBytes, source.Width * 4);
                    for (var x = 0; x < source.Width; x++)
                    {
                        to[x * 4] = from[x * 4 + 2];
                        to[x * 4 + 1] = from[x * 4 + 1];
                        to[x * 4 + 2] = from[x * 4];
                        to[x * 4 + 3] = from[x * 4 + 3];
                    }
                }
            }
        }
        return target;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (TransformEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && TransformBox is { } box && beginTransformDrag(box, ToDocument(e.GetPosition(this)), e.KeyModifiers))
        {
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (CropEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var point = ToDocument(e.GetPosition(this));
            var frame = CropBox ?? WholeCanvas();
            if (e.ClickCount > 1 && Inside(frame, point))
            {
                e.Handled = true;
                CropCommitted?.Invoke();
                return;
            }
            _cropStart = point;
            _cropOriginal = frame;
            _cropHandle = null;
            if (TransformEdits.HandleAt(CropEdits.Box(frame), point, TransformEdits.Grab / _zoom) is { } handle)
            {
                _cropHandle = handle;
                _cropDragging = CropDrag.Resize;
            }
            else if (Inside(frame, point))
            {
                _cropDragging = CropDrag.Move;
            }
            else
            {
                _cropDragging = CropDrag.Create;
            }
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (TypeOnClick && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            TextClicked?.Invoke(ToDocument(e.GetPosition(this)));
            return;
        }
        if (EyedropperOnClick && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            EyedropperClicked?.Invoke(ToDocument(e.GetPosition(this)));
            return;
        }
        if (SampleSourceOnClick && e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            CloneSourceClicked?.Invoke(ToDocument(e.GetPosition(this)));
            return;
        }
        if (PaintEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _painting = true;
            _stroke.Clear();
            _stroke.Add(ToDocument(e.GetPosition(this)));
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (Selection != SelectionTool.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var point = ToDocument(e.GetPosition(this));
            if (Selection == SelectionTool.Wand)
            {
                e.Handled = true;
                WandClicked?.Invoke(point, ModeOf(e.KeyModifiers));
                return;
            }
            if (Selection == SelectionTool.Polygon)
            {
                // The mode is fixed when the first point goes down, as the Mac build fixes it.
                if (_lasso.Count == 0) _draftMode = ModeOf(e.KeyModifiers);
                var closes = e.ClickCount > 1
                    || (_lasso.Count >= 3 && Near(e.GetPosition(this), _lasso[0]));
                _lasso.Add(point);
                _lassoPointer = point;
                e.Handled = true;
                if (closes) CompleteSelection();
                else InvalidateVisual();
                return;
            }
            _selecting = true;
            _draftMode = ModeOf(e.KeyModifiers);
            _selectionAnchor = point;
            _selectionBox = null;
            _lasso.Clear();
            _lasso.Add(point);
            _lassoPointer = point;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        _dragging = e.GetPosition(this);
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var now = e.GetPosition(this);
        if (_painting)
        {
            var point = ToDocument(now);
            // Samples arrive thick and fast; only a real move is worth another dab.
            if (_stroke.Count == 0 || Math.Abs(point.X - _stroke[^1].X) + Math.Abs(point.Y - _stroke[^1].Y) >= 0.5f)
            {
                _stroke.Add(point);
                InvalidateVisual();
            }
            base.OnPointerMoved(e);
            return;
        }
        if (_cropDragging is not CropDrag.None)
        {
            var point = ToDocument(now);
            var ratio = CropRatio;
            var symmetric = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var frame = _cropDragging switch
            {
                CropDrag.Create => CropEdits.Create(_cropStart, point, ratio, symmetric),
                CropDrag.Move => CropEdits.Move(_cropOriginal, _cropStart, point),
                _ => _cropHandle is { } grabbed
                    ? CropEdits.Resize(_cropOriginal, grabbed, _cropStart, point, ratio, symmetric)
                    : _cropOriginal,
            };
            CropChanged?.Invoke(frame, point);
            base.OnPointerMoved(e);
            return;
        }
        if (_dragOriginal.IsValid && TransformBox is not null && _transformDragging)
        {
            var point = ToDocument(now);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var option = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var draft = _handle switch
            {
                TransformHandle.Rotate => TransformEdits.Rotate(_dragOriginal, _dragStart, point, steps: shift),
                null => TransformEdits.Move(_dragOriginal, point.X - _dragStart.X, point.Y - _dragStart.Y, axisLock: shift),
                var handle => TransformEdits.Resize(_dragOriginal, handle.Value, _dragStart, point,
                    lockRatio: TransformLockRatio != shift, fromCentre: option),
            };
            TransformChanged?.Invoke(draft);
            base.OnPointerMoved(e);
            return;
        }
        if (_selecting)
        {
            var point = ToDocument(now);
            if (Selection == SelectionTool.Lasso && Moved(point)) _lasso.Add(point);
            if (Selection is SelectionTool.Rectangle or SelectionTool.Ellipse)
            {
                _selectionBox = Between(_selectionAnchor, point);
            }
            _lassoPointer = point;
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
        if (Selection == SelectionTool.Polygon && _lasso.Count > 0)
        {
            _lassoPointer = ToDocument(now);
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
        if (_dragging is { } last)
        {
            _origin = new SKPoint(
                (float)(_origin.X - (now.X - last.X) / _zoom),
                (float)(_origin.Y - (now.Y - last.Y) / _zoom));
            _dragging = now;
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_painting)
        {
            _painting = false;
            var stroke = _stroke.ToList();
            _stroke.Clear();
            InvalidateVisual();
            if (stroke.Count > 0) StrokeFinished?.Invoke(stroke);
            e.Pointer.Capture(null);
            return;
        }
        if (_cropDragging is not CropDrag.None)
        {
            _cropDragging = CropDrag.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }
        if (_transformDragging)
        {
            _transformDragging = false;
            _handle = null;
            SnapLines = (null, null);
            e.Pointer.Capture(null);
            InvalidateVisual();
            TransformFinished?.Invoke();
            return;
        }
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            var mode = _draftMode;
            if (Selection == SelectionTool.Lasso)
            {
                var points = _lasso.ToList();
                ClearDraft();
                InvalidateVisual();
                LassoFinished?.Invoke(points, mode);
                return;
            }
            if (_selectionBox is { } box)
            {
                ClearDraft();
                InvalidateVisual();
                MarqueeFinished?.Invoke(box, mode);
                return;
            }
            ClearDraft();
            InvalidateVisual();
            return;
        }
        _dragging = null;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }
}
