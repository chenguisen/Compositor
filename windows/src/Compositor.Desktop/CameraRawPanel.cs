using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;
using SkiaSharp;

namespace Compositor.Desktop;

/// <summary>
/// The Camera Raw Filter's panel: every group of settings as sliders, and Apply to write them into the layer's
/// pixels. It is not a window of its own — the editor window docks it at its right edge, because the canvas has
/// to stay live while it is up: the amounts are previewed on the canvas as they move, and the Mac build draws
/// upright guides and picks colours on that same canvas, which a modal dialog would not allow.
/// </summary>
internal sealed class CameraRawPanel
{
    private readonly List<(Slider Slider, Action<CameraRawSettings, double> Set, TextBlock Readout, string Format)> _rows = [];
    /// <summary>The panel's amounts by the label their row carries, which is how the self check moves one.</summary>
    private readonly Dictionary<string, Slider> _labelled = [];
    private readonly ComboBox _glowStyle = new();
    private readonly ComboBox _vignetteStyle = new();
    private readonly ComboBox _geometryProjection = new();
    private readonly ComboBox _curveChannel = new();
    private readonly ListBox _points = new() { Height = 96 };
    private readonly List<CameraRawPointColor> _pointList = [];
    private CameraRawPointColor? _point;
    private bool _showingPoints;
    private CurveEditor? _curve;

    /// <summary>
    /// Asks for the picture to be shown with the amounts as they stand, which is called on every change. The
    /// panel does not wait for it: a slider being dragged should not stop moving while a filter runs.
    /// </summary>
    public Action<CameraRawSettings, bool, bool, bool>? Preview { get; set; }

    /// <summary>The amounts to write into the layer, once Apply is pressed.</summary>
    public event Action<CameraRawSettings>? Applied;

    /// <summary>Nothing is to be written; the caller puts the picture back as it was.</summary>
    public event Action? Cancelled;

    /// <summary>What is shown over the picture while the amounts are moved: clipped shadows in blue, clipped
    /// highlights in red, and the sharpening mask. None of it is ever applied on Apply.</summary>
    private readonly CheckBox _shadowClip = new() { Content = "Clipped shadows" };
    private readonly CheckBox _highlightClip = new() { Content = "Clipped highlights" };
    private readonly CheckBox _sharpenMaskView = new() { Content = "Sharpening mask" };

    /// <summary>The scope above the groups, and the readout of the pixel the pointer is over.</summary>
    private readonly ScopesView _scopes = new() { Height = 110 };
    private readonly TextBlock _readout = new()
    {
        Text = EmptyReadout,
        FontSize = 11,
        Foreground = Skin.SecondaryBrush,
    };
    private const string EmptyReadout = "R —   G —   B —";

    /// <summary>The body, for the window to dock at its right edge.</summary>
    public Control View { get; }

    /// <summary>How many amounts the panel is made of. A bitmap does not lay a scroll view's content out, so
    /// the drawing cannot show the rows; the self check counts them here instead.</summary>
    internal int AmountCount => _rows.Count;

    /// <summary>Shows what is being asked for now, overlays and all.</summary>
    private void RefreshPreview() => Preview?.Invoke(Current(), _shadowClip.IsChecked == true,
        _highlightClip.IsChecked == true, _sharpenMaskView.IsChecked == true);

    /// <summary>Asks for a picture of the amounts as they stand, without any of them having moved.</summary>
    public void Show() => RefreshPreview();

    /// <summary>Shows the scope the last preview counted, which is the picture the canvas is drawing.</summary>
    public void ShowScope(CameraRawScope? scope) => _scopes.Scope = scope;

    /// <summary>
    /// Shows the colour of the pixel the pointer is over, as the readout under the scope. Nothing is passed
    /// when the pointer is off the picture, and the readout goes back to its dashes.
    /// </summary>
    public void ShowReadout((int Red, int Green, int Blue)? pixel) => _readout.Text = pixel is { } value
        ? $"R {value.Red}   G {value.Green}   B {value.Blue}"
        : EmptyReadout;

