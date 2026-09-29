using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.IO;

/// <summary>
/// Writing the flattened document out. The extension chooses the format: PNG is streamed a band of tiles at
/// a time and works at any canvas size; JPEG has to be made whole, so it is refused for a canvas too big to
/// hold rather than quietly failing.
/// </summary>
public static class ImageWriter
{
    public const int DefaultQuality = 90;

    public static bool Write(CanvasDocument document, string path, int quality = DefaultQuality)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            TiledPngWriter.Write(document, path);
            return true;
        }
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return WriteJpeg(document, path, quality);
        }
        return false;
    }

    /// <summary>Whether the format named by the extension is one this writes.</summary>
    public static bool Knows(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WriteJpeg(CanvasDocument document, string path, int quality)
    {
        // Encoding is done on the whole picture at once, so a canvas that will not fit cannot be written.
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels) return false;
        using var flattened = DocumentRenderer.Render(document);
        using var image = SKImage.FromBitmap(flattened);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        if (encoded is null) return false;
        using var file = File.Create(path);
        encoded.SaveTo(file);
        return true;
    }
}
