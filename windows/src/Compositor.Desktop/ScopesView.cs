using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// The Camera Raw panel's scope: three RGB ribbons against one shared peak, or the hue-and-saturation density
/// of the same picture, with the two clipping triangles in its bottom corners. The Mac draws the same three
/// things in its own panel.
/// </summary>
internal sealed class ScopesView : Control
{
    /// <summary>How big each clipping triangle is, and so how big a target it is to click.</summary>
    private const double Triangle = 16;
    private const double Inset = 4;

    /// <summary>What the scope is drawn on, which is the Mac's black at a third over the panel.</summary>
    private static readonly IBrush Ground = new SolidColorBrush(Colors.Black, 0.35);
    private static readonly IBrush ShadowOn = new SolidColorBrush(Color.FromRgb(0x32, 0x8C, 0xFF));
    private static readonly IBrush HighlightOn = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly IBrush TriangleOff = new SolidColorBrush(Colors.White, 0.55);

    private CameraRawScope? _scope;
    private bool _vectorscope;
    private bool _shadows;
    private bool _highlights;

    /// <summary>A triangle was clicked: true for the shadow one, false for the highlight one.</summary>
    public event Action<bool>? ClippingToggled;

    /// <summary>The scope was right-clicked, which asks for the other of the histogram and the vectorscope.</summary>
    public event Action? ModeSwapped;

    /// <summary>The scope to draw, or null until the panel's preview has counted one.</summary>
    public CameraRawScope? Scope
    {
        get => _scope;
        set { _scope = value; InvalidateVisual(); }
    }

    /// <summary>Whether the density is drawn instead of the three ribbons.</summary>
    public bool Vectorscope
    {
        get => _vectorscope;
        set { _vectorscope = value; InvalidateVisual(); }
    }

    /// <summary>Whether the shadow triangle is lit, which is the same switch the panel's own checkbox sets.</summary>
    public bool ShowsShadows
    {
        get => _shadows;
        set { _shadows = value; InvalidateVisual(); }
    }

    /// <summary>Whether the highlight triangle is lit.</summary>
    public bool ShowsHighlights
    {
        get => _highlights;
        set { _highlights = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        context.FillRectangle(Ground, new Rect(size));
        if (_scope is { } scope)
        {
            if (_vectorscope) Density(context, scope, size);
            else Ribbons(context, scope, size);
        }
        // The triangles sit in the corners they belong to: shadows on the left, highlights on the right.
        DrawTriangle(context, new Rect(Inset, size.Height - Triangle - Inset, Triangle, Triangle),
            _shadows ? ShadowOn : TriangleOff);
        DrawTriangle(context, new Rect(size.Width - Triangle - Inset, size.Height - Triangle - Inset, Triangle, Triangle),
            _highlights ? HighlightOn : TriangleOff);
    }

    /// <summary>The three channels drawn over each other, all against the one peak so they stay comparable.</summary>
    private static void Ribbons(DrawingContext context, CameraRawScope scope, Size size)
    {
        if (!(scope.Peak > 0)) return;
        // Drawn red, then green, then blue, so the three overlap the way the Mac's do.
        Ribbon(context, scope.Red, new SolidColorBrush(Colors.Red, 0.55), scope.Peak, size);
        Ribbon(context, scope.Green, new SolidColorBrush(Colors.LimeGreen, 0.55), scope.Peak, size);
        Ribbon(context, scope.Blue, new SolidColorBrush(Colors.RoyalBlue, 0.55), scope.Peak, size);
    }

    private static void Ribbon(DrawingContext context, double[] bins, IBrush brush, double peak, Size size)
    {
        if (bins.Length == 0) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(0, size.Height), true);
            for (var index = 0; index < bins.Length; index++)
            {
                var x = index * size.Width / bins.Length;
                var height = size.Height * Math.Clamp(bins[index] / peak, 0, 1);
                path.LineTo(new Point(x, size.Height - height));
            }
            path.LineTo(new Point(size.Width, size.Height));
            path.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    /// <summary>The vectorscope: one cell per hue and saturation, brighter the more of the picture is there.</summary>
    private static void Density(DrawingContext context, CameraRawScope scope, Size size)
    {
        if (!(scope.ScopePeak > 0)) return;
        var cell = size.Width / CameraRawScope.ScopeSide;
        for (var index = 0; index < scope.Vectorscope.Length; index++)
        {
            var amount = scope.Vectorscope[index];
            if (!(amount > 0)) continue;
            var column = index % CameraRawScope.ScopeSide;
            var row = index / CameraRawScope.ScopeSide;
            var brush = new SolidColorBrush(Colors.White, 0.15 + 0.85 * Math.Clamp(amount / scope.ScopePeak, 0, 1));
            // Rows count up from the bottom, so a bright cell is high in the square rather than low in it.
            context.FillRectangle(brush,
                new Rect(column * cell, size.Height - (row + 1) * cell, cell + 0.2, cell + 0.2));
        }
    }

    /// <summary>A filled triangle standing on the bottom of its box, as the Mac's clipping buttons are.</summary>
    private static void DrawTriangle(DrawingContext context, Rect box, IBrush brush)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(box.Left + box.Width / 2, box.Top), true);
            path.LineTo(new Point(box.Right, box.Bottom));
            path.LineTo(new Point(box.Left, box.Bottom));
            path.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    /// <summary>The box each triangle is drawn in, in the same place the drawing puts it.</summary>
    private Rect ShadowBox => new(Inset, Bounds.Height - Triangle - Inset, Triangle, Triangle);
    private Rect HighlightBox => new(Bounds.Width - Triangle - Inset, Bounds.Height - Triangle - Inset, Triangle, Triangle);

    /// <summary>
    /// Turns the clipping switch a triangle stands for, or asks for the other scope, as a click or a
    /// right-click would. A drawn shape raises no button of its own, so the self check asks for what a press
    /// does rather than making one.
    /// </summary>
    internal void Press(bool shadows) => ClippingToggled?.Invoke(shadows);

    /// <summary>Asks for the scope that is not being shown, as a right-click does.</summary>
    internal void Swap() => ModeSwapped?.Invoke();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            ModeSwapped?.Invoke();
            e.Handled = true;
            return;
        }
        // The triangles are the whole of the scope that is clickable; anywhere else is the panel's own.
        if (ShadowBox.Contains(point))
        {
            ClippingToggled?.Invoke(true);
            e.Handled = true;
            return;
        }
        if (HighlightBox.Contains(point))
        {
            ClippingToggled?.Invoke(false);
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
    }
}
