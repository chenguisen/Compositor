import io

# ---------------------------------------------------------------- CanvasView: the move drag is the box now
path = r"windows/src/Compositor.Desktop/CanvasView.cs"
s = io.open(path, encoding="utf-8").read()

def swap(old, new, where=path):
    global s
    assert old in s, "missing in " + where + ":\n" + old
    s = s.replace(old, new, 1)

swap("""    /// <summary>When set, dragging moves the selected layer instead of panning.</summary>
    public bool MoveEnabled { get; set; }

""", "")

swap("""    /// <summary>A move gesture, so the whole drag is one undo step: the app opens an edit on
    /// <see cref="MoveStarted"/>, applies each delta, and closes it on <see cref="MoveFinished"/>.</summary>
    public Action? MoveStarted { get; set; }

    public Action<SKPoint>? MoveChanged { get; set; }

    public Action? MoveFinished { get; set; }

    private SKPoint _moveAnchor;
    private bool _moving;

""", "")

swap("""        if (MoveEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _moving = true;
            _moveAnchor = ToDocument(e.GetPosition(this));
            e.Pointer.Capture(this);
            MoveStarted?.Invoke();
            e.Handled = true;
            return;
        }
""", "")

swap("""        if (_moving)
        {
            var point = ToDocument(now);
            var delta = new SKPoint(point.X - _moveAnchor.X, point.Y - _moveAnchor.Y);
            _moveAnchor = point;
            if (delta.X != 0 || delta.Y != 0) MoveChanged?.Invoke(delta);
            base.OnPointerMoved(e);
            return;
        }
""", "")

swap("""        if (_moving)
        {
            _moving = false;
            e.Pointer.Capture(null);
            MoveFinished?.Invoke();
            return;
        }
""", "")

io.open(path, "w", encoding="utf-8", newline="\n").write(s)

# ---------------------------------------------------------------- MainWindow: drop the move handlers
path = r"windows/src/Compositor.Desktop/MainWindow.cs"
s = io.open(path, encoding="utf-8").read()

swap("""        _canvas.PaintEnabled = tool == Tool.Brush;
        _canvas.MoveEnabled = tool == Tool.Move;""",
     """        _canvas.PaintEnabled = tool == Tool.Brush;""", path)

swap("""        _canvas.MoveStarted = MoveStarted;
        _canvas.MoveChanged = MoveChanged;
        _canvas.MoveFinished = MoveFinished;
""", "", path)

swap("""    private void MoveFinished()
    {
        if (_document is not { } document || _moving is not { } id) return;
        _moving = null;
        _history.End(document, id);
        Refresh();
    }

""", "", path)

swap("""    /// <summary>The layer a move gesture is moving, while the pointer is down.</summary>
    private Guid? _moving;

""", "", path)

io.open(path, "w", encoding="utf-8", newline="\n").write(s)
print("patched")