    /// <summary>
    /// Which of the two clipping views is on, so the caller knows what the triangles and the checkboxes agree
    /// on. They are one switch each, whichever of the two is used to move them.
    /// </summary>
    public (bool Shadows, bool Highlights) Clipping =>
        (_shadowClip.IsChecked == true, _highlightClip.IsChecked == true);

    /// <summary>The two triangles lit as their checkboxes have them, which is what a tick of one changes.</summary>
    private void RefreshClipping()
    {
        _scopes.ShowsShadows = _shadowClip.IsChecked == true;
        _scopes.ShowsHighlights = _highlightClip.IsChecked == true;
    }

    /// <summary>Presses one of the scope's clipping triangles, and the switch it stands for goes with it.</summary>
    internal void PressClippingTriangle(bool shadows) => _scopes.Press(shadows);

    /// <summary>Asks for the other of the histogram and the vectorscope, as a right-click on the scope does.</summary>
    internal void SwapScope() => _scopes.Swap();

    /// <summary>Whether the density is being drawn rather than the three ribbons.</summary>
    internal bool ShowingVectorscope => _scopes.Vectorscope;

    /// <summary>Writes the amounts as they stand into the layer. The buttons go through here, and so does the
    /// self check, so what it drives is the path a press takes.</summary>
    public void Apply() => Applied?.Invoke(Current());

    /// <summary>Asks for nothing to be written. The buttons go through here, and so does the self check.</summary>
    public void Cancel() => Cancelled?.Invoke();

    /// <summary>Puts every amount back to nothing, and the curve with them.</summary>
    public void Reset()
    {
        foreach (var (slider, _, _, _) in _rows) slider.Value = 0;
        if (_curve is not null) _curve.Curves = new Compositor.Core.Format.CurvesSettings();
    }

    /// <summary>
    /// Moves one of the panel's own amounts, by the label its row carries. A slider's value is a property and
    /// not a template, so the panel can be driven without a pointer: the change runs the same handler a drag
    /// would, preview and all. The self check is the only caller.
    /// </summary>
    internal void Move(string label, double value)
    {
        if (!_labelled.TryGetValue(label, out var slider))
        {
            throw new InvalidOperationException($"the panel has no amount called {label}");
        }
        slider.Value = value;
    }

    /// <summary>The amounts as the panel has them, for a preview of what they would do.</summary>
    public CameraRawSettings Current()
    {
        var settings = new CameraRawSettings
        {
            GlowStyle = Math.Max(0, _glowStyle.SelectedIndex),
            VignetteStyle = Math.Max(0, _vignetteStyle.SelectedIndex),
            Geometry = new CameraRawGeometrySettings
            {
                Projection = (GeometryProjection)Math.Max(0, _geometryProjection.SelectedIndex),
            },
            Curve = _curve is { } curve ? curve.Curves : new Compositor.Core.Format.CurvesSettings(),
            // The mixer's places are written into by the rows, so the settings the rows are handed have all
            // twenty-four of them whatever the layer's panel started from.
            Mixer = new double[24],
            Points = [.. Points()],
        };
        foreach (var (slider, set, _, _) in _rows) set(settings, slider.Value);
        return settings;
    }

    internal CameraRawPanel(CameraRawSettings start, SKColor brush)
    {
        brush.ToHsl(out var brushHue, out var brushSaturation, out _);
        _brushHue = brushHue;
        _brushSaturation = brushSaturation;
        var groups = new StackPanel { Margin = new Thickness(16), Spacing = 4 };

        // The overlays come first: they are shown over whatever the groups below are doing, and none of them
        // is written into the layer when Apply is pressed. The clipping triangles in the scope are the same two
        // switches, so one setter drives both and neither can be lit while the other is not.
        var overlays = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var box in new[] { _shadowClip, _highlightClip, _sharpenMaskView })
        {
            box.IsCheckedChanged += (_, _) => { RefreshClipping(); RefreshPreview(); };
            overlays.Children.Add(box);
        }
        _scopes.ClippingToggled += shadows =>
        {
            var box = shadows ? _shadowClip : _highlightClip;
            box.IsChecked = box.IsChecked != true;
        };
        _scopes.ModeSwapped += () => _scopes.Vectorscope = !_scopes.Vectorscope;
        groups.Children.Add(overlays);

