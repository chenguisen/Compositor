using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// The panel for the filters that are not Camera Raw — vignette, tonal contrast or lens correction. Each is
/// one short group of amounts, and Apply runs it over the selected layer's pixels. Avalonia ships no such
/// dialog, so this is one.
/// </summary>
internal sealed class FilterDialog : Window
{
    /// <summary>Asks for the picture to be shown with this filter's amounts as they stand.</summary>
    public Action<FilterSettings>? Preview { get; set; }

    private readonly List<(Slider Slider, Action<FilterSettings, double> Set)> _rows = [];
    private readonly List<double> _fallbacks = [];
    private readonly List<(CheckBox Box, Action<FilterSettings, bool> Set, bool Fallback)> _checks = [];
    private FilterSettings? _result;

    private FilterDialog(FilterKind kind, FilterSettings start)
    {
        Title = kind switch
        {
            FilterKind.GaussianBlur => "Gaussian Blur",
            FilterKind.MotionBlur => "Motion Blur",
            FilterKind.BloomGlow => "Bloom / Glow",
            FilterKind.AddNoise => "Add Noise",
            FilterKind.Vignette => "Vignette",
            FilterKind.TonalContrast => "Tonal Contrast",
            _ => "Lens Correction",
        };
        Width = 420;
        Height = 360;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var defaults = new FilterSettings();
        var group = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        switch (kind)
        {
            case FilterKind.GaussianBlur:
                Add(group, "Radius, pixels", 0.1, 250, start.BlurRadius, defaults.BlurRadius, (s, v) => s.BlurRadius = v);
                break;
            case FilterKind.BloomGlow:
                Add(group, "Amount", 0, 100, start.BloomAmount, defaults.BloomAmount, (s, v) => s.BloomAmount = v);
                Add(group, "Radius, pixels", 1, 150, start.BloomRadius, defaults.BloomRadius, (s, v) => s.BloomRadius = v, "0");
                break;
            case FilterKind.MotionBlur:
                Add(group, "Angle, degrees", -90, 90, start.MotionAngle, defaults.MotionAngle, (s, v) => s.MotionAngle = v);
                Add(group, "Distance, pixels", 1, 2000, start.MotionDistance, defaults.MotionDistance, (s, v) => s.MotionDistance = v, "0");
                break;
            case FilterKind.AddNoise:
                Add(group, "Amount, %", 0.1, 400, start.NoiseAmount, defaults.NoiseAmount, (s, v) => s.NoiseAmount = v);
                Check(group, "Gaussian", start.NoiseGaussian, (s, v) => s.NoiseGaussian = v);
                Check(group, "Monochromatic", start.NoiseMonochromatic, (s, v) => s.NoiseMonochromatic = v);
                break;
            case FilterKind.Vignette:
                Add(group, "Amount", 0, 100, start.VignetteAmount, defaults.VignetteAmount, (s, v) => s.VignetteAmount = v);
                Add(group, "Red", 0, 1, start.VignetteRed, defaults.VignetteRed, (s, v) => s.VignetteRed = v, "0.00");
                Add(group, "Green", 0, 1, start.VignetteGreen, defaults.VignetteGreen, (s, v) => s.VignetteGreen = v, "0.00");
                Add(group, "Blue", 0, 1, start.VignetteBlue, defaults.VignetteBlue, (s, v) => s.VignetteBlue = v, "0.00");
                Add(group, "Midpoint", 0, 100, start.VignetteMidpoint, defaults.VignetteMidpoint, (s, v) => s.VignetteMidpoint = v);
                Add(group, "Roundness", -100, 100, start.VignetteRoundness, defaults.VignetteRoundness, (s, v) => s.VignetteRoundness = v);
                Add(group, "Feather", 0, 100, start.VignetteFeather, defaults.VignetteFeather, (s, v) => s.VignetteFeather = v);
                Add(group, "Highlights", 0, 100, start.VignetteHighlights, defaults.VignetteHighlights, (s, v) => s.VignetteHighlights = v);
                break;
            case FilterKind.TonalContrast:
                Add(group, "Amount", 0, 100, start.TonalAmount, defaults.TonalAmount, (s, v) => s.TonalAmount = v);
                Add(group, "Radius, pixels", 1, 100, start.TonalRadius, defaults.TonalRadius, (s, v) => s.TonalRadius = v, "0");
                Add(group, "Shadows", -100, 100, start.TonalShadows, defaults.TonalShadows, (s, v) => s.TonalShadows = v);
                Add(group, "Midtones", -100, 100, start.TonalMidtones, defaults.TonalMidtones, (s, v) => s.TonalMidtones = v);
                Add(group, "Highlights", -100, 100, start.TonalHighlights, defaults.TonalHighlights, (s, v) => s.TonalHighlights = v);
                break;
            default:
                Add(group, "Distortion", -100, 100, start.Distortion, defaults.Distortion, (s, v) => s.Distortion = v);
                break;
        }

        var ok = new Button { Content = "Apply", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var reset = new Button { Content = "Reset" };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) => Restore();
        group.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { reset, cancel, ok },
        });

        Content = new ScrollViewer { Content = group };
    }

    /// <summary>A box that follows the setting it belongs to, and what it started as for Reset.</summary>
    private void Check(StackPanel parent, string label, bool value, Action<FilterSettings, bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        parent.Children.Add(box);
        _checks.Add((box, set, value));
    }

    private void Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<FilterSettings, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 240 };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void Show() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            Show();
            Preview?.Invoke(Current());
        };
        Show();
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center },
                slider,
                readout,
            },
        });
        _rows.Add((slider, set));
        _fallbacks.Add(fallback);
    }

    /// <summary>Back to the filter's own defaults, which for a vignette is not zero.</summary>
    private void Restore()
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Slider.Value = _fallbacks[i];
        foreach (var (box, _, fallback) in _checks) box.IsChecked = fallback;
    }

    /// <summary>The amounts as the panel has them, for a preview of what they would do.</summary>
    private FilterSettings Current()
    {
        var settings = new FilterSettings();
        foreach (var (slider, set) in _rows) set(settings, slider.Value);
        foreach (var (box, set, _) in _checks) set(settings, box.IsChecked == true);
        return settings;
    }

    private void Accept()
    {
        _result = Current();
        Close();
    }

    /// <summary>The amounts to apply, or null when the panel was dismissed or asks for nothing.</summary>
    public static async Task<FilterSettings?> Ask(Window owner, FilterKind kind, FilterSettings start,
        Action<FilterSettings>? preview = null)
    {
        var dialog = new FilterDialog(kind, start) { Preview = preview };
        await dialog.ShowDialog(owner);
        return dialog._result is { } settings && settings.IsValid(kind) && settings.DoesAnything(kind) ? settings : null;
    }
}
