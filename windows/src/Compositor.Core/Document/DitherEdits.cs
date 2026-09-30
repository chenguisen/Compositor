using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>Dither's looks, in the order <see cref="DitherPixels"/> lists them.</summary>
public enum DitherStyle
{
    Atkinson,
    FloydSteinberg,
    Bayer2,
    Bayer4,
    Bayer8,
    Dots,
    Lines,
    Diamonds,
    Patterns,
    Ascii,
}

/// <summary>How a chunky pixel is drawn: a solid square, or a round dot with the gap colour showing around it.</summary>
public enum DitherPixelShape
{
    Square,
    Dot,
}

/// <summary>What the dots are made of.</summary>
public enum DitherColors
{
    BlackWhite,
    TwoColors,
    Original,
}

/// <summary>
/// Filter ▸ Dither's settings. A class rather than a struct: most of its defaults are not zero, and a record
/// struct zeroed by <c>new</c> would turn "the look Dither opens with" into "nothing".
/// </summary>
public sealed class DitherSettings
{
    /// <summary>The characters ASCII mode draws with, least ink first as the kernel reads them.</summary>
    public const string DefaultCharacters = " .:-=+*#%@";

    /// <summary>
    /// The looks in the groups the Mac build's panel lists them in — the diffusions, the ordered screens, the
    /// half-tone shapes, then the patterns and characters — with a rule drawn between the groups wherever they
    /// are offered. It holds every look once, in the enum's own order.
    /// </summary>
    public static readonly DitherStyle[][] Groups =
    [
        [DitherStyle.Atkinson, DitherStyle.FloydSteinberg],
        [DitherStyle.Bayer2, DitherStyle.Bayer4, DitherStyle.Bayer8],
        [DitherStyle.Dots, DitherStyle.Lines, DitherStyle.Diamonds],
        [DitherStyle.Patterns, DitherStyle.Ascii],
    ];

    /// <summary>Each dithered pixel covers this many layer pixels on a side, for chunky old-screen pixels.</summary>
    public double PixelSize { get; set; } = 2;
    public DitherPixelShape PixelShape { get; set; } = DitherPixelShape.Square;
    /// <summary>Halftone screen and character cells, in dithered pixels.</summary>
    public double CellSize { get; set; } = 8;
    /// <summary>ASCII's line height in layer pixels; the characters are about six tenths as wide.</summary>
    public double TextSize { get; set; } = 14;
    /// <summary>Halftone screen angle in degrees, −90 to 90.</summary>
    public double Angle { get; set; } = 45;
    /// <summary>Tones per channel for diffusion and ordered styles, 2 to 8. Two is pure 1-bit.</summary>
    public double Levels { get; set; } = 2;
    /// <summary>How much of the error diffusion passes on, 0 to 100%: less gives flatter areas.</summary>
    public double Diffusion { get; set; } = 100;
    /// <summary>−100 to 100: more ink (darker) or less before dithering.</summary>
    public double Density { get; set; }
    public double Contrast { get; set; }
    public DitherColors Colors { get; set; } = DitherColors.BlackWhite;
    /// <summary>Straight sRGB, 0 to 1.</summary>
    public double DarkRed { get; set; }
    public double DarkGreen { get; set; }
    public double DarkBlue { get; set; }
    public double LightRed { get; set; } = 1;
    public double LightGreen { get; set; } = 1;
    public double LightBlue { get; set; } = 1;
    /// <summary>Marks stand for the light tones, drawn in the light colour on the dark. Only halftones,
    /// patterns and ASCII draw marks; squares and tones use <see cref="Colors"/> the other way.</summary>
    public bool LightOnDark { get; set; } = true;
    /// <summary>ASCII's characters, in any order: they are sorted by how much ink each one has.</summary>
    public string Characters { get; set; } = DefaultCharacters;

    /// <summary>Error diffusion: each pixel's rounding error is passed to its neighbours.</summary>
    public static bool Diffuses(DitherStyle style) => style is DitherStyle.Atkinson or DitherStyle.FloydSteinberg;

    /// <summary>The half-tone shapes, which are drawn in cells that have a size and an angle.</summary>
    public static bool IsHalftone(DitherStyle style) => style is DitherStyle.Dots or DitherStyle.Lines or DitherStyle.Diamonds;

