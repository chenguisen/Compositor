using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Format;
using TextAlignment = Compositor.Core.Format.TextAlignment;

namespace Compositor.Desktop;

/// <summary>
/// The Type tool's settings: the text, the face, and how it is set. Avalonia ships no such dialog, so this
/// is one — a box for the words, the fields a text layer holds, and OK.
/// </summary>
internal sealed class TextDialog : Window
{
    private readonly TextBox _content = new() { AcceptsReturn = true, Height = 120, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _font = new();
    private readonly TextBox _size = new();
    private readonly TextBox _colour = new();
    private readonly TextBox _tracking = new();
    private readonly TextBox _leading = new();
    private readonly ComboBox _alignment = new();
    private LayerTextStyle? _result;

    private TextDialog(string title, LayerTextStyle style)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _content.Text = style.Content;
        _font.Text = style.FontName;
        _size.Text = $"{style.FontSize:0.##}";
        _colour.Text = $"{style.Red * 255:0},{style.Green * 255:0},{style.Blue * 255:0}";
        _tracking.Text = $"{style.Tracking:0.##}";
        _leading.Text = $"{style.Leading:0.##}";
        _alignment.ItemsSource = new[] { "Left", "Centre", "Right" };
        _alignment.SelectedIndex = style.Alignment switch
        {
            TextAlignment.Center => 1,
            TextAlignment.Right => 2,
            _ => 0,
        };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => Accept(style);
        cancel.Click += (_, _) => Close();
        var rows = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Text" },
                _content,
                Row("Font", _font),
                Row("Size in pixels", _size),
                Row("Colour, red green blue 0-255", _colour),
                Row("Tracking, pixels between letters", _tracking),
                Row("Leading, line to line 0 for auto", _leading),
                new TextBlock { Text = "Alignment" },
                _alignment,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { cancel, ok },
                },
            },
        };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = { rows } } };
        Opened += (_, _) => _content.Focus();
    }

    private static Control Row(string label, Control field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = label }, field },
    };

    private void Accept(LayerTextStyle original)
    {
        var style = new LayerTextStyle
        {
            Content = _content.Text ?? "",
            FontName = string.IsNullOrWhiteSpace(_font.Text) ? original.FontName : _font.Text.Trim(),
            FontSize = original.FontSize,
            Red = original.Red,
            Green = original.Green,
            Blue = original.Blue,
            Alignment = _alignment.SelectedIndex switch
            {
                1 => TextAlignment.Center,
                2 => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            Tracking = original.Tracking,
            Leading = original.Leading,
            BoxSize = original.BoxSize,
            ColorRuns = original.ColorRuns,
            FontRuns = original.FontRuns,
        };
        if (Number(_size.Text, 1, 2000) is not { } size) return;
        style.FontSize = size;
        if (Colour(_colour.Text) is not { } colour) return;
        (style.Red, style.Green, style.Blue) = colour;
        if (Number(_tracking.Text, -100, 1000) is not { } tracking) return;
        style.Tracking = tracking;
        if (Number(_leading.Text, 0, 5000) is not { } leading) return;
        style.Leading = leading;
        if (!style.IsValid) return;
        _result = style;
        Close();
    }

    private double? Number(string? typed, double least, double most) =>
        double.TryParse((typed ?? "").Trim(), out var value) && value >= least && value <= most ? value : null;

    private (double Red, double Green, double Blue)? Colour(string? typed)
    {
        var parts = (typed ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var values = new double[3];
        for (var index = 0; index < 3; index++)
        {
            if (!double.TryParse(parts[index], out values[index]) || values[index] is < 0 or > 255) return null;
            values[index] /= 255;
        }
        return (values[0], values[1], values[2]);
    }

    /// <summary>The style that was asked for, or null when the dialog was dismissed.</summary>
    public static async Task<LayerTextStyle?> Ask(Window owner, string title, LayerTextStyle style)
    {
        var dialog = new TextDialog(title, style);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
