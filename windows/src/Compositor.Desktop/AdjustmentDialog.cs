using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.Document;
using Compositor.Core.Format;

namespace Compositor.Desktop;

/// <summary>
/// An adjustment layer's settings, as the sliders its kind presents. The kind is fixed when the layer is
/// made, so one panel serves them all; whatever the panel does not show is carried over from the layer, so
/// Apply never loses anything. Avalonia ships no such dialog, so this is one.
/// </summary>
internal sealed class AdjustmentDialog : Window
{
    private readonly List<(Slider Slider, Action<LayerAdjustment, double> Set)> _rows = [];
    private readonly List<double> _fallbacks = [];
    private readonly List<(CheckBox Box, Action<LayerAdjustment, bool> Set, bool Fallback)> _boxes = [];
    private readonly ComboBox? _range;
    private readonly ComboBox? _levelsChannel;
    private LayerAdjustment? _result;

    private AdjustmentDialog(LayerAdjustment start)
    {
        var kind = start.Kind;
        Title = $"{LayerPlacement.Name(kind)} Adjustment";
        Width = 460;
        Height = 580;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var group = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        switch (kind)
        {
            case AdjustmentKind.HueSaturation:
            {
                var hsv = start.ResolvedHSV;
                _range = new ComboBox
                {
                    ItemsSource = HueBand.Ranges.Select(range => range.ToString()).ToList(),
                    SelectedIndex = HueBand.Ranges.IndexOf(hsv.Range),
                };
                group.Children.Add(Row("Range", _range));
                Add(group, "Hue", -180, 180, hsv.Current.Hue, 0, (s, v) => s.HsvSettings = Hsv(s, hue: v));
                Add(group, "Saturation", -100, 100, hsv.Current.Saturation, 0, (s, v) => s.HsvSettings = Hsv(s, saturation: v));
                Add(group, "Lightness", -100, 100, hsv.Current.Lightness, 0, (s, v) => s.HsvSettings = Hsv(s, lightness: v));
                Check(group, "Colorize", hsv.Colorize, (s, v) => s.HsvSettings = Hsv(s, colorize: v));
                break;
            }
            case AdjustmentKind.Levels:
            {
                var range = start.Levels.Ranges[(int)start.Levels.Channel];
                _levelsChannel = new ComboBox
                {
                    ItemsSource = new[] { "RGB", "Red", "Green", "Blue" },
                    SelectedIndex = (int)start.Levels.Channel,
                };
                group.Children.Add(Row("Channel", _levelsChannel));
                Add(group, "Black point", 0, 254, range.Black, 0, SetLevels);
                Add(group, "Gamma", 0.1, 9.99, range.Gamma, 1, SetLevels, "0.00");
                Add(group, "White point", 1, 255, range.White, 255, SetLevels);
                Add(group, "Output black", 0, 255, range.OutputBlack, 0, SetLevels);
                Add(group, "Output white", 0, 255, range.OutputWhite, 255, SetLevels);
                break;
            }
            case AdjustmentKind.Exposure:
                Add(group, "Exposure, stops", -20, 20, start.Exposure.Exposure, 0, (s, v) => s.ExposureSettings = Exposure(s, exposure: v), "0.00");
                Add(group, "Offset", -0.5, 0.5, start.Exposure.Offset, 0, (s, v) => s.ExposureSettings = Exposure(s, offset: v), "0.000");
                Add(group, "Gamma", 0.01, 9.99, start.Exposure.Gamma, 1, (s, v) => s.ExposureSettings = Exposure(s, gamma: v), "0.00");
                break;
            case AdjustmentKind.Grain:
                Add(group, "Amount", 0, 100, start.Grain.Amount, 25, (s, v) => s.GrainSettings = Grain(s, amount: v));
                Add(group, "Size", 0.5, 20, start.Grain.Size, 1.5, (s, v) => s.GrainSettings = Grain(s, size: v), "0.0");
                Add(group, "Roughness", 0, 100, start.Grain.Roughness, 50, (s, v) => s.GrainSettings = Grain(s, roughness: v));
                break;
            case AdjustmentKind.AddNoise:
                Add(group, "Amount, %", 0.1, 400, start.ResolvedNoiseAmount, 10, (s, v) => s.NoiseAmount = v);
                Check(group, "Gaussian", start.ResolvedNoiseGaussian, (s, v) => s.NoiseGaussian = v);
                Check(group, "Monochromatic", start.ResolvedNoiseMonochromatic, (s, v) => s.NoiseMonochromatic = v);
                break;
            case AdjustmentKind.GaussianBlur:
                Add(group, "Radius, pixels", 0.1, 250, start.GaussianRadius, 10, (s, v) => s.BlurRadius = v);
                break;
            case AdjustmentKind.MotionBlur:
                Add(group, "Angle, degrees", -90, 90, start.ResolvedMotionAngle, 0, (s, v) => s.MotionAngle = v);
                Add(group, "Distance, pixels", 1, 2000, start.ResolvedMotionDistance, 10, (s, v) => s.MotionDistance = v, "0");
                break;
            case AdjustmentKind.Invert:
                group.Children.Add(new TextBlock { Text = "Invert has no settings: it turns every pixel over." });
                break;
            case AdjustmentKind.BlackWhite:
                Add(group, "Reds", -200, 300, start.BlackWhite.Reds, 40, (s, v) => s.BlackWhiteSettings = Mix(s, reds: v));
                Add(group, "Yellows", -200, 300, start.BlackWhite.Yellows, 60, (s, v) => s.BlackWhiteSettings = Mix(s, yellows: v));
                Add(group, "Greens", -200, 300, start.BlackWhite.Greens, 40, (s, v) => s.BlackWhiteSettings = Mix(s, greens: v));
                Add(group, "Cyans", -200, 300, start.BlackWhite.Cyans, 60, (s, v) => s.BlackWhiteSettings = Mix(s, cyans: v));
                Add(group, "Blues", -200, 300, start.BlackWhite.Blues, 20, (s, v) => s.BlackWhiteSettings = Mix(s, blues: v));
                Add(group, "Magentas", -200, 300, start.BlackWhite.Magentas, 80, (s, v) => s.BlackWhiteSettings = Mix(s, magentas: v));
                Check(group, "Tint", start.BlackWhite.Tint, (s, v) => s.BlackWhiteSettings = Mix(s, tint: v));
                Add(group, "Tint hue", 0, 360, start.BlackWhite.TintHue, 40, (s, v) => s.BlackWhiteSettings = Mix(s, tintHue: v));
                Add(group, "Tint saturation", 0, 100, start.BlackWhite.TintSaturation, 20, (s, v) => s.BlackWhiteSettings = Mix(s, tintSaturation: v));
                break;
            default:
                Add(group, "Shadows: cyan to red", -100, 100, start.ColorBalance.ShadowCyanRed, 0, (s, v) => s.ColorBalanceSettings = Balance(s, shadowCyanRed: v));
                Add(group, "Shadows: magenta to green", -100, 100, start.ColorBalance.ShadowMagentaGreen, 0, (s, v) => s.ColorBalanceSettings = Balance(s, shadowMagentaGreen: v));
                Add(group, "Shadows: yellow to blue", -100, 100, start.ColorBalance.ShadowYellowBlue, 0, (s, v) => s.ColorBalanceSettings = Balance(s, shadowYellowBlue: v));
                Add(group, "Midtones: cyan to red", -100, 100, start.ColorBalance.MidCyanRed, 0, (s, v) => s.ColorBalanceSettings = Balance(s, midCyanRed: v));
                Add(group, "Midtones: magenta to green", -100, 100, start.ColorBalance.MidMagentaGreen, 0, (s, v) => s.ColorBalanceSettings = Balance(s, midMagentaGreen: v));
                Add(group, "Midtones: yellow to blue", -100, 100, start.ColorBalance.MidYellowBlue, 0, (s, v) => s.ColorBalanceSettings = Balance(s, midYellowBlue: v));
                Add(group, "Highlights: cyan to red", -100, 100, start.ColorBalance.HighlightCyanRed, 0, (s, v) => s.ColorBalanceSettings = Balance(s, highlightCyanRed: v));
                Add(group, "Highlights: magenta to green", -100, 100, start.ColorBalance.HighlightMagentaGreen, 0, (s, v) => s.ColorBalanceSettings = Balance(s, highlightMagentaGreen: v));
                Add(group, "Highlights: yellow to blue", -100, 100, start.ColorBalance.HighlightYellowBlue, 0, (s, v) => s.ColorBalanceSettings = Balance(s, highlightYellowBlue: v));
                Check(group, "Preserve luminosity", start.ColorBalance.PreserveLuminosity, (s, v) => s.ColorBalanceSettings = Balance(s, preserve: v));
                break;
        }

        var ok = new Button { Content = "Apply", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var reset = new Button { Content = "Reset" };
        ok.Click += (_, _) => Accept(start);
        cancel.Click += (_, _) => Close();
        reset.Click += (_, _) => Restore(new LayerAdjustment { Kind = kind });
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

    private LevelsChannelRange LevelsRange(LayerAdjustment settings) =>
        settings.Levels.Ranges[(int)(_levelsChannel is { SelectedIndex: >= 0 } box ? (LevelsChannel)box.SelectedIndex : settings.Levels.Channel)];

    private void SetLevels(LayerAdjustment settings, double value)
    {
        if (_levelsChannel is not { SelectedIndex: >= 0 } box) return;
        var channel = (LevelsChannel)box.SelectedIndex;
        settings.Levels.Channel = channel;
        var range = LevelsRange(settings);
        var updated = new LevelsChannelRange
        {
            Black = range.Black,
            Gamma = range.Gamma,
            White = range.White,
            OutputBlack = range.OutputBlack,
            OutputWhite = range.OutputWhite,
        };
        settings.Levels.Ranges[(int)channel] = updated;
    }

    private static HueSaturationSettings Hsv(LayerAdjustment settings,
        double? hue = null, double? saturation = null, double? lightness = null, bool? colorize = null)
    {
        var from = settings.ResolvedHSV;
        var next = new HueSaturationSettings
        {
            Range = from.Range,
            Colorize = colorize ?? from.Colorize,
            InvertRange = from.InvertRange,
        };
        foreach (var entry in from.Adjustments.Entries) next.Adjustments.Set(entry.Key, entry.Value);
        foreach (var entry in from.Bands.Entries) next.Bands.Set(entry.Key, entry.Value);
        var current = from.Current;
        next.Set(hue ?? current.Hue, saturation ?? current.Saturation, lightness ?? current.Lightness);
        return next;
    }

    private static ExposureSettings Exposure(LayerAdjustment settings,
        double? exposure = null, double? offset = null, double? gamma = null) => new()
    {
        Exposure = exposure ?? settings.Exposure.Exposure,
        Offset = offset ?? settings.Exposure.Offset,
        Gamma = gamma ?? settings.Exposure.Gamma,
    };

    private static GrainSettings Grain(LayerAdjustment settings, double? amount = null, double? size = null, double? roughness = null)
    {
        var grain = settings.Grain;
        return new GrainSettings { Amount = amount ?? grain.Amount, Size = size ?? grain.Size, Roughness = roughness ?? grain.Roughness, Seed = grain.Seed };
    }

    private static BlackWhiteSettings Mix(LayerAdjustment settings,
        double? reds = null, double? yellows = null, double? greens = null, double? cyans = null,
        double? blues = null, double? magentas = null, bool? tint = null,
        double? tintHue = null, double? tintSaturation = null)
    {
        var mix = settings.BlackWhite;
        return new BlackWhiteSettings
        {
            Reds = reds ?? mix.Reds,
            Yellows = yellows ?? mix.Yellows,
            Greens = greens ?? mix.Greens,
            Cyans = cyans ?? mix.Cyans,
            Blues = blues ?? mix.Blues,
            Magentas = magentas ?? mix.Magentas,
            Tint = tint ?? mix.Tint,
            TintHue = tintHue ?? mix.TintHue,
            TintSaturation = tintSaturation ?? mix.TintSaturation,
        };
    }

    private static ColorBalanceSettings Balance(LayerAdjustment settings,
        double? shadowCyanRed = null, double? shadowMagentaGreen = null, double? shadowYellowBlue = null,
        double? midCyanRed = null, double? midMagentaGreen = null, double? midYellowBlue = null,
        double? highlightCyanRed = null, double? highlightMagentaGreen = null, double? highlightYellowBlue = null,
        bool? preserve = null)
    {
        var balance = settings.ColorBalance;
        return new ColorBalanceSettings
        {
            ShadowCyanRed = shadowCyanRed ?? balance.ShadowCyanRed,
            ShadowMagentaGreen = shadowMagentaGreen ?? balance.ShadowMagentaGreen,
            ShadowYellowBlue = shadowYellowBlue ?? balance.ShadowYellowBlue,
            MidCyanRed = midCyanRed ?? balance.MidCyanRed,
            MidMagentaGreen = midMagentaGreen ?? balance.MidMagentaGreen,
            MidYellowBlue = midYellowBlue ?? balance.MidYellowBlue,
            HighlightCyanRed = highlightCyanRed ?? balance.HighlightCyanRed,
            HighlightMagentaGreen = highlightMagentaGreen ?? balance.HighlightMagentaGreen,
            HighlightYellowBlue = highlightYellowBlue ?? balance.HighlightYellowBlue,
            PreserveLuminosity = preserve ?? balance.PreserveLuminosity,
        };
    }

    private void Check(StackPanel parent, string label, bool value, Action<LayerAdjustment, bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        parent.Children.Add(box);
        _boxes.Add((box, set, value));
    }

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center },
            control,
        },
    };

    private void Add(StackPanel parent, string label, double least, double most, double value, double fallback,
        Action<LayerAdjustment, double> set, string format = "0.#")
    {
        var slider = new Slider { Minimum = least, Maximum = most, Value = value, Width = 220 };
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
                new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center },
                slider,
                readout,
            },
        });
        _rows.Add((slider, set));
        _fallbacks.Add(fallback);
    }

    /// <summary>Back to what the layer would be made with.</summary>
    private void Restore(LayerAdjustment fresh)
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Slider.Value = _fallbacks[i];
        foreach (var (box, _, fallback) in _boxes) box.IsChecked = fallback;
        if (_range is not null) _range.SelectedIndex = HueBand.Ranges.IndexOf(ColorRange.Master);
        if (_levelsChannel is not null) _levelsChannel.SelectedIndex = (int)fresh.Levels.Channel;
    }

    /// <summary>A copy of the layer's levels, so the panel's changes do not reach the layer until Apply.</summary>
    private static LevelsSettings Levels(LayerAdjustment settings)
    {
        var copy = new LevelsSettings { Channel = settings.Levels.Channel };
        copy.Ranges.Clear();
        foreach (var range in settings.Levels.Ranges)
        {
            copy.Ranges.Add(new LevelsChannelRange
            {
                Black = range.Black,
                Gamma = range.Gamma,
                White = range.White,
                OutputBlack = range.OutputBlack,
                OutputWhite = range.OutputWhite,
            });
        }
        return copy;
    }

    private void Accept(LayerAdjustment start)
    {
        // A copy of what the layer holds, so everything this panel does not show is kept as it was.
        var settings = new LayerAdjustment
        {
            Kind = start.Kind,
            Hue = start.Hue,
            Saturation = start.Saturation,
            Lightness = start.Lightness,
            Colorize = start.Colorize,
            Levels = Levels(start),
            Curves = start.Curves,
            ExposureSettings = start.ExposureSettings,
            GradientMapSettings = start.GradientMapSettings,
            GrainSettings = start.GrainSettings,
            BlackWhiteSettings = start.BlackWhiteSettings,
            ColorBalanceSettings = start.ColorBalanceSettings,
            BlurRadius = start.BlurRadius,
            MotionAngle = start.MotionAngle,
            MotionDistance = start.MotionDistance,
            NoiseAmount = start.NoiseAmount,
            NoiseGaussian = start.NoiseGaussian,
            NoiseMonochromatic = start.NoiseMonochromatic,
            NoiseSeed = start.NoiseSeed,
        };
        foreach (var (slider, set) in _rows) set(settings, slider.Value);
        foreach (var (box, set, _) in _boxes) set(settings, box.IsChecked == true);
        // The rows build the range-aware settings from the layer's own, so the range goes on afterwards.
        if (_range is { SelectedIndex: >= 0 } range)
        {
            settings.HsvSettings ??= Hsv(settings);
            settings.HsvSettings.Range = HueBand.Ranges[range.SelectedIndex];
        }
        _result = settings.IsValid ? settings : null;
        Close();
    }

    /// <summary>The settings to put back on the layer, or null when the panel was dismissed or asks nothing.</summary>
    public static async Task<LayerAdjustment?> Ask(Window owner, LayerAdjustment start)
    {
        var dialog = new AdjustmentDialog(start);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