    /// <summary>Diffusion and ordered styles quantize to a number of tones; the rest draw marks in two.</summary>
    public static bool HasTones(DitherStyle style) =>
        Diffuses(style) || style is DitherStyle.Bayer2 or DitherStyle.Bayer4 or DitherStyle.Bayer8;

    /// <summary>
    /// Halftone shapes, patterns and characters mark one tone on the other, so which of the two the mark is
    /// matters — the Mac build's <c>drawsMarks</c>, and what its panel gates "Light on Dark" on.
    /// </summary>
    public static bool DrawsMarks(DitherStyle style) => !HasTones(style);

    private static double Clamp(double value, double least, double most, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, least, most) : fallback;

    /// <summary>The same amounts within the ranges the filter allows, as the Mac build clamps them.</summary>
    public DitherSettings Normalized() => new()
    {
        PixelSize = Math.Round(Clamp(PixelSize, 1, 32, 2)),
        PixelShape = PixelShape,
        CellSize = Math.Round(Clamp(CellSize, 4, 64, 8)),
        TextSize = Math.Round(Clamp(TextSize, 6, 64, 14)),
        Angle = Clamp(Angle, -90, 90, 45),
        Levels = Math.Round(Clamp(Levels, 2, 8, 2)),
        Diffusion = Clamp(Diffusion, 0, 100, 100),
        Density = Clamp(Density, -100, 100, 0),
        Contrast = Clamp(Contrast, -100, 100, 0),
        Colors = Colors,
        DarkRed = Clamp(DarkRed, 0, 1, 0),
        DarkGreen = Clamp(DarkGreen, 0, 1, 0),
        DarkBlue = Clamp(DarkBlue, 0, 1, 0),
        LightRed = Clamp(LightRed, 0, 1, 1),
        LightGreen = Clamp(LightGreen, 0, 1, 1),
        LightBlue = Clamp(LightBlue, 0, 1, 1),
        LightOnDark = LightOnDark,
        Characters = Characters,
    };
}

/// <summary>
/// Filter ▸ Dither: the picture reduced to ink. Chunky pixels are averaged down, dithered at that size, then
/// blown back up without smoothing, so each dithered pixel is one block of the layer's own.
/// </summary>
public static class DitherEdits
{
    /// <summary>
    /// Dithers a layer's pixels. False when the layer cannot take it, or when the kernel could not have the
    /// memory it needs — in which case the layer is left exactly as it was.
    /// </summary>
    public static bool Apply(CanvasDocument document, Guid layerID, DitherStyle style, DitherSettings settings)
    {
        var values = settings.Normalized();
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return false;
        if (!FilterSurface.Begin(layer, 0, out var work, out var placement)) return false;
        using var _ = work;
        using var was = document.Selection.Path is null ? null : FilterSurface.Copy(work);

        // ASCII draws its characters at full resolution: shrinking first would blur them into one another.
        var block = style == DitherStyle.Ascii ? 1 : (int)values.PixelSize;
        if (block > 1)
        {
            // Chunky pixels: average the layer down, dither that, then blow it back up without smoothing.
            var smallWidth = (work.Width + block - 1) / block;
            var smallHeight = (work.Height + block - 1) / block;
            using var small = Bitmaps.Scale(work, smallWidth, smallHeight);
            if (!Dither(small, style, values)) return false;
            using (var canvas = new SKCanvas(work))
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(small, SKRect.Create(0, 0, small.Width * block, small.Height * block),
                    new SKSamplingOptions(SKFilterMode.Nearest), paint);
            }
            if (values.PixelShape == DitherPixelShape.Dot)
            {
                var gap = values.Colors == DitherColors.TwoColors
                    ? new[] { Byte(values.DarkRed), Byte(values.DarkGreen), Byte(values.DarkBlue) }
                    : new byte[3];
                DitherPixels.DitherDots(work.GetPixelSpan(), work.Width, work.Height, work.RowBytes, block, gap);
            }
        }
        else if (!Dither(work, style, values))
        {
            return false;
        }