        groups.Children.Add(Heading("Light"));
        Add(groups, "Exposure, stops", -5, 5, start.Exposure, (s, v) => s.Exposure = v, "0.00");
        Add(groups, "Contrast", -100, 100, start.Contrast, (s, v) => s.Contrast = v);
        Add(groups, "Highlights", -100, 100, start.Highlights, (s, v) => s.Highlights = v);
        Add(groups, "Shadows", -100, 100, start.Shadows, (s, v) => s.Shadows = v);
        Add(groups, "Whites", -100, 100, start.Whites, (s, v) => s.Whites = v);
        Add(groups, "Blacks", -100, 100, start.Blacks, (s, v) => s.Blacks = v);

        groups.Children.Add(Heading("Color"));
        Add(groups, "Temperature, cool to warm", -100, 100, start.Temperature, (s, v) => s.Temperature = v);
        Add(groups, "Tint, green to magenta", -100, 100, start.Tint, (s, v) => s.Tint = v);
        Add(groups, "Vibrance", -100, 100, start.Vibrance, (s, v) => s.Vibrance = v);
        Add(groups, "Saturation", -100, 100, start.Saturation, (s, v) => s.Saturation = v);

        groups.Children.Add(Heading("Effects"));
        Add(groups, "Texture", -100, 100, start.Texture, (s, v) => s.Texture = v);
        Add(groups, "Clarity", -100, 100, start.Clarity, (s, v) => s.Clarity = v);
        Add(groups, "Dehaze", -100, 100, start.Dehaze, (s, v) => s.Dehaze = v);
        Add(groups, "Glow", 0, 100, start.Glow, (s, v) => s.Glow = v);
        groups.Children.Add(Choice("Glow style", _glowStyle, ["Diffusion", "Bloom", "Halation"]));
        Add(groups, "Glow range", 0, 100, start.GlowRange, (s, v) => s.GlowRange = v);
        Add(groups, "Glow spread", 0, 100, start.GlowSpread, (s, v) => s.GlowSpread = v);
        Add(groups, "Glow warmth", -100, 100, start.GlowWarmth, (s, v) => s.GlowWarmth = v);
        Add(groups, "Vignette amount", -100, 100, start.VignetteAmount, (s, v) => s.VignetteAmount = v);
        groups.Children.Add(Choice("Vignette style", _vignetteStyle,
            ["Highlight priority", "Color priority", "Paint overlay"]));
        Add(groups, "Vignette midpoint", 0, 100, start.VignetteMidpoint, (s, v) => s.VignetteMidpoint = v);
        Add(groups, "Vignette roundness", -100, 100, start.VignetteRoundness, (s, v) => s.VignetteRoundness = v);
        Add(groups, "Vignette feather", 0, 100, start.VignetteFeather, (s, v) => s.VignetteFeather = v);
        Add(groups, "Vignette highlights", -100, 100, start.VignetteHighlights, (s, v) => s.VignetteHighlights = v);
        Add(groups, "Grain amount", 0, 100, start.GrainAmount, (s, v) => s.GrainAmount = v);
        Add(groups, "Grain size", 0, 100, start.GrainSize, (s, v) => s.GrainSize = v);
        Add(groups, "Grain roughness", 0, 100, start.GrainRoughness, (s, v) => s.GrainRoughness = v);

        groups.Children.Add(Heading("Detail"));
        Add(groups, "Sharpen amount", 0, 150, start.SharpenAmount, (s, v) => s.SharpenAmount = v);
        Add(groups, "Sharpen radius", 0.5, 100, start.SharpenRadius, (s, v) => s.SharpenRadius = v, "0.0");
        Add(groups, "Sharpen detail", 0, 100, start.SharpenDetail, (s, v) => s.SharpenDetail = v);
        Add(groups, "Sharpen masking", 0, 100, start.SharpenMasking, (s, v) => s.SharpenMasking = v);
        Add(groups, "Noise luminance", 0, 100, start.NoiseLuminance, (s, v) => s.NoiseLuminance = v);
        Add(groups, "Noise luminance detail", 0, 100, start.NoiseLuminanceDetail, (s, v) => s.NoiseLuminanceDetail = v);
        Add(groups, "Noise luminance contrast", 0, 100, start.NoiseLuminanceContrast, (s, v) => s.NoiseLuminanceContrast = v);
        Add(groups, "Noise colour", 0, 100, start.NoiseColor, (s, v) => s.NoiseColor = v);
        Add(groups, "Noise colour detail", 0, 100, start.NoiseColorDetail, (s, v) => s.NoiseColorDetail = v);
        Add(groups, "Noise colour smoothness", 0, 100, start.NoiseColorSmoothness, (s, v) => s.NoiseColorSmoothness = v);

