import io

# ---------------------------------------------------------------- CanvasView: the crop frame
path = r"windows/src/Compositor.Desktop/CanvasView.cs"
s = io.open(path, encoding="utf-8").read()

def swap(old, new, where=path):
    global s
    assert old in s, "missing in " + where + ":\n" + old
    s = s.replace(old, new, 1)

swap("""    /// <summary>When set, clicking reports where a new text layer should go.</summary>
    public bool TypeOnClick { get; set; }""",
"""    /// <summary>When set, the crop frame below is drawn and can be dragged about.</summary>
    public bool CropEnabled { get; set; }

    /// <summary>The crop frame, in document pixels; null leaves the whole canvas as the frame.</summary>
    public SKRectI? CropBox { get; set; }

    /// <summary>The ratio a crop drag is held to, or null for a free frame; the app sets it.</summary>
    public double? CropRatio { get; set; }

    /// <summary>The frame a crop drag has worked out, with where the pointer is, for the app to snap.</summary>
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
    public bool TypeOnClick { get; set; }""")

swap("""        if (TypeOnClick && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)""",
"""        if (CropEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
        if (TypeOnClick && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)""")

swap("""        if (_dragOriginal.IsValid && TransformBox is not null && _transformDragging)""",
"""        if (_cropDragging is not CropDrag.None)
        {
            var point = ToDocument(now);
            var symmetric = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var frame = _cropDragging switch
            {
                CropDrag.Create => CropEdits.Create(_cropStart, point, CropRatio, symmetric),
                CropDrag.Move => CropEdits.Move(_cropOriginal, _cropStart, point),
                _ => _cropHandle is { } grabbed
                    ? CropEdits.Resize(_cropOriginal, grabbed, _cropStart, point, CropRatio, symmetric)
                    : _cropOriginal,
            };
            CropChanged?.Invoke(frame, point);
            base.OnPointerMoved(e);
            return;
        }
        if (_dragOriginal.IsValid && TransformBox is not null && _transformDragging)""")

swap("""        if (_transformDragging)
        {
            _transformDragging = false;""",
"""        if (_cropDragging is not CropDrag.None)
        {
            _cropDragging = CropDrag.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }
        if (_transformDragging)
        {
            _transformDragging = false;""")

swap("""    /// <summary>Lets go of an outline that is being drawn, as Escape does in the Mac build.</summary>""",
"""    /// <summary>Whether a crop drag is under way, so the tool is not reset under it.</summary>
    public bool CropDragging => _cropDragging is not CropDrag.None;

    /// <summary>Whether the crop frame is being moved whole rather than by an edge, which snaps differently.</summary>
    public bool CropMoving => _cropDragging == CropDrag.Move;

    private SKRectI WholeCanvas() => _document is { } document
        ? SKRectI.Create(0, 0, document.Width, document.Height)
        : SKRectI.Create(0, 0, 1, 1);

    /// <summary>Whether a document point is inside a frame, which is what tells a move from a new frame.</summary>
    private static bool Inside(SKRectI frame, SKPoint point) =>
        point.X >= frame.Left && point.X < frame.Right && point.Y >= frame.Top && point.Y < frame.Bottom;

    /// <summary>Lets go of an outline that is being drawn, as Escape does in the Mac build.</summary>""")

swap("""        DrawDraft(context, pen);
        DrawTransform(context);""",
"""        DrawDraft(context, pen);
        DrawCrop(context);
        DrawTransform(context);""")

swap("""    /// <summary>The transform box: its outline, its eight handles, the grip that turns it, and the lines a
    /// drag has snapped to.</summary>""",
"""    /// <summary>
    /// The crop frame: what will be kept is left clear, what will go is dimmed, and the eight handles say
    /// where it can be dragged.
    /// </summary>
    private void DrawCrop(DrawingContext context)
    {
        if (!CropEnabled || _document is null) return;
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
    /// drag has snapped to.</summary>""")

swap("""    /// <summary>A line a drag has snapped to: cyan, as Photoshop shows them.</summary>""",
"""    /// <summary>What a crop is about to take away.</summary>
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

    /// <summary>A line a drag has snapped to: cyan, as Photoshop shows them.</summary>""")

io.open(path, "w", encoding="utf-8", newline="\n").write(s)

# ---------------------------------------------------------------- TransformEdits: handles without the grip
path = r"windows/src/Compositor.Core/Document/TransformEdits.cs"
s = io.open(path, encoding="utf-8").read()

swap("""    /// <summary>
    /// Which handle a click takes hold of, within <paramref name="tolerance"/> document pixels: a handle
    /// first, then the edge between two of them — which is that edge's middle handle, so the whole side can
    /// be dragged.
    /// </summary>
    public static TransformHandle? HandleAt(LayerTransform box, SKPoint point, double tolerance, double grip)
    {
        if (Near(point, RotatePosition(box, grip), tolerance)) return TransformHandle.Rotate;""",
"""    /// <summary>
    /// Which of the eight handles a click takes hold of, ignoring the turning grip — for a frame that is
    /// dragged about but not turned, as a crop is.
    /// </summary>
    public static TransformHandle? HandleAt(LayerTransform box, SKPoint point, double tolerance)
    {
        var found = HandleAt(box, point, tolerance, double.NaN);
        return found == TransformHandle.Rotate ? null : found;
    }

    /// <summary>
    /// Which handle a click takes hold of, within <paramref name="tolerance"/> document pixels: a handle
    /// first, then the edge between two of them — which is that edge's middle handle, so the whole side can
    /// be dragged.
    /// </summary>
    public static TransformHandle? HandleAt(LayerTransform box, SKPoint point, double tolerance, double grip)
    {
        if (!double.IsNaN(grip) && Near(point, RotatePosition(box, grip), tolerance)) return TransformHandle.Rotate;""",
      path)

io.open(path, "w", encoding="utf-8", newline="\n").write(s)
print("patched canvas and transform")
