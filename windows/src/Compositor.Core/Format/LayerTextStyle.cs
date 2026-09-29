namespace Compositor.Core.Format;

/// <summary>An editable text layer's metadata; the PNG remains the display and export fallback.</summary>
public sealed class LayerTextStyle
{
    public string Content { get; set; } = "Text";
    public string FontName { get; set; } = "Helvetica";
    public double FontSize { get; set; } = 72;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public double Tracking { get; set; }

    /// <summary>Baseline to baseline, in layer pixels. 0 is Auto: 120% of the font size.</summary>
    public double Leading { get; set; }

    /// <summary>Fixed paragraph bounds in layer pixels; nil for older point-text layers.</summary>
    public JsonSize? BoxSize { get; set; }

    /// <summary>Letters painted in a color other than `red`/`green`/`blue`, in UTF-16 offsets into `content`.</summary>
    public List<LayerTextColorRun>? ColorRuns { get; set; }

    /// <summary>Letters set in a face other than `fontName`, in the same offsets.</summary>
    public List<LayerTextFontRun>? FontRuns { get; set; }

    public double LineHeight => Leading > 0 ? Leading : FontSize * 1.2;

    public bool IsValid =>
        Content is not null && Content.Length <= 100_000 && BoxIsValid
        && double.IsFinite(FontSize) && FontSize is >= 1 and <= 2000
        && Color.IsValid(Red, Green, Blue)
        && double.IsFinite(Tracking) && Tracking is >= -100 and <= 1000
        && double.IsFinite(Leading) && Leading is >= 0 and <= 5000
        && ColorRunsAreValid && FontRunsAreValid;

    private bool BoxIsValid
    {
        get
        {
            if (BoxSize is not { } box) return true;
            return double.IsFinite(box.Width) && double.IsFinite(box.Height)
                && box.Width is >= 16 and <= Model.DocumentLimits.MaxSide
                && box.Height is >= 16 and <= Model.DocumentLimits.MaxSide
                && box.Width * box.Height <= Model.DocumentLimits.MaxSurfacePixels;
        }
    }

    /// <summary>Runs are sorted, do not overlap, have a positive length, and end within the content.</summary>
    private bool ColorRunsAreValid
    {
        get
        {
            if (ColorRuns is null) return true;
            var end = 0;
            foreach (var run in ColorRuns)
            {
                if (run.Location < end || run.Length <= 0 || run.Location > int.MaxValue - run.Length
                    || !Color.IsValid(run.Red, run.Green, run.Blue))
                {
                    return false;
                }
                end = run.Location + run.Length;
            }
            return ColorRuns.Count > 0 && end <= Content.Length;
        }
    }

    private bool FontRunsAreValid
    {
        get
        {
            if (FontRuns is null) return true;
            var end = 0;
            foreach (var run in FontRuns)
            {
                if (run.Location < end || run.Length <= 0 || run.Location > int.MaxValue - run.Length
                    || string.IsNullOrEmpty(run.FontName) || run.FontName.Length > 200
                    || ContainsNewline(run.FontName))
                {
                    return false;
                }
                end = run.Location + run.Length;
            }
            return FontRuns.Count > 0 && end <= Content.Length;
        }
    }

    /// <summary>Swift's `Character.isNewline`: the separators a font name may not hold.</summary>
    private static bool ContainsNewline(string value) =>
        value.Any(character => character is '\n' or '\r' or '\u000b' or '\u000c' or '\u0085' or '\u2028' or '\u2029');
}

public sealed class LayerTextColorRun
{
    public int Location { get; set; }
    public int Length { get; set; }
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
}

public sealed class LayerTextFontRun
{
    public int Location { get; set; }
    public int Length { get; set; }
    public string FontName { get; set; } = "";
}