        groups.Children.Add(Heading("Optics"));
        Add(groups, "Remove chromatic aberration", 0, 1, start.RemoveChromaticAberration ? 1 : 0, (s, v) => s.RemoveChromaticAberration = v > 0.5, "0");
        Add(groups, "Lens profile", 0, 1, start.EnableLensProfile ? 1 : 0, (s, v) => s.EnableLensProfile = v > 0.5, "0");
        Add(groups, "Profile distortion", 0, 100, start.ProfileDistortion, (s, v) => s.ProfileDistortion = v);
        Add(groups, "Profile vignetting", 0, 100, start.ProfileVignetting, (s, v) => s.ProfileVignetting = v);
        Add(groups, "Distortion", -100, 100, start.Distortion, (s, v) => s.Distortion = v);
        Add(groups, "Purple amount", 0, 100, start.PurpleAmount, (s, v) => s.PurpleAmount = v);
        Add(groups, "Purple hue low", 0, 360, start.PurpleHueLow, (s, v) => s.PurpleHueLow = v);
        Add(groups, "Purple hue high", 0, 360, start.PurpleHueHigh, (s, v) => s.PurpleHueHigh = v);
        Add(groups, "Green amount", 0, 100, start.GreenAmount, (s, v) => s.GreenAmount = v);
        Add(groups, "Green hue low", 0, 360, start.GreenHueLow, (s, v) => s.GreenHueLow = v);
        Add(groups, "Green hue high", 0, 360, start.GreenHueHigh, (s, v) => s.GreenHueHigh = v);
        Add(groups, "Lens vignette", -100, 100, start.OpticsVignetteAmount, (s, v) => s.OpticsVignetteAmount = v);
        Add(groups, "Lens vignette midpoint", 0, 100, start.OpticsVignetteMidpoint, (s, v) => s.OpticsVignetteMidpoint = v);

        groups.Children.Add(Heading("Geometry"));
        groups.Children.Add(Choice("Projection", _geometryProjection, ["Perspective", "Rectilinear"]));
        Add(groups, "Vertical", -100, 100, start.Geometry.Vertical, (s, v) => s.Geometry.Vertical = v);
        Add(groups, "Horizontal", -100, 100, start.Geometry.Horizontal, (s, v) => s.Geometry.Horizontal = v);
        Add(groups, "Rotate", -45, 45, start.Geometry.Rotate, (s, v) => s.Geometry.Rotate = v);
        Add(groups, "Aspect", -100, 100, start.Geometry.Aspect, (s, v) => s.Geometry.Aspect = v);
        Add(groups, "Scale", -100, 100, start.Geometry.Scale, (s, v) => s.Geometry.Scale = v);
        Add(groups, "Offset X", -100, 100, start.Geometry.OffsetX, (s, v) => s.Geometry.OffsetX = v);
        Add(groups, "Offset Y", -100, 100, start.Geometry.OffsetY, (s, v) => s.Geometry.OffsetY = v);
        Add(groups, "Constrain crop", 0, 1, start.Geometry.ConstrainCrop ? 1 : 0,
            (s, v) => s.Geometry.ConstrainCrop = v > 0.5, "0");

