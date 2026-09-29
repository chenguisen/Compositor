using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Compositor.Core.Format;
using Compositor.Core.Pixels;

namespace Compositor.Desktop;

/// <summary>
/// The curve a channel is bent by, drawn from the operator's own maths so what is shown is what the pixels
/// get. Handles are dragged, the line is clicked to put a new one on it, and a handle is right-clicked to
/// take it off.
/// </summary>
internal sealed class CurveEditor : Control
{
    /// <summary>How near a handle a click has to be, in screen pixels, to take hold of it.</summary>
    private const double Grab = 9;

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

    private static readonly Pen GridPen = new() { Brush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), Thickness = 1 };
    private static readonly Pen DiagonalPen = new() { Brush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), Thickness = 1 };
    private static readonly Pen CurvePen = new() { Brush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), Thickness = 2 };
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF));
    private static readonly IBrush ChosenBrush = Brushes.White;

    private CurvesSettings _curves = new();
    private int _channel;
    private int _held = -1;

    public CurveEditor()
    {
        Focusable = true;
    }

    /// <summary>Raised after a handle has been moved, added or taken away.</summary>
    public event Action? Changed;

    /// <summary>The curves being edited, held by the panel: a fresh copy each time it is set.</summary>
    public CurvesSettings Curves
    {
        get => _curves;
        set
        {
            _curves = value;
            _held = -1;
            InvalidateVisual();
        }
    }

    /// <summary>Which channel is shown and edited: 0 is the master, then red, green and blue.</summary>
    public int Channel
    {
        get => _channel;
        set
        {
            _channel = Math.Clamp(value, 0, 3);
            _held = -1;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var box = new Rect(Bounds.Size);
        context.FillRectangle(Backdrop, box);
        // The input runs left to right and the output upwards, as Photoshop draws a curve.
        for (var quarter = 1; quarter < 4; quarter++)
        {
            var at = Bounds.Width * quarter / 4;
            context.DrawLine(GridPen, new Point(at, 0), new Point(at, Bounds.Height));
            var down = Bounds.Height * quarter / 4;
            context.DrawLine(GridPen, new Point(0, down), new Point(Bounds.Width, down));
        }
        context.DrawLine(DiagonalPen, new Point(0, Bounds.Height), new Point(Bounds.Width, 0));

        var points = _curves.Channels[_channel];
        var line = new List<Point>(256);
        for (var input = 0; input <= 255; input++)
        {
            var output = AdjustmentOperators.CurvesValue(_curves, _channel, input);
            line.Add(At(input, output));
        }
        for (var index = 1; index < line.Count; index++) context.DrawLine(CurvePen, line[index - 1], line[index]);

        for (var index = 0; index < points.Count; index++)
        {
            var at = At(points[index].X, points[index].Y);
            var brush = index == _held ? ChosenBrush : HandleBrush;
            context.FillRectangle(brush, new Rect(at.X - 4, at.Y - 4, 8, 8));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var at = e.GetPosition(this);
        _held = Nearest(at);
        if (_held < 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // A click on the line itself puts a handle on it, and it is then being dragged.
            var points = _curves.Channels[_channel];
            var input = Math.Round(Input(at), MidpointRounding.AwayFromZero);
            if (input > 0 && input < 255 && points.Count < 32)
            {
                var output = Math.Round(AdjustmentOperators.CurvesValue(_curves, _channel, input), MidpointRounding.AwayFromZero);
                var made = new CurvePoint { X = input, Y = Math.Clamp(output, 0, 255) };
                points.Add(made);
                points.Sort((left, right) => left.X.CompareTo(right.X));
                _held = points.IndexOf(made);
                Announce();
            }
        }
        else if (_held >= 0 && e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // A handle away from the ends is taken off by right-clicking it.
            var points = _curves.Channels[_channel];
            if (_held != 0 && _held != points.Count - 1)
            {
                points.RemoveAt(_held);
                _held = -1;
                Announce();
            }
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_held < 0) return;
        var points = _curves.Channels[_channel];
        var at = e.GetPosition(this);
        var input = Math.Clamp(Math.Round(Input(at), MidpointRounding.AwayFromZero),
            points[_held == 0 ? 0 : _held - 1].X + 1, _held == points.Count - 1 ? 255 : points[_held + 1].X - 1);
        // The ends stay where they are across; only their output moves.
        if (_held != 0 && _held != points.Count - 1) points[_held].X = input;
        points[_held].Y = Math.Clamp(Math.Round(Output(at), MidpointRounding.AwayFromZero), 0, 255);
        Announce();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _held = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private void Announce()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>The handle within reach of a click, or -1 when the click is on the line rather than a handle.</summary>
    private int Nearest(Point at)
    {
        var points = _curves.Channels[_channel];
        var best = -1;
        var nearest = Grab;
        for (var index = 0; index < points.Count; index++)
        {
            var there = At(points[index].X, points[index].Y);
            var distance = Math.Sqrt((there.X - at.X) * (there.X - at.X) + (there.Y - at.Y) * (there.Y - at.Y));
            if (distance > nearest) continue;
            nearest = distance;
            best = index;
        }
        return best;
    }

    private Point At(double input, double output) =>
        new(input / 255 * Bounds.Width, Bounds.Height - output / 255 * Bounds.Height);

    private double Input(Point at) => Bounds.Width <= 0 ? 0 : at.X / Bounds.Width * 255;

    private double Output(Point at) => Bounds.Height <= 0 ? 0 : (Bounds.Height - at.Y) / Bounds.Height * 255;
}
