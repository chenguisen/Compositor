using BitMiracle.LibTiff.Classic;
using ImageMagick;
using Sdcb.LibRaw;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO;

public enum ImportError
{
    /// <summary>The file could not be read at all.</summary>
    Unreadable,

    /// <summary>The file is a kind this build cannot read.</summary>
    Unsupported,

    /// <summary>The image would push the document past its side limit or its pixel budget.</summary>
    TooLarge,
}

public sealed class ImportException : Exception
{
    public ImportException(ImportError error, string? message = null) : base(message ?? error switch
    {
        ImportError.Unreadable => "The image could not be read. It may be damaged or unavailable.",
        ImportError.Unsupported => "Choose a JPEG, PNG, HEIC, TIFF, SVG or Photoshop (PSD) file.",
        _ => $"This import exceeds the {DocumentLimits.MaxSide:N0}-pixel side limit or the "
            + $"{DocumentLimits.DocumentBudgetMegapixels}-megapixel document budget.",
    })
    {
        Error = error;
    }

    public ImportError Error { get; }
}

/// <summary>
/// Reads an image file into the pixels a layer holds. Skia decodes JPEG, PNG, WebP, GIF and BMP; TIFF goes
/// through LibTiff, because Skia ships no TIFF decoder; SVG is drawn once into pixels by <c>Svg.Skia</c> and
/// does not stay vector, as the Mac build's does not. Camera RAW, HEIC and Photoshop files are not read
/// here yet and say so rather than failing obscurely.
/// </summary>
public static class ImageImporter
{
    /// <summary>What the importer will attempt, by extension.</summary>
    public static readonly string[] Extensions =
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg", ".tif", ".tiff", ".psd", ".psb",
         ".heic", ".heif", ".avif", ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2",
         ".raf", ".orf", ".rw2", ".raw", ".rwl", ".pef", ".3fr", ".erf", ".mrw", ".kdc", ".dcr", ".x3f", ".iiq"];

    /// <summary>The longest side of the small copy the layers panel draws, as the Mac build uses.</summary>
    public const int ThumbnailSide = 96;

    public static bool LooksImportable(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads one image. <paramref name="fitting"/> is the canvas an SVG should be drawn to fit.</summary>
    public static ImportedImage Decode(string path) => Decode(path, null, DocumentLimits.DocumentPixelBudget);

    public static ImportedImage Decode(string path, SKSizeI? fitting) =>
        Decode(path, fitting, DocumentLimits.DocumentPixelBudget);

    public static ImportedImage Decode(string path, SKSizeI? fitting, int remainingPixels)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeSvg(path, fitting, name, remainingPixels);
        }
        if (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeTiff(path, name, remainingPixels);
        }
        if (extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".psb", StringComparison.OrdinalIgnoreCase))
        {
            // Photoshop files become a document, layer by layer, through PsdImporter — not one image.
            throw new ImportException(ImportError.Unsupported,
                "Photoshop files are imported as a document, through PsdImporter, not as one image.");
        }
        if (HeicReads.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return DecodeHeic(path, name, remainingPixels);
        }
        if (RawReads.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return DecodeRaw(path, name, remainingPixels);
        }
        var bytes = Read(path);
        if (!TrySkia(bytes, name, remainingPixels, out var image))
        {
            throw SkiaReads.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? new ImportException(ImportError.Unreadable)
                : new ImportException(ImportError.Unsupported,
                    $"{extension} files are not read yet. PNG, JPEG, WebP, GIF, BMP, TIFF, SVG, HEIC and camera RAW are.");
        }
        return image;
    }

    /// <summary>HEIC and HEIF, which no Skia build for Windows reads: ImageMagick has the decoder.</summary>
    private static readonly string[] HeicReads = [".heic", ".heif", ".avif"];

    /// <summary>
    /// The camera RAW files, which only LibRaw reads. The list is the Mac build's `UTType.rawImage` — as many
    /// of the house formats as a camera is likely to write, since LibRaw is the decoder either way.
    /// </summary>
    private static readonly string[] RawReads =
    [
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2",
        ".raw", ".rwl", ".pef", ".3fr", ".erf", ".mrw", ".kdc", ".dcr", ".x3f", ".iiq", ".mos", ".mef",
    ];

    /// <summary>
    /// A HEIC through ImageMagick, which is the decoder this build of Skia does not carry. The camera's own
    /// orientation is applied, and the pixels come back straight-alpha sRGB, which is what a layer holds.
    /// </summary>
    private static ImportedImage DecodeHeic(string path, string name, int remainingPixels)
    {
        var (width, height) = HeicSize(path);
        Check(width, height, remainingPixels);
        try
        {
            using var image = new MagickImage(path);
            // Turn the picture the way the camera said it goes, as the Mac build's ImageIO import does.
            image.AutoOrient();
            using var pixels = image.GetPixels();
            var bytes = pixels.ToByteArray(PixelMapping.RGBA)
                        ?? throw new ImportException(ImportError.Unreadable);
            return FromRgba(bytes, (int)image.Width, (int)image.Height, name, remainingPixels);
        }
        catch (MagickException error)
        {
            throw new ImportException(ImportError.Unreadable, error.Message);
        }
    }

    /// <summary>The size a HEIC says it is, read from its header alone so a refused one is never decoded.</summary>
    private static (int Width, int Height) HeicSize(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            // ImageMagick counts pixels as unsigned; anything past the longest side is refused anyway.
            return (info.Width > int.MaxValue ? 0 : (int)info.Width, info.Height > int.MaxValue ? 0 : (int)info.Height);
        }
        catch (MagickException error)
        {
            throw new ImportException(ImportError.Unreadable, error.Message);
        }
    }

    /// <summary>
    /// A camera RAW through LibRaw: the camera's own white balance, demosaiced, in sRGB and 8 bits, which is
    /// what an import gives a layer. The size is known before the heavy work, so a picture that would not fit
    /// is refused without unpacking it.
    /// </summary>
    private static ImportedImage DecodeRaw(string path, string name, int remainingPixels)
    {
        try
        {
            return ReadRaw(path, name, remainingPixels);
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception error)
        {
            // LibRaw speaks for itself; a project asks for a project's own error.
            throw new ImportException(ImportError.Unreadable, error.Message);
        }
    }

    private static ImportedImage ReadRaw(string path, string name, int remainingPixels)
    {
        using var context = RawContext.OpenFile(path, LibRawInitFlags.None);
        Check(context.Width, context.Height, remainingPixels);
        context.Unpack();
        context.DcrawProcess(settings =>
        {
            // The white balance the camera chose, as the Mac build's RAW import shows it. The output colour
            // space is left at LibRaw's own default, which is sRGB, and the depth at 8 bits.
            settings.UseCameraWb = true;
            settings.OutputBps = 8;
        });
        using var image = context.MakeDcrawMemoryImage();
        if (image.Width <= 0 || image.Height <= 0 || image.Channels < 3 || image.Bits != 8)
        {
            throw new ImportException(ImportError.Unreadable, "That RAW did not come back as 8-bit colour.");
        }
        var length = image.DataSize;
        if (length <= 0) throw new ImportException(ImportError.Unreadable);
        var rgb = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(image.DataPointer, rgb, 0, length);
        var pixels = image.Width * image.Height;
        var rgba = new byte[(long)pixels * 4];
        for (var index = 0; index < pixels; index++)
        {
            var from = index * image.Channels;
            var to = index * 4;
            rgba[to] = rgb[from];
            rgba[to + 1] = rgb[from + 1];
            rgba[to + 2] = rgb[from + 2];
            rgba[to + 3] = 255;
        }
        return FromRgba(rgba, image.Width, image.Height, name, remainingPixels);
    }

    /// <summary>Refuses a picture that is too long a side, or too many pixels, to hold.</summary>
    private static void Check(int width, int height, int remainingPixels)
    {
        if (width <= 0 || height <= 0) throw new ImportException(ImportError.Unreadable);
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > remainingPixels)
        {
            throw new ImportException(ImportError.TooLarge);
        }
    }

    /// <summary>Straight-alpha RGBA into the bitmap a layer's pixels are held in.</summary>
    private static ImportedImage FromRgba(byte[] rgba, int width, int height, string name, int remainingPixels)
    {
        Check(width, height, remainingPixels);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul,
            SKColorSpace.CreateSrgb()));
        if (!bitmap.ReadyToDraw) throw new ImportException(ImportError.TooLarge);
        var target = bitmap.GetPixelSpan();
        var needed = width * height * 4;
        if (rgba.Length < needed) { bitmap.Dispose(); throw new ImportException(ImportError.Unreadable); }
        rgba.AsSpan(0, needed).CopyTo(target);
        return ImportedImage.Create(bitmap, name);
    }

    /// <summary>
    /// A TIFF through LibTiff, which is the only way to read one here: Skia ships no TIFF decoder. The
    /// library hands back rows bottom first and channels in ABGR order; a layer's pixels are top first, so
    /// both are turned round on the way in.
    /// </summary>
    private static ImportedImage DecodeTiff(string path, string name, int remainingPixels)
    {
        using var tiff = Tiff.Open(path, "r") ?? throw new ImportException(ImportError.Unreadable);
        var width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        var height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        if (width <= 0 || height <= 0) throw new ImportException(ImportError.Unreadable);
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > remainingPixels)
        {
            throw new ImportException(ImportError.TooLarge);
        }
        var raster = new int[width * height];
        if (!tiff.ReadRGBAImage(width, height, raster)) throw new ImportException(ImportError.Unreadable);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul,
            SKColorSpace.CreateSrgb()));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var source = (height - 1 - y) * width;
            var destination = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var pixel = raster[source + x];
                pixels[destination + x * 4] = (byte)pixel;
                pixels[destination + x * 4 + 1] = (byte)(pixel >> 8);
                pixels[destination + x * 4 + 2] = (byte)(pixel >> 16);
                pixels[destination + x * 4 + 3] = (byte)(pixel >> 24);
            }
        }
        return ImportedImage.Create(bitmap, name);
    }

    /// <summary>The kinds Skia decodes here, so a corrupt one of these is damage rather than a wrong format.</summary>
    private static readonly string[] SkiaReads = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    private static bool TrySkia(byte[] bytes, string name, int remainingPixels, out ImportedImage image)
    {
        image = null!;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return false;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul,
            SKColorSpace.CreateSrgb());
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return false;

        var oriented = codec.EncodedOrigin != SKEncodedOrigin.TopLeft || SwapsAxes(codec.EncodedOrigin)
            ? Oriented(decoded, codec.EncodedOrigin)
            : Copy(decoded);
        if (oriented.Width > DocumentLimits.MaxSide || oriented.Height > DocumentLimits.MaxSide
            || (long)oriented.Width * oriented.Height > remainingPixels)
        {
            oriented.Dispose();
            throw new ImportException(ImportError.TooLarge);
        }
        image = ImportedImage.Create(oriented, name);
        return true;
    }

    private static ImportedImage DecodeSvg(string path, SKSizeI? fitting, string name, int remainingPixels)
    {
        var svg = new Svg.Skia.SKSvg();
        if (svg.Load(path) is null || svg.Picture is not { } picture) throw new ImportException(ImportError.Unreadable);
        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ImportException(ImportError.Unreadable);
        var scale = fitting is { } fit
            ? Math.Min(fit.Width / bounds.Width, fit.Height / bounds.Height)
            : 1;
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide || (long)width * height > remainingPixels)
        {
            throw new ImportException(ImportError.TooLarge);
        }
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul,
            SKColorSpace.CreateSrgb()));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Scale((float)scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }
        return ImportedImage.Create(bitmap, name);
    }

    /// <summary>A canvas holding the image at its own size, as a new project starts.</summary>
    public static CanvasDocument NewDocument(ImportedImage image, double resolution = 72)
    {
        var document = new CanvasDocument(Guid.NewGuid(), image.Width, image.Height, resolution);
        document.Layers.Add(Layer(image));
        return document;
    }

    /// <summary>The image as a layer, at its own pixel size with its top-left on the canvas's.</summary>
    public static ImageLayer Layer(ImportedImage image) =>
        new(Guid.NewGuid(), image, new Model.LayerTransform(0, 0, image.Width, image.Height), image.Name);

    private static byte[] Read(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ImportException(ImportError.Unreadable, $"Could not read {path}: {error.Message}");
        }
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) => origin
        is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Turns the stored pixels the way the file's Exif orientation says they are meant to be seen. The
    /// matrix is the usual one: a,c,e across and b,d,f down, applied to the source.
    /// </summary>
    public static SKBitmap Oriented(SKBitmap source, SKEncodedOrigin origin)
    {
        var width = source.Width;
        var height = source.Height;
        var swaps = SwapsAxes(origin);
        var across = swaps ? height : width;
        var down = swaps ? width : height;
        var (a, b, c, d, e, f) = origin switch
        {
            SKEncodedOrigin.TopLeft => (1f, 0f, 0f, 1f, 0f, 0f),
            SKEncodedOrigin.TopRight => (-1f, 0f, 0f, 1f, width, 0f),
            SKEncodedOrigin.BottomRight => (-1f, 0f, 0f, -1f, width, height),
            SKEncodedOrigin.BottomLeft => (1f, 0f, 0f, -1f, 0f, height),
            SKEncodedOrigin.LeftTop => (0f, 1f, 1f, 0f, 0f, 0f),
            SKEncodedOrigin.RightTop => (0f, 1f, -1f, 0f, height, 0f),
            SKEncodedOrigin.RightBottom => (0f, -1f, -1f, 0f, height, width),
            _ => (0f, -1f, 1f, 0f, 0f, width),
        };
        var matrix = new SKMatrix
        {
            ScaleX = a,
            SkewY = b,
            SkewX = c,
            ScaleY = d,
            TransX = e,
            TransY = f,
            Persp2 = 1,
        };
        var target = new SKBitmap(new SKImageInfo(across, down, source.ColorType, source.AlphaType, source.ColorSpace));
        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(matrix);
        using var pixels = SKImage.FromBitmap(source);
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawImage(pixels, 0, 0,
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), paint);
        return target;
    }

    private static SKBitmap Copy(SKBitmap source)
    {
        var copy = new SKBitmap(source.Info);
        source.CopyTo(copy);
        return copy;
    }
}