        groups.Children.Add(Heading("Calibration"));
        Add(groups, "Process version", 1, 6, start.ProcessVersion, (s, v) => s.ProcessVersion = (int)Math.Round(v), "0");
        Add(groups, "Shadow tint", -100, 100, start.ShadowTint, (s, v) => s.ShadowTint = v);
        Add(groups, "Red hue", -100, 100, start.RedHue, (s, v) => s.RedHue = v);
        Add(groups, "Red saturation", -100, 100, start.RedSaturation, (s, v) => s.RedSaturation = v);
        Add(groups, "Green hue", -100, 100, start.GreenHue, (s, v) => s.GreenHue = v);
        Add(groups, "Green saturation", -100, 100, start.GreenSaturation, (s, v) => s.GreenSaturation = v);
        Add(groups, "Blue hue", -100, 100, start.BlueHue, (s, v) => s.BlueHue = v);
        Add(groups, "Blue saturation", -100, 100, start.BlueSaturation, (s, v) => s.BlueSaturation = v);

        groups.Children.Add(Heading("Curve"));
        _curveChannel.ItemsSource = new[] { "Whole picture", "Red", "Green", "Blue" };
        _curveChannel.SelectedIndex = Math.Clamp((int)start.Curve.Channel, 0, 3);
        _curveChannel.Width = 160;
        _curve = new CurveEditor { Curves = Clone(start.Curve), Height = 220 };
        _curveChannel.SelectionChanged += (_, _) => _curve.Channel = Math.Max(0, _curveChannel.SelectedIndex);
        _curve.Changed += RefreshPreview;
        groups.Children.Add(_curveChannel);
        groups.Children.Add(_curve);
        Add(groups, "Refine saturation", -100, 100, start.RefineSaturation, (s, v) => s.RefineSaturation = v);

        groups.Children.Add(Heading("Colour mixer"));
        // The hues come first in the mixer's own places and then the saturations, which is the order the
        // kernel reads them in rather than the order a panel would list them.
        var mixer = start.Mixer;
        for (var family = 0; family < CameraRawSettings.MixerFamilies.Length; family++)
        {
            var hue = family;
            var saturation = CameraRawSettings.MixerFamilies.Length + family;
            var luminance = CameraRawSettings.MixerFamilies.Length * 2 + family;
            var name = CameraRawSettings.MixerFamilies[family];
            Add(groups, $"{name}: hue", -100, 100, At(mixer, hue), (s, v) => s.Mixer[hue] = v);
            Add(groups, $"{name}: saturation", -100, 100, At(mixer, saturation), (s, v) => s.Mixer[saturation] = v);
            Add(groups, $"{name}: luminance", -100, 100, At(mixer, luminance), (s, v) => s.Mixer[luminance] = v);
        }

