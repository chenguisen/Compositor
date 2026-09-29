using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Model;

namespace Compositor.Desktop;

/// <summary>
/// File ▸ New: how big the canvas is and how finely it is measured, with the sizes a screen or a post is
/// usually wanted at a menu away. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class NewDocumentDialog : Window
{
    /// <summary>A size worth starting from, as the Mac build's presets menu lists them.</summary>
    private static readonly (string Title, int Width, int Height)[] Presets =
    [
        ("Custom", 0, 0),
        ("4K", 3840, 2160),
        ("1440p", 2560, 1440),
        ("1080p", 1920, 1080),
        ("Instagram square", 1080, 1080),
        ("Instagram portrait", 1080, 1350),
        ("Instagram story", 1080, 1920),
        ("YouTube thumbnail", 1080, 608),
        ("A4 at 300 per inch", 2480, 3508),
        ("A3 at 300 per inch", 3508, 4961),
    ];

    private readonly TextBox _width = new() { Text = "1920", Width = 100 };
    private readonly TextBox _height = new() { Text = "1080", Width = 100 };
    private readonly TextBox _resolution = new() { Text = "72", Width = 100 };
    private readonly ComboBox _preset = new() { Width = 200 };
    private readonly TextBlock _size = new() { Margin = new Thickness(0, 4, 0, 0) };
    private bool _choosing;
    private (int Width, int Height, double Resolution)? _result;

    private NewDocumentDialog()
    {
        Title = "New Project";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _preset.ItemsSource = Presets.Select(preset => preset.Title).ToList();
        _preset.SelectedIndex = 0;
        _preset.SelectionChanged += (_, _) => ChoosePreset();
        _width.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) ShowSize();
        };
        _height.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) ShowSize();
        };

        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                Row("Preset", _preset),
                Row("Width, pixels", _width),
                Row("Height, pixels", _height),
                Row("Resolution, per inch", _resolution),
                _size,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };
        ShowSize();
        Opened += (_, _) =>
        {
            _width.Focus();
            _width.SelectAll();
        };
    }

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = label, Width = 140, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    /// <summary>A preset chosen from the menu fills the two fields in; typing in them goes back to Custom.</summary>
    private void ChoosePreset()
    {
        if (_choosing || _preset.SelectedIndex <= 0) return;
        var (_, width, height) = Presets[_preset.SelectedIndex];
        _choosing = true;
        try
        {
            _width.Text = width.ToString();
            _height.Text = height.ToString();
        }
        finally
        {
            _choosing = false;
        }
        ShowSize();
    }

    /// <summary>What the canvas would cost, which is what says whether it is a sensible size.</summary>
    private void ShowSize()
    {
        if (_choosing) return;
        if (!int.TryParse(_width.Text, out var width) || !int.TryParse(_height.Text, out var height)
            || width < 1 || height < 1)
        {
            _size.Text = "A canvas is at least one pixel each way";
            return;
        }
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide)
        {
            _size.Text = $"A side is at most {DocumentLimits.MaxSide} pixels";
            return;
        }
        var megapixels = width * (double)height / 1_000_000;
        var megabytes = width * (long)height * 4 / 1024.0 / 1024.0;
        _size.Text = $"{megapixels:0.#} megapixels, about {megabytes:0} MB while it is open";
        if ((long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            _size.Text += $" — too big to hold (the most is {DocumentLimits.MaxSurfaceMegapixels} megapixels)";
        }
    }

    private void Accept()
    {
        if (!int.TryParse(_width.Text, out var width) || !int.TryParse(_height.Text, out var height)) return;
        if (width < 1 || height < 1 || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide) return;
        if ((long)width * height > DocumentLimits.MaxSurfacePixels) return;
        if (!double.TryParse(_resolution.Text, out var resolution) || !double.IsFinite(resolution)
            || resolution is < 1 or > 9600)
        {
            return;
        }
        _result = (width, height, resolution);
        Close();
    }

    /// <summary>What was asked for, or null when the dialog was dismissed or the numbers made no sense.</summary>
    public static async Task<(int Width, int Height, double Resolution)?> Ask(Window owner)
    {
        var dialog = new NewDocumentDialog();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
