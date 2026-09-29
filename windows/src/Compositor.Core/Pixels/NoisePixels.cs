namespace Compositor.Core.Pixels;

/// <summary>Port of <c>Rendering/NoisePixels.c</c>.</summary>
public static class NoisePixels
{
    // A well-mixed 32-bit hash, so neighboring pixels get unrelated values.
    private static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16; x *= 0x7feb352dU;
            x ^= x >> 15; x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }
    }

    // Uniform in [0, 1).
    private static float Unit(uint key) => (float)(Hash(key) >> 8) * (1.0f / 16777216.0f);

    /// <summary>
    /// Adds noise to the color of premultiplied RGBA pixels, leaving alpha untouched and fully transparent
    /// pixels alone. <paramref name="amount"/> is Photoshop's percentage: uniform noise spans ±amount% of half
    /// the range, Gaussian noise has a standard deviation of two thirds of that. Monochromatic adds the same
    /// value to all three channels.
    /// </summary>
    public static void NoiseAdd(Span<byte> rgba, int width, int height, int stride,
                                float amount, bool gaussian, bool monochromatic, uint seed)
        => NoiseAddAt(rgba, width, height, stride, amount, gaussian, monochromatic, seed, 0, 0);

    /// <summary>
    /// As <see cref="NoiseAdd"/>, but pixel (x, y) is hashed at (originX + x, originY + y), so a piece of an
    /// image gets the same grain as that part of the whole.
    /// </summary>
    public static void NoiseAddAt(Span<byte> rgba, int width, int height, int stride,
                                  float amount, bool gaussian, bool monochromatic, uint seed,
                                  long originX, long originY)
    {
        float spread = amount / 100.0f * 127.5f;
        for (int y = 0; y < height; ++y)
        {
            int row = y * stride;
            for (int x = 0; x < width; ++x)
            {
                int o = row + x * 4;
                uint alpha = rgba[o + 3];
                if (alpha == 0) continue;
                uint px, py, baseKey;
                unchecked
                {
                    px = (uint)(originX + x);
                    py = (uint)(originY + y);
                    baseKey = Hash(seed ^ Hash(px * 0x9e3779b9U ^ Hash(py * 0x85ebca6bU)));
                }
                for (int c = 0; c < 3; ++c)
                {
                    uint key = monochromatic ? baseKey : baseKey + (uint)c * 0x9e3779b9U;
                    float n;
                    if (gaussian)
                    {
                        // Box–Muller: two uniform values make one normally distributed one.
                        float u1 = Unit(key), u2 = Unit(key ^ 0x68e31da4U);
                        n = MathF.Sqrt(-2.0f * MathF.Log(1.0f - u1)) * MathF.Cos(6.2831853f * u2)
                            * spread * (2.0f / 3.0f);
                    }
                    else
                    {
                        n = (Unit(key) * 2.0f - 1.0f) * spread;
                    }
                    float value = rgba[o + c] * 255.0f / alpha + n;
                    value = value < 0 ? 0 : value > 255 ? 255 : value;
                    rgba[o + c] = (byte)MathF.Round(value * alpha / 255.0f, MidpointRounding.AwayFromZero);
                }
            }
        }
    }
}
