namespace Compositor.Core.Pixels;

/// <summary>
/// Bloom: the bright parts of a picture spread into a soft glow around them.
/// <para>
/// The Mac build asks Core Image for this (<c>CIBloom</c>), which Windows has no equivalent of, so this is
/// the port's own take on the look rather than the same arithmetic: the picture's bright parts are lifted,
/// spread by a Gaussian, and screened back over what was there. What matters is that the amounts do what the
/// panel says — nothing at zero, more glow as they rise, and dark areas left nearly alone — so it is written
/// to have those properties rather than to match Core Image pixel for pixel.
/// </para>
/// </summary>
public static class BloomPixels
{
    /// <summary>
    /// Spreads <paramref name="rgba"/> in place: <paramref name="intensity"/> is how strongly the glow is
    /// laid over the picture (0 leaves it exactly as it was) and <paramref name="blurred"/> is the picture
    /// already lifted and softened, at the same size.
    /// </summary>
    public static void Screen(Span<byte> rgba, ReadOnlySpan<byte> blurred, int width, int height, int stride,
                              double intensity)
    {
        if (!(intensity > 0) || width <= 0 || height <= 0 || blurred.Length < rgba.Length) return;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var at = row + x * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var under = rgba[at + channel];
                    var above = blurred[at + channel] * intensity;
                    // Screening leaves what is under it the way it is where nothing glows over it.
                    var over = Math.Clamp(above, 0, 255);
                    var screened = 255 - (255 - under) * (255 - over) / 255.0;
                    rgba[at + channel] = (byte)Math.Clamp(Math.Round(screened), 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// The picture as the glow sees it: every pixel lifted by its own brightness, so what is dark stays where
    /// it is and what is bright is given something to spread. Alpha is left as it was, so a cut-out does not
    /// glow into the empty space around it.
    /// </summary>
    public static void Lift(ReadOnlySpan<byte> source, Span<byte> lifted, int width, int height, int stride,
                            double intensity)
    {
        if (width <= 0 || height <= 0 || lifted.Length < source.Length) return;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var at = row + x * 4;
                var alpha = source[at + 3];
                if (alpha == 0)
                {
                    lifted[at] = lifted[at + 1] = lifted[at + 2] = lifted[at + 3] = 0;
                    continue;
                }
                for (var channel = 0; channel < 3; channel++)
                {
                    // Straight-alpha, so the lift is measured on the colour rather than on the bytes.
                    var colour = source[at + channel] * 255.0 / alpha;
                    var raised = Math.Min(255, colour * (1 + intensity * colour / 255.0));
                    lifted[at + channel] = (byte)Math.Round(raised * alpha / 255.0);
                }
                lifted[at + 3] = alpha;
            }
        }
    }
}
