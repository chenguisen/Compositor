using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// The pixel a document point lands on, which is what the Camera Raw panel's readout shows while the pointer
/// is over the picture. It reads the picture the panel is showing, not the layer as it was, so what it says is
/// what the pointer is over.
/// </summary>
public static class CameraRawSample
{
    /// <summary>
    /// The colour of the layer's own pixel under a document point, or null when the point is outside the layer
    /// or the pixel there is transparent — a transparent pixel has no colour to name.
    /// </summary>
    public static (int Red, int Green, int Blue)? Under(ImageLayer layer, SKPoint document)
    {
        if (layer.Asset is not { } asset || asset.Width <= 0 || asset.Height <= 0) return null;
        if (layer.Transform.InBox(document) is not { } at) return null;
        var x = (int)Math.Floor(at.X);
        var y = (int)Math.Floor(at.Y);
        if ((uint)x >= (uint)asset.Width || (uint)y >= (uint)asset.Height) return null;
        // A layer is held straight-alpha, so there is nothing to divide the colour back out of.
        var pixel = asset.Image.GetPixel(x, y);
        return pixel.Alpha == 0 ? null : (pixel.Red, pixel.Green, pixel.Blue);
    }
}
