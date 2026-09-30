using Compositor.Core.Document;

namespace Compositor.Core.Tests;

/// <summary>
/// The colour maths behind the picker: the wheel coming out as the colours it is drawn with, a hue that
/// survives a drag through grey or black, and the hex digits the field reads and writes.
/// </summary>
public class PickerColorTests
{
    [Theory]
    [InlineData(0, 1, 1, 1, 0, 0)]
    [InlineData(60, 1, 1, 1, 1, 0)]
    [InlineData(120, 1, 1, 0, 1, 0)]
    [InlineData(180, 1, 1, 0, 1, 1)]
    [InlineData(240, 1, 1, 0, 0, 1)]
    [InlineData(300, 1, 1, 1, 0, 1)]
    [InlineData(360, 1, 1, 1, 0, 0)]
    [InlineData(0, 0, 1, 1, 1, 1)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(200, 1, 0.5, 0, 1 / 3.0, 0.5)]
    public void TheWheelGivesTheColourItIsDrawnWith(double hue, double saturation, double brightness,
        double red, double green, double blue)
    {
        var colour = new PickerHsb(hue, saturation, brightness).Rgb;
        Assert.Equal(red, colour.Red, 3);
        Assert.Equal(green, colour.Green, 3);
        Assert.Equal(blue, colour.Blue, 3);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void AColourFromElsewhereLandsOnTheWheelWhereItBelongs(double red, double green, double blue)
    {
        var hsb = new PickerHsb((red, green, blue));
        Assert.Equal(1, hsb.Brightness, 6);
        Assert.Equal(1, hsb.Saturation, 6);
        // And the colour worked back out of the wheel is the one that went in.
        var back = hsb.Rgb;
        Assert.Equal(red, back.Red, 6);
        Assert.Equal(green, back.Green, 6);
        Assert.Equal(blue, back.Blue, 6);
    }

    /// <summary>
    /// The whole point of holding the hue rather than reading it back: a drag through grey would lose it, and
    /// the Mac build's field keeps it, which is what makes the hue band in the field stay put while the
    /// saturation goes to nothing.
    /// </summary>
    [Fact]
    public void AGreyKeepsTheHueItWasDraggedAt()
    {
        var hsb = new PickerHsb(200, 0.8, 1);
        hsb.SetRgb((0.5, 0.5, 0.5));
        Assert.Equal(200, hsb.Hue, 6);
        Assert.Equal(0, hsb.Saturation, 6);
        Assert.Equal(0.5, hsb.Brightness, 6);
    }

    /// <summary>And black keeps the saturation, so the colour that comes back out of the field is the one that
    /// went in rather than a grey.</summary>
    [Fact]
    public void BlackKeepsTheSaturation()
    {
        var hsb = new PickerHsb(200, 0.8, 1);
        hsb.SetRgb((0, 0, 0));
        Assert.Equal(0.8, hsb.Saturation, 6);
        Assert.Equal(0, hsb.Brightness, 6);
        // Which is what a drag down the field's right edge does: black at the bottom, full colour at the top.
        hsb.Brightness = 1;
        var back = hsb.Rgb;
        Assert.Equal(0.2, back.Red, 6);
        Assert.Equal(0.2 + 0.8 * (2 / 3.0), back.Green, 6);
        Assert.Equal(1, back.Blue, 6);
    }

    [Theory]
    [InlineData(1, 0, 0, "FF0000")]
    [InlineData(0, 1, 0, "00FF00")]
    [InlineData(0.5, 0.5, 0.5, "808080")]
    [InlineData(0.2, 0.4, 0.6, "336699")]
    public void AColourReadsAsItsHexDigits(double red, double green, double blue, string hex)
    {
        Assert.Equal(hex, PickerHsb.Text((red, green, blue)));
    }

    [Theory]
    [InlineData("FF0000")]
    [InlineData("ff0000")]
    [InlineData("#FF0000")]
    [InlineData("#ff0000")]
    [InlineData("F00")]
    [InlineData("#F00")]
    public void TheHexDigitsGiveBackTheColourTheyName(string text)
    {
        var colour = PickerHsb.FromHex(text);
        Assert.NotNull(colour);
        Assert.Equal(1, colour.Value.Red, 6);
        Assert.Equal(0, colour.Value.Green, 6);
        Assert.Equal(0, colour.Value.Blue, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("FF00")]
    [InlineData("FF00000")]
    [InlineData("GGGGGG")]
    [InlineData("-FFFFF")]
    public void SomethingThatIsNotHexDigitsGivesNothing(string text)
    {
        Assert.Null(PickerHsb.FromHex(text));
    }

    /// <summary>The hex the picker shows is the one its own field would take back, at eight bits a channel.</summary>
    [Fact]
    public void ThePickersHexIsTheColourItIsShowing()
    {
        var hsb = new PickerHsb(210, 0.75, 0.6);
        var back = PickerHsb.FromHex(hsb.Hex);
        Assert.NotNull(back);
        Assert.Equal(Math.Round(hsb.Rgb.Red * 255), Math.Round(back.Value.Red * 255));
        Assert.Equal(Math.Round(hsb.Rgb.Green * 255), Math.Round(back.Value.Green * 255));
        Assert.Equal(Math.Round(hsb.Rgb.Blue * 255), Math.Round(back.Value.Blue * 255));
    }
}
