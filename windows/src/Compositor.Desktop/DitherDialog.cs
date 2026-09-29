using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// Filter ▸ Dither's panel: the look and its amounts, and Apply to run it over the selected layer. Avalonia
/// ships no such dialog, so this is one.
/// </summary>
internal sealed class DitherDialog : DialogWindow
{
    private readonly List<(Slider Slider, Action<DitherSettings, double> Set)> _rows = [];
    private readonly List<double> _fallbacks = [];
    private readonly ComboBox _style = new();
    private readonly ComboBox _shape = new();
    private readonly ComboBox _colors = new();
    private readonly CheckBox _lightOnDark = new();
    private readonly TextBox _characters = new();
    private DitherSettings? _result;

    /// <summary>Asks for the picture to be shown with this look and its amounts as they stand.</summary>
    public Action<DitherStyle, DitherSettings>? Preview { get; set; }

    private static readonly string[] StyleNames =
    [
        "Atkinson (Classic Mac)", "Floyd–Steinberg", "Bayer 2 × 2", "Bayer 4 × 4", "Bayer 8 × 8",
        "Halftone Dots", "Halftone Lines", "Halftone Diamonds", "Mac Patterns", "ASCII",
    ];

    private DitherDialog(DitherSettings start)
    {
        Title = "Dither";
        Width = 460;
        Height = 660;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var defaults = new DitherSettings();
        var group = new StackPanel { Margin = new Thickness(16), Spacing = 4 };

        Choice(group, "Look", _style, StyleNames, 0);

        group.Children.Add(Heading("Pixels"));
        Add(group, "Pixel size", 1, 32, start.PixelSize, defaults.PixelSize, (s, v) => s.PixelSize = v, "0");
        Choice(group, "Pixel shape", _shape, ["Square", "Dot"], (int)start.PixelShape);

        group.Children.Add(Heading("Tones"));
        Add(group, "Levels", 2, 8, start.Levels, defaults.Levels, (s, v) => s.Levels = v, "0");
        Add(group, "Diffusion, %", 0, 100, start.Diffusion, defaults.Diffusion, (s, v) => s.Diffusion = v, "0");
        Add(group, "Density", -100, 100, start.Density, defaults.Density, (s, v) => s.Density = v);
        Add(group, "Contrast", -100, 100, start.Contrast, defaults.Contrast, (s, v) => s.Contrast = v);

        group.Children.Add(Heading("Halftone and characters"));
        Add(group, "Cell size", 4, 64, start.CellSize, defaults.CellSize, (s, v) => s.CellSize = v, "0");
        Add(group, "Angle, degrees", -90, 90, start.Angle, defaults.Angle, (s, v) => s.Angle = v);
        Add(group, "Text size", 6, 64, start.TextSize, defaults.TextSize, (s, v) => s.TextSize = v, "0");
        group.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Characters", Width = 130, VerticalAlignment = VerticalAlignment.Center },
                _characters,
            },
        });
        _characters.Text = start.Characters;
        _characters.Width = 240;
        Choice(group, "Marks", _lightOnDark, start.LightOnDark);

        group.Children.Add(Heading("Colours"));
        Choice(group, "Ink and paper", _colors, ["Black & White", "Two Colors", "Original"], (int)start.Colors);
        Add(group, "Dark red", 0, 1, start.DarkRed, defaults.DarkRed, (s, v) => s.DarkRed = v, "0.00");
        Add(group, "Dark green", 0, 1, start.DarkGreen, defaults.DarkGreen, (s, v) => s.DarkGreen = v, "0.00");
        Add(group, "Dark blue", 0, 1, start.DarkBlue, defaults.DarkBlue, (s, v) => s.DarkBlue = v, "0.00");
        Add(group, "Light red", 0, 1, start.LightRed, defaults.LightRed, (s, v) => s.LightRed = v, "0.00");
        Add(group, "Light green", 0, 1, start.LightGreen, defaults.LightGreen, (s, v) => s.LightGreen = v, "0.00");
        Add(group, "Light blue", 0, 1, start.LightBlue, defaults.LightBlue, (s, v) => s.LightBlue = v, "0.00");

        var ok = new Button { Content = "Apply", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var reset = new Button { Content = "Reset" };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) => Restore(defaults);
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

    private static Control Heading(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private static void Choice(StackPanel parent, string label, ComboBox box, string[] options, int selected)
    {
        box.ItemsSource = options;
        box.SelectedIndex = Math.Clamp(selected, 0, options.Length - 1);
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center },
                box,
            },
        });
    }

    /// <summary>"Light on dark" is a box, not a list, since it says yes or no.</summary>
    private static void Choice(StackPanel parent, string label, CheckBox box, bool selected)
    {
        box.Content = label;
        box.IsChecked = selected;
        parent.Children.Add(box);
    }

    private void Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<DitherSettings, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 240 };
        var readout = new TextBlock { Text = "", Width = 44, VerticalAlignment = VerticalAlignment.Center };
        void Show() => readout.Text = slider.Value.ToString(format);
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            Show();
            Preview?.Invoke(Style(), Current());
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

    /// <summary>Back to the look Dither opens with.</summary>
    private void Restore(DitherSettings defaults)
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Slider.Value = _fallbacks[i];
        _style.SelectedIndex = 0;
        _shape.SelectedIndex = 0;
        _colors.SelectedIndex = 0;
        _lightOnDark.IsChecked = defaults.LightOnDark;
        _characters.Text = defaults.Characters;
    }

    /// <summary>The look the panel has chosen.</summary>
    private DitherStyle Style() => (DitherStyle)Math.Max(0, _style.SelectedIndex);

    /// <summary>The amounts as the panel has them, for a preview of what they would do.</summary>
    private DitherSettings Current()
    {
        var settings = new DitherSettings
        {
            PixelShape = (DitherPixelShape)Math.Max(0, _shape.SelectedIndex),
            Colors = (DitherColors)Math.Max(0, _colors.SelectedIndex),
            LightOnDark = _lightOnDark.IsChecked == true,
            Characters = _characters.Text ?? DitherSettings.DefaultCharacters,
        };
        foreach (var (slider, set) in _rows) set(settings, slider.Value);
        return settings;
    }

    private void Accept()
    {
        var settings = new DitherSettings
        {
            PixelShape = (DitherPixelShape)Math.Max(0, _shape.SelectedIndex),
            Colors = (DitherColors)Math.Max(0, _colors.SelectedIndex),
            LightOnDark = _lightOnDark.IsChecked == true,
            Characters = _characters.Text ?? DitherSettings.DefaultCharacters,
        };
        foreach (var (slider, set) in _rows) set(settings, slider.Value);
        _result = settings;
        Close();
    }

    /// <summary>The look and its amounts, or null when the panel was dismissed.</summary>
    public static async Task<(DitherStyle Style, DitherSettings Settings)?> Ask(Window owner, DitherSettings start,
        Action<DitherStyle, DitherSettings>? preview = null)
    {
        var dialog = new DitherDialog(start) { Preview = preview };
        await dialog.ShowDialog(owner);
        if (dialog._result is not { } settings) return null;
        var style = (DitherStyle)Math.Max(0, dialog._style.SelectedIndex);
        return (style, settings);
    }
}
