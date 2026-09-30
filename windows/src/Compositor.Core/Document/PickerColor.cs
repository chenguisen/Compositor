namespace Compositor.Core.Document;

/// <summary>
/// The colour the app's picker works in: hue in degrees around the wheel, saturation and brightness 0 to 1.
/// It is held as it is dragged rather than converted back from the colour each time, so hue survives a drag
/// through grey and saturation survives one through black, as Photoshop's own field does — a hue read back
/// from a grey is not the hue the pointer was at. One object is shared by the field, the strip and the
/// amounts, so a drag on any of them moves the rest.
/// </summary>
public sealed class PickerHsb
{
    public PickerHsb(double hue, double saturation, double brightness)
    {
        Hue = hue;
        Saturation = saturation;
        Brightness = brightness;
    }

    /// <summary>The colour a picker opens on: its hue, saturation and brightness as it stands.</summary>
    public PickerHsb((double Red, double Green, double Blue) colour) : this(0, 0, 0) => SetRgb(colour);

    public double Hue { get; set; }
    public double Saturation { get; set; }
    public double Brightness { get; set; }

    /// <summary>The colour this works out to, at full opacity.</summary>
    public (double Red, double Green, double Blue) Rgb
    {
        get
        {
            var hue = ((Hue % 360) + 360) % 360 / 60;
            var chroma = Brightness * Saturation;
            var second = chroma * (1 - Math.Abs(hue % 2 - 1));
            var least = Brightness - chroma;
            var (red, green, blue) = (int)hue switch
            {
                0 => (chroma, second, 0.0),
                1 => (second, chroma, 0.0),
                2 => (0.0, chroma, second),
                3 => (0.0, second, chroma),
                4 => (second, 0.0, chroma),
                _ => (chroma, 0.0, second),
            };
            return (red + least, green + least, blue + least);
        }
    }

    /// <summary>
    /// Follows a colour that came from elsewhere — the canvas, or the field's own numbers — keeping the hue it
    /// already had for a grey and the saturation for black.
    /// </summary>
    public void SetRgb((double Red, double Green, double Blue) colour)
    {
        var high = Math.Max(colour.Red, Math.Max(colour.Green, colour.Blue));
        var low = Math.Min(colour.Red, Math.Min(colour.Green, colour.Blue));
        var delta = high - low;
        Brightness = high;
        if (high > 0) Saturation = delta / high;
        if (delta <= 0) return;
        double hue;
        if (high == colour.Red) hue = (colour.Green - colour.Blue) / delta;
        else if (high == colour.Green) hue = (colour.Blue - colour.Red) / delta + 2;
        else hue = (colour.Red - colour.Green) / delta + 4;
        hue *= 60;
        Hue = hue < 0 ? hue + 360 : hue;
    }

    /// <summary>The colour as the six hex digits the picker's field shows, at eight bits a channel.</summary>
    public string Hex => Text(Rgb);

    /// <summary>A colour's six hex digits, upper case and without a leading hash.</summary>
    public static string Text((double Red, double Green, double Blue) colour) =>
        $"{Channel(colour.Red):X2}{Channel(colour.Green):X2}{Channel(colour.Blue):X2}";

    /// <summary>
    /// The colour six hex digits name, with or without a leading hash, and in either the long or the three
    /// digit short form. Nothing when the text is not hex digits.
    /// </summary>
    public static (double Red, double Green, double Blue)? FromHex(string text)
    {
        var digits = (text ?? "").Trim();
        if (digits.StartsWith('#')) digits = digits[1..];
        if (digits.Length == 3) digits = string.Concat(digits.Select(digit => $"{digit}{digit}"));
        if (digits.Length != 6 || !digits.All(Uri.IsHexDigit)) return null;
        var value = Convert.ToInt32(digits, 16);
        return (((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0);
    }

    /// <summary>One channel as the byte it is stored as.</summary>
    private static int Channel(double value) => Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