        groups.Children.Add(Heading("Point colour"));
        groups.Children.Add(new TextBlock
        {
            Text = "Pick the colour the brush is set to out of the picture, then move it. The Mac build picks "
                + "colours by clicking on the canvas, which this panel does not do.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75,
        });
        var addPoint = new Button { Content = "Add the brush colour" };
        var removePoint = new Button { Content = "Remove" };
        addPoint.Click += (_, _) => AddPoint();
        removePoint.Click += (_, _) => RemovePoint();
        groups.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { addPoint, removePoint },
        });
        groups.Children.Add(_points);
        _points.SelectionChanged += (_, _) => SelectPoint();
        // The nine numbers of the one being edited, which is what the panel's sliders move.
        Add(groups, "Hue", 0, 360, 0, (_, v) => Point(point => point.Hue = v), "0");
        Add(groups, "Saturation", 0, 1, 0, (_, v) => Point(point => point.Saturation = v), "0.00");
        Add(groups, "Lightness", 0, 1, 0, (_, v) => Point(point => point.Luminance = v), "0.00");
        Add(groups, "Turn the hue", -100, 100, 0, (_, v) => Point(point => point.HueShift = v));
        Add(groups, "Raise the saturation", -100, 100, 0, (_, v) => Point(point => point.SaturationShift = v));
        Add(groups, "Move the lightness", -100, 100, 0, (_, v) => Point(point => point.LuminanceShift = v));
        Add(groups, "Hue range", 5, 180, 30, (_, v) => Point(point => point.HueRange = v), "0");
        Add(groups, "Saturation range", 0.05, 1, 0.4, (_, v) => Point(point => point.SaturationRange = v), "0.00");
        Add(groups, "Lightness range", 0.05, 1, 0.4, (_, v) => Point(point => point.LuminanceRange = v), "0.00");
        foreach (var point in start.Points) _pointList.Add(point.Normalized());
        if (_pointList.Count > 0) _points.SelectedIndex = 0;

        groups.Children.Add(Heading("Colour grading"));
        Add(groups, "Shadows: hue", 0, 360, start.ShadowHue, (s, v) => s.ShadowHue = v, "0");
        Add(groups, "Shadows: amount", 0, 100, start.ShadowSaturation, (s, v) => s.ShadowSaturation = v, "0");
        Add(groups, "Shadows: lightness", -100, 100, start.ShadowLuminance, (s, v) => s.ShadowLuminance = v);
        Add(groups, "Midtones: hue", 0, 360, start.MidtoneHue, (s, v) => s.MidtoneHue = v, "0");
        Add(groups, "Midtones: amount", 0, 100, start.MidtoneSaturation, (s, v) => s.MidtoneSaturation = v, "0");
        Add(groups, "Midtones: lightness", -100, 100, start.MidtoneLuminance, (s, v) => s.MidtoneLuminance = v);
        Add(groups, "Highlights: hue", 0, 360, start.HighlightHue, (s, v) => s.HighlightHue = v, "0");
        Add(groups, "Highlights: amount", 0, 100, start.HighlightSaturation, (s, v) => s.HighlightSaturation = v, "0");
        Add(groups, "Highlights: lightness", -100, 100, start.HighlightLuminance, (s, v) => s.HighlightLuminance = v);
        Add(groups, "Whole picture: hue", 0, 360, start.GlobalHue, (s, v) => s.GlobalHue = v, "0");
        Add(groups, "Whole picture: amount", 0, 100, start.GlobalSaturation, (s, v) => s.GlobalSaturation = v, "0");
        Add(groups, "Whole picture: lightness", -100, 100, start.GlobalLuminance, (s, v) => s.GlobalLuminance = v);
        Add(groups, "Grading blending", 0, 100, start.GradeBlending, (s, v) => s.GradeBlending = v, "0");
        Add(groups, "Grading balance", -100, 100, start.GradeBalance, (s, v) => s.GradeBalance = v);

        _glowStyle.SelectedIndex = start.GlowStyle;
        _vignetteStyle.SelectedIndex = start.VignetteStyle;

        var ok = new Button { Content = "Apply" };
        var cancel = new Button { Content = "Cancel" };
        var reset = new Button { Content = "Reset" };
        ok.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Cancel();
        reset.Click += (_, _) => Reset();

        var title = new TextBlock
        {
            Text = "Camera Raw Filter",
            Margin = new Thickness(16, 12, 16, 4),
            Foreground = Skin.LabelBrush,
            FontWeight = FontWeight.SemiBold,
        };
        // The scope sits above the groups and outside their scroll, so it stays in view while they are worked
        // through — the Mac's panel puts its histogram in the same place.
        var scope = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 6),
            Spacing = 4,
            Children = { _scopes, _readout },
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 10, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, ok },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(scope, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(title);
        dock.Children.Add(scope);
        dock.Children.Add(buttons);
        // The groups fill what is left, and scroll: the panel is the height of the window and they are not.
        dock.Children.Add(new ScrollViewer { Content = groups });
        View = dock;
    }

    /// <summary>The colours as the panel has them, with the one being edited stored back first.</summary>
    private List<CameraRawPointColor> Points()
    {
        StorePoint();
        return [.. _pointList];
    }

    /// <summary>Changes the colour being edited, if there is one.</summary>
    private void Point(Action<CameraRawPointColor> change)
    {
        if (_point is null) return;
        change(_point);
        Labelled();
    }

    /// <summary>Adds the brush's own colour to the points, at the middle of the picture's lightness.</summary>
    private void AddPoint()
    {
        if (_pointList.Count >= CameraRawSettings.MostPoints) return;
        StorePoint();
        _pointList.Add(new CameraRawPointColor
        {
            Hue = _brushHue,
            Saturation = _brushSaturation,
            Luminance = 0.5,
        });
        _showingPoints = true;
        try
        {
            _points.SelectedIndex = _pointList.Count - 1;
        }
        finally
        {
            _showingPoints = false;
        }
        _point = _pointList[^1];
        Labelled();
        RefreshPreview();
    }

    /// <summary>Takes the colour being edited out of the list.</summary>
    private void RemovePoint()
    {
        if (_points.SelectedIndex < 0 || _points.SelectedIndex >= _pointList.Count) return;
        _pointList.RemoveAt(_points.SelectedIndex);
        _showingPoints = true;
        try
        {
            _points.SelectedIndex = _pointList.Count > 0 ? Math.Min(_points.SelectedIndex, _pointList.Count - 1) : -1;
        }
        finally
        {
            _showingPoints = false;
        }
        _point = _points.SelectedIndex >= 0 ? _pointList[_points.SelectedIndex] : null;
        Labelled();
        RefreshPreview();
    }

    /// <summary>The list has moved to another colour: what was being edited is kept and the other loaded.</summary>
    private void SelectPoint()
    {
        if (_showingPoints) return;
        StorePoint();
        _point = _points.SelectedIndex >= 0 && _points.SelectedIndex < _pointList.Count
            ? _pointList[_points.SelectedIndex]
            : null;
        LoadPoint();
        RefreshPreview();
    }

    private void StorePoint()
    {
        if (_point is null) return;
        var at = _pointList.IndexOf(_point);
        if (at >= 0) _pointList[at] = _point.Normalized();
    }

    /// <summary>
    /// The sliders read the colour being edited. The rows are the panel's own, so they are moved without
    /// asking for a preview of each one.
    /// </summary>
    private void LoadPoint()
    {
        if (_point is not { } point) return;
        var values = new[]
        {
            point.Hue, point.Saturation, point.Luminance,
            point.HueShift, point.SaturationShift, point.LuminanceShift,
            point.HueRange, point.SaturationRange, point.LuminanceRange,
        };
        for (var index = 0; index < values.Length; index++)
        {
            _rows[_pointRow + index].Slider.Value = values[index];
        }
    }

    /// <summary>Puts the numbers of each colour, and whether one is being edited at all, into the list.</summary>
    private void Labelled()
    {
        _showingPoints = true;
        try
        {
            var at = _points.SelectedIndex;
            _points.ItemsSource = _pointList
                .Select((point, index) => $"{index + 1}: hue {point.Hue:0}°, saturation {point.Saturation:0.00}, "
                    + $"lightness {point.Luminance:0.00}")
                .ToList();
            _points.SelectedIndex = at;
        }
        finally
        {
            _showingPoints = false;
        }
    }

    /// <summary>The first row that belongs to the colour being edited.</summary>
    private int _pointRow =>
        _rows.Count - 9;

    /// <summary>One of the mixer's numbers, or nothing when the settings came without their twenty-four.</summary>
    private static double At(double[] mixer, int index) => index < mixer.Length ? mixer[index] : 0;

    private static Control Heading(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private void Add(StackPanel parent, string label, double least, double most, double value,
        Action<CameraRawSettings, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 260 };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void UpdateReadout() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            UpdateReadout();
            RefreshPreview();
        };
        UpdateReadout();
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center },
                slider,
                readout,
            },
        });
        _rows.Add((slider, set, readout, format));
        _labelled[label] = slider;
    }

    private static Control Choice(string label, ComboBox box, string[] options)
    {
        box.ItemsSource = options;
        box.SelectedIndex = 0;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center },
                box,
            },
        };
    }

    /// <summary>The brush's colour while the panel was opened, which is the colour a point starts at.</summary>
    private readonly double _brushHue;
    private readonly double _brushSaturation;

    /// <summary>
    /// The curve copied, so dragging a handle does not reach the layer until Apply: the editor writes into the
    /// copy it is given, and a history snapshot holds the record it started from.
    /// </summary>
    private static Compositor.Core.Format.CurvesSettings Clone(Compositor.Core.Format.CurvesSettings curves) => new()
    {
        Channel = curves.Channel,
        Channels = curves.Channels
            .Select(points => points.Select(point => new Compositor.Core.Format.CurvePoint { X = point.X, Y = point.Y }).ToList())
            .ToList(),
    };
}
