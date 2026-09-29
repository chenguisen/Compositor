using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;
using SkiaSharp;
using SelectionMode = Compositor.Core.Document.SelectionMode;

namespace Compositor.Desktop;

/// <summary>
/// Select ▸ Colour Range: the colour to look for, how near a colour counts, and what to do with what is
/// found. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class ColorRangeDialog : DialogWindow
{
    private readonly Slider _red = new() { Minimum = 0, Maximum = 255, Width = 220 };
    private readonly Slider _green = new() { Minimum = 0, Maximum = 255, Width = 220 };
    private readonly Slider _blue = new() { Minimum = 0, Maximum = 255, Width = 220 };
    private readonly Slider _fuzziness = new() { Minimum = 0, Maximum = 200, Value = 40, Width = 220 };
    private readonly CheckBox _invert = new() { Content = "Select everything else" };
    private readonly ComboBox _mode = new();
    private readonly Border _swatch = new() { Width = 44, Height = 22, BorderThickness = new Thickness(1) };
    private (SKColor Colour, int Fuzziness, bool Invert, SelectionMode Mode)? _result;

    private ColorRangeDialog(SKColor start)
    {
        Title = "Colour Range";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _red.Value = start.Red;
        _green.Value = start.Green;
        _blue.Value = start.Blue;
        _mode.ItemsSource = new[] { "Replace the selection", "Add to the selection", "Take from the selection" };
        _mode.SelectedIndex = 0;
        _mode.Width = 200;
        foreach (var slider in new[] { _red, _green, _blue }) slider.PropertyChanged += (_, change) => Swatch();

        var group = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        group.Children.Add(Row("Red", _red));
        group.Children.Add(Row("Green", _green));
        group.Children.Add(Row("Blue", _blue));
        group.Children.Add(Row("Colour", _swatch));
        group.Children.Add(Row("Fuzziness", _fuzziness));
        group.Children.Add(_invert);
        group.Children.Add(Row("Then", _mode));
        group.Children.Add(new TextBlock
        {
            Text = "Every pixel in the picture this near the colour is selected, wherever it is. The Mac "
                + "build picks its colours by clicking on the canvas and can look for several at once; this "
                + "panel takes one colour, and the mode adds it or takes it away.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) =>
        {
            _result = (Chosen(), (int)Math.Round(_fuzziness.Value), _invert.IsChecked == true,
                (SelectionMode)Math.Max(0, _mode.SelectedIndex));
            Close();
        };
        cancel.Click += (_, _) => Close();
        group.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        });
        Content = group;
        Swatch();
    }

    private SKColor Chosen() => new((byte)_red.Value, (byte)_green.Value, (byte)_blue.Value);

    private void Swatch() => _swatch.Background = new SolidColorBrush(Color.FromRgb(
        (byte)_red.Value, (byte)_green.Value, (byte)_blue.Value));

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    /// <summary>What was asked for, or null when the dialog was dismissed.</summary>
    public static async Task<(SKColor Colour, int Fuzziness, bool Invert, SelectionMode Mode)?> Ask(
        Window owner, SKColor start)
    {
        var dialog = new ColorRangeDialog(start);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
