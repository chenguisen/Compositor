using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// The Camera Raw Filter's panel: its Light, Color and Effects groups as sliders, and OK to apply them to
/// the layer's pixels. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class CameraRawDialog : Window
{
    private readonly List<(Slider Slider, Action<CameraRawSettings, double> Set, TextBlock Readout, string Format)> _rows = [];
    private readonly ComboBox _glowStyle = new();
    private readonly ComboBox _vignetteStyle = new();
    private CameraRawSettings? _result;

    private CameraRawDialog(CameraRawSettings start)
    {
        Title = "Camera Raw Filter";
        Width = 460;
        Height = 700;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var groups = new StackPanel { Margin = new Thickness(16), Spacing = 4 };

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

        groups.Children.Add(Heading("Calibration"));
        Add(groups, "Process version", 1, 6, start.ProcessVersion, (s, v) => s.ProcessVersion = (int)Math.Round(v), "0");
        Add(groups, "Shadow tint", -100, 100, start.ShadowTint, (s, v) => s.ShadowTint = v);
        Add(groups, "Red hue", -100, 100, start.RedHue, (s, v) => s.RedHue = v);
        Add(groups, "Red saturation", -100, 100, start.RedSaturation, (s, v) => s.RedSaturation = v);
        Add(groups, "Green hue", -100, 100, start.GreenHue, (s, v) => s.GreenHue = v);
        Add(groups, "Green saturation", -100, 100, start.GreenSaturation, (s, v) => s.GreenSaturation = v);
        Add(groups, "Blue hue", -100, 100, start.BlueHue, (s, v) => s.BlueHue = v);
        Add(groups, "Blue saturation", -100, 100, start.BlueSaturation, (s, v) => s.BlueSaturation = v);

        _glowStyle.SelectedIndex = start.GlowStyle;
        _vignetteStyle.SelectedIndex = start.VignetteStyle;

        var ok = new Button { Content = "Apply", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var reset = new Button { Content = "Reset" };
        ok.Click += (_, _) => Accept(start);
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) =>
        {
            foreach (var (slider, _, _, _) in _rows) slider.Value = 0;
        };
        groups.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, ok },
        });

        Content = new ScrollViewer { Content = groups };
    }

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
        void Show() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            Show();
        };
        Show();
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

    private void Accept(CameraRawSettings start)
    {
        var settings = new CameraRawSettings
        {
            GlowStyle = Math.Max(0, _glowStyle.SelectedIndex),
            VignetteStyle = Math.Max(0, _vignetteStyle.SelectedIndex),
        };
        foreach (var (slider, set, _, _) in _rows) set(settings, slider.Value);
        _result = settings.IsValid ? settings : null;
        Close();
    }

    /// <summary>The settings to apply, or null when the panel was dismissed or asks for nothing.</summary>
    public static async Task<CameraRawSettings?> Ask(Window owner, CameraRawSettings start)
    {
        var dialog = new CameraRawDialog(start);
        await dialog.ShowDialog(owner);
        return dialog._result is { } settings && !settings.IsIdentity ? settings : null;
    }
}
