using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Compositor.Core.Document;
using Compositor.Core.Format;

namespace Compositor.Desktop;

/// <summary>
/// A ruler along one edge of the canvas: ticks every round step of document pixels, numbered about every 70
/// points whatever the zoom. The guides themselves are dragged on the canvas, so the strip sits outside it and
/// leaves every pointer position the canvas works out alone — except for the one thing a ruler is for in
/// Photoshop: a press on the strip makes a guide there and the drag carries it onto the canvas.
/// </summary>
internal sealed class RulerStrip : Control
{
    /// <summary>How much of the strip a tick reaches into it: numbered, half-way, and the rest.</summary>
    private const double MajorTick = 8;
    private const double MidTick = 5;
    private const double MinorTick = 3;

    // The Mac's CanvasRulers colours: a 0.2 face, 0.62 ticks and 0.78 labels, shut off with a 0.08 line.
    private static readonly IBrush Face = new SolidColorBrush(Skin.RulerFace);
    private static readonly IBrush Tick = new SolidColorBrush(Skin.RulerTick);
    private static readonly IBrush Label = new SolidColorBrush(Skin.RulerLabel);
    private static readonly IPen TickPen = new Pen(Tick, 1);
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Skin.RulerEdge), 1);
    private static readonly Typeface Digits = new("Consolas");

    /// <summary>Which edge this strip is on: the numbers run left to right, or down the side.</summary>
    public GuideAxis Axis { get; init; }

    /// <summary>Points on the strip for each document pixel, which is the canvas's own zoom.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>The document place at the strip's left end or top, which is the canvas's own origin.</summary>
    public double Origin { get; set; }

    /// <summary>A press on the strip, with the point it landed on: where a new guide goes.</summary>
    public Action<GuideAxis, Point>? Grabbed { get; set; }

    /// <summary>Where the pointer is while the guide is being pulled, in the strip's own coordinates.</summary>
    public Action<Point>? Dragged { get; set; }

    /// <summary>The pull has ended, wherever it ended.</summary>
    public Action? LetGo { get; set; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        Grabbed?.Invoke(Axis, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        Dragged?.Invoke(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Pointer.Capture(null);
        LetGo?.Invoke();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Face, new Rect(0, 0, width, height));
        if (Scale <= 0 || width <= 0 || height <= 0) return;
        var across = Axis == GuideAxis.Horizontal;
        var length = across ? width : height;
        var last = Origin + length / Scale;
        foreach (var tick in RulerScale.Between(Origin, last, Scale))
        {
            var at = (tick.Value - Origin) * Scale;
            if (at < 0 || at > length) continue;
            var into = tick.Kind switch
            {
                RulerTickKind.Major => MajorTick,
                RulerTickKind.Mid => MidTick,
                _ => MinorTick,
            };
            if (across) context.DrawLine(TickPen, new Point(at, height - into), new Point(at, height));
            else context.DrawLine(TickPen, new Point(width - into, at), new Point(width, at));
            if (tick.Kind != RulerTickKind.Major) continue;
            var text = new FormattedText(RulerScale.Label(tick.Value), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Digits, 9, Label);
            if (across)
            {
                context.DrawText(text, new Point(at + 2, 0));
                continue;
            }
            // Down the side the numbers read downwards, so the strip is turned a quarter turn to write them.
            using (context.PushTransform(Matrix.CreateTranslation(1, at + text.Width + 2)
                * Matrix.CreateRotation(-Math.PI / 2)))
            {
                context.DrawText(text, new Point(0, 0));
            }
        }
        // The line the strip shares with the canvas, so the two read as one edge.
        if (across) context.DrawLine(Edge, new Point(0, height - 0.5), new Point(width, height - 0.5));
        else context.DrawLine(Edge, new Point(width - 0.5, 0), new Point(width - 0.5, height));
    }
}

/// <summary>The little square between the two rulers, with the diagonal the Mac build's corner has.</summary>
internal sealed class RulerCorner : Control
{
    private static readonly IBrush Face = new SolidColorBrush(Skin.RulerFace);
    private static readonly IPen Diagonal = new Pen(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), 1);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Face, new Rect(0, 0, width, height));
        context.DrawLine(Diagonal, new Point(5, height - 4), new Point(width - 4, 5));
    }
}
