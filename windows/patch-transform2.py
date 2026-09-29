import io

path = r"windows/src/Compositor.Desktop/MainWindow.cs"
s = io.open(path, encoding="utf-8").read()

def swap(old, new):
    global s
    assert old in s, "missing in " + path + ":\n" + old
    s = s.replace(old, new, 1)

swap("""        _canvas.EyedropperClicked = Picked;""",
"""        _canvas.EyedropperClicked = Picked;
        _canvas.TransformStarted = TransformStarted;
        _canvas.TransformChanged = TransformChanged;
        _canvas.TransformFinished = TransformFinished;""")

swap("""        _canvas.SampleSourceOnClick = tool == Tool.Clone;
        _canvas.EyedropperOnClick = tool == Tool.Eyedropper;""",
"""        _canvas.SampleSourceOnClick = tool == Tool.Clone;
        _canvas.EyedropperOnClick = tool == Tool.Eyedropper;
        _canvas.TransformEnabled = tool == Tool.Move;
        ShowTransformBox();""")

swap("""            Tool.Move => "Move — drag the selected layer",""",
"""            Tool.Move => "Move — drag the layer, or a handle to scale and turn it",""")

# ---------------------------------------------------------------- the transform drag
swap("""    /// <summary>Repaints the canvas and says where the history stands.</summary>""",
"""    /// <summary>
    /// The transform handles follow the selected layer: the tool shows its box while nothing is being
    /// dragged, and nothing at all when there is no layer with pixels to transform.
    /// </summary>
    private void ShowTransformBox()
    {
        if (_tool != Tool.Move || _document is not { } document || Selected is not { } id)
        {
            _canvas.TransformBox = null;
            return;
        }
        _canvas.TransformBox = document.Layers.FirstOrDefault(layer => layer.ID == id) is { IsGroup: false, Asset: not null } layer
            ? layer.Transform
            : null;
    }

    /// <summary>The pointer took hold of the box: the whole drag is one step in the history.</summary>
    private void TransformStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _transforming = id;
        _history.Begin("Transform", document, id);
    }

    /// <summary>
    /// A drag under way: the box the handles worked out goes on the layer, snapped to whatever is nearby,
    /// and the lines it snapped to are drawn along.
    /// </summary>
    private void TransformChanged(LayerTransform draft)
    {
        if (_document is not { } document || _transforming is not { } id) return;
        var tolerance = TransformSnap.Distance / Math.Max(_canvas.Zoom, 0.0001);
        var placed = TransformEdits.Snap(document, draft, [id], tolerance, out var lineX, out var lineY);
        _canvas.SnapLines = (lineX, lineY);
        LayerEdits.SetTransform(document, id, placed);
        _canvas.TransformBox = placed;
        Refresh();
    }

    private void TransformFinished()
    {
        if (_document is not { } document || _transforming is not { } id) return;
        _transforming = null;
        _canvas.SnapLines = (null, null);
        _history.End(document, id);
        ShowTransformBox();
        Refresh();
    }

    /// <summary>Repaints the canvas and says where the history stands.</summary>""")

swap("""    private void Refresh()
    {
        _canvas.InvalidateVisual();
        UpdateLayerMenu();""",
"""    private void Refresh()
    {
        _canvas.InvalidateVisual();
        UpdateLayerMenu();
        if (_transforming is null) ShowTransformBox();""")

swap("""    /// <summary>The layer a move gesture is moving, while the pointer is down.</summary>
    private Guid? _moving;""",
"""    /// <summary>The layer a move gesture is moving, while the pointer is down.</summary>
    private Guid? _moving;

    /// <summary>The layer a transform drag is editing, while the pointer is down.</summary>
    private Guid? _transforming;""")

# The old move-by-drag path is what the transform handles now do.
swap("""    private void MoveStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _moving = id;
        _history.Begin("Move", document, id);
    }

    private void MoveChanged(SKPoint delta)
    {
        if (_document is not { } document || _moving is not { } id) return;
        LayerEdits.Move(document, id, delta.X, delta.Y);
        Refresh();
    }

    private void MoveFinished()
    {
        if (_document is not { } document || _moving is not { } id) return;
        _moving = null;
        _history.End(document, id);
        Refresh();
    }""",
"""    private void MoveFinished()
    {
        if (_document is not { } document || _moving is not { } id) return;
        _moving = null;
        _history.End(document, id);
        Refresh();
    }""")

io.open(path, "w", encoding="utf-8", newline="\n").write(s)
print("patched")