        if (was is not null) FilterSurface.Keep(document, was, work, placement);
        FilterSurface.Finish(layer, work, placement);
        return true;
    }

    /// <summary>The kernel over one buffer, in the premultiplied form it reads.</summary>
    private static bool Dither(SKBitmap image, DitherStyle style, DitherSettings settings)
    {
        // The kernel indexes the glyph maps whatever the look is, so they are never null, only empty.
        var glyphs = style == DitherStyle.Ascii
            ? Glyphs(string.IsNullOrEmpty(settings.Characters) ? DitherSettings.DefaultCharacters : settings.Characters,
                (int)settings.TextSize)
            : (Maps: Array.Empty<byte>(), Coverage: Array.Empty<float>(), Width: 1, Height: 1);
        var twoColors = settings.Colors == DitherColors.TwoColors;
        var parameters = new DitherParams
        {
            Style = (int)style,
            Levels = (int)settings.Levels,
            Diffusion = (float)(settings.Diffusion / 100),
            Density = (float)(settings.Density / 100),
            Contrast = (float)(settings.Contrast / 100),
            Cell = (int)settings.CellSize,
            Angle = (float)(settings.Angle * Math.PI / 180),
            LightOnDark = settings.LightOnDark,
            OriginalColors = settings.Colors == DitherColors.Original,
            Dark = twoColors
                ? [Byte(settings.DarkRed), Byte(settings.DarkGreen), Byte(settings.DarkBlue)]
                : [0, 0, 0],
            Light = twoColors
                ? [Byte(settings.LightRed), Byte(settings.LightGreen), Byte(settings.LightBlue)]
                : [255, 255, 255],
            GlyphWidth = Math.Max(1, glyphs.Width),
            GlyphHeight = Math.Max(1, glyphs.Height),
            GlyphCount = glyphs.Coverage.Length,
            Glyphs = glyphs.Maps,
            GlyphCoverage = glyphs.Coverage,
        };
        return DitherPixels.DitherApply(image.GetPixelSpan(), image.Width, image.Height, image.RowBytes, parameters) != 0;
    }

    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>
    /// Each distinct character drawn into a cell of monospaced text, <paramref name="lineHeight"/> tall and one
    /// character wide, on a shared baseline, sorted from least ink to most — 255 being fully inked.
    /// </summary>
    private static (byte[] Maps, float[] Coverage, int Width, int Height) Glyphs(string characters, int lineHeight)
    {
        var typeface = SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
        var height = Math.Max(1, lineHeight);
        using var font = new SKFont(typeface, Math.Max(1, lineHeight / 1.2f));
        var metrics = font.Metrics;
        var width = Math.Max(1, (int)Math.Round(font.MeasureText("M")));
        // Centred on the font's own ink, as Core Text lays a line out in a cell.
        var baseline = (height - (-metrics.Ascent + metrics.Descent)) / 2 - metrics.Ascent;

        var drawn = new List<(byte[] Map, float Coverage)>();
        var seen = new HashSet<char>();
        var map = new byte[width * height];
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        foreach (var character in characters)
        {
            if (!seen.Add(character)) continue;
            Array.Clear(map);
            using (var bitmap = new SKBitmap(info))
            {
                bitmap.Erase(SKColors.Black);
                using (var canvas = new SKCanvas(bitmap))
                {
                    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                    var across = Math.Max(0, (width - font.MeasureText(character.ToString())) / 2);
                    canvas.DrawText(character.ToString(), across, baseline, SKTextAlign.Left, font, paint);
                }
                var pixels = bitmap.GetPixelSpan();
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        // White ink on black: any channel is the ink's coverage there.
                        map[y * width + x] = pixels[(y * bitmap.RowBytes) + x * 4];
                    }
                }
            }
            long ink = 0;
            foreach (var value in map) ink += value;
            drawn.Add(((byte[])map.Clone(), (float)ink / (255f * width * height)));
        }
        drawn.Sort((left, right) => left.Coverage.CompareTo(right.Coverage));
        var maps = new byte[drawn.Count * width * height];
        var coverage = new float[drawn.Count];
        for (var index = 0; index < drawn.Count; index++)
        {
            drawn[index].Map.CopyTo(maps, index * width * height);
            coverage[index] = drawn[index].Coverage;
        }
        return (maps, coverage, width, height);
    }
}
