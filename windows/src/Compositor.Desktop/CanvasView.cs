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

namespace Compositor.Desktop;

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

    private CanvasDocument? _document;
    private SKPoint _origin;
    private double _zoom = 1;
    private Point? _dragging;

    /// <summary>The stroke being drawn, in document pixels, until the pointer comes back up.</summary>
    private readonly List<SKPoint> _stroke = [];
    private bool _painting;

    /// <summary>When set, dragging paints instead of panning.</summary>
    public bool PaintEnabled { get; set; }

    /// <summary>When set, dragging moves the selected layer instead of panning.</summary>
    public bool MoveEnabled { get; set; }

    /// <summary>When set, dragging draws a rectangular selection instead of panning.</summary>
    public bool MarqueeEnabled { get; set; }

    /// <summary>Handed the rectangle the marquee ended on, in document pixels.</summary>
    public Action<SKRectI>? MarqueeFinished { get; set; }

    private SKPoint _marqueeAnchor;
    private SKRectI? _marqueeDrawing;

    public BrushSettings Brush { get; set; } = new();

    /// <summary>Handed the finished stroke, in document pixels.</summary>
    public Action<IReadOnlyList<SKPoint>>? StrokeFinished { get; set; }

    /// <summary>A move gesture, so the whole drag is one undo step: the app opens an edit on
    /// <see cref="MoveStarted"/>, applies each delta, and closes it on <see cref="MoveFinished"/>.</summary>
    public Action? MoveStarted { get; set; }

    public Action<SKPoint>? MoveChanged { get; set; }

    public Action? MoveFinished { get; set; }

    private SKPoint _moveAnchor;
    private bool _moving;

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

    /// <summary>The outline of what is selected, and of the rectangle being dragged right now.</summary>
    private void DrawSelection(DrawingContext context)
    {
        var rectangle = _marqueeDrawing ?? _document?.Selection.Rect;
        if (rectangle is not { } rect || rect.Width <= 0 || rect.Height <= 0) return;
        var pen = new Pen
        {
            Brush = Brushes.White,
            Thickness = 1,
            DashStyle = new DashStyle([4.0, 4.0], 0),
        };
        var topLeft = ToScreen(new SKPoint(rect.Left, rect.Top));
        context.DrawRectangle(null, pen, new Rect(topLeft.X, topLeft.Y, rect.Width * _zoom, rect.Height * _zoom));
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
        if (MoveEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _moving = true;
            _moveAnchor = ToDocument(e.GetPosition(this));
            e.Pointer.Capture(this);
            MoveStarted?.Invoke();
            e.Handled = true;
            return;
        }
        if (MarqueeEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _marqueeAnchor = ToDocument(e.GetPosition(this));
            _marqueeDrawing = null;
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
        if (_moving)
        {
            var point = ToDocument(now);
            var delta = new SKPoint(point.X - _moveAnchor.X, point.Y - _moveAnchor.Y);
            _moveAnchor = point;
            if (delta.X != 0 || delta.Y != 0) MoveChanged?.Invoke(delta);
            base.OnPointerMoved(e);
            return;
        }
        if (_marqueeDrawing is not null || MarqueeEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _marqueeDrawing = Between(_marqueeAnchor, ToDocument(now));
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
        if (_moving)
        {
            _moving = false;
            e.Pointer.Capture(null);
            MoveFinished?.Invoke();
            return;
        }
        if (_marqueeDrawing is { } rectangle)
        {
            _marqueeDrawing = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
            MarqueeFinished?.Invoke(rectangle);
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
