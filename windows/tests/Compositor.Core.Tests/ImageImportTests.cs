using BitMiracle.LibTiff.Classic;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Reading images in: what comes back, what is refused, and how Exif orientation is applied.</summary>
public class ImageImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorImport-" + Guid.NewGuid().ToString("N"));

    public ImageImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static SKBitmap Solid(SKColor colour, int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return bitmap;
    }

    [Fact]
    public void APngComesInWithItsNameItsPixelsAndASmallCopy()
    {
        using var source = Solid(new SKColor(10, 200, 90), 300, 120);
        var path = Write("Holiday.png", PngCodec.Encode(source));

        using var imported = ImageImporter.Decode(path);
        Assert.Equal("Holiday", imported.Name);
        Assert.Equal(300, imported.Width);
        Assert.Equal(120, imported.Height);
        Assert.Equal(10, imported.Image.GetPixel(150, 60).Red);
        Assert.Equal(200, imported.Image.GetPixel(150, 60).Green);
        // The small copy keeps the shape and fits the panel.
        Assert.Equal(ImageImporter.ThumbnailSide, imported.Thumbnail.Width);
        Assert.Equal(38, imported.Thumbnail.Height);
    }

    [Fact]
    public void AnImagePastTheRemainingBudgetIsRefused()
    {
        using var source = Solid(SKColors.Red, 40, 40);
        var path = Write("Big.png", PngCodec.Encode(source));
        var error = Assert.Throws<ImportException>(() => ImageImporter.Decode(path, null, remainingPixels: 100));
        Assert.Equal(ImportError.TooLarge, error.Error);
    }

    [Fact]
    public void AFileThatIsNotAnImageIsRefusedAsUnreadable()
    {
        var path = Write("Broken.png", "this is not a png"u8.ToArray());
        Assert.Equal(ImportError.Unreadable, Assert.Throws<ImportException>(() => ImageImporter.Decode(path)).Error);
    }

    [Fact]
    public void AFormatThatIsNotReadYetSaysSo()
    {
        var path = Write("Drawing.xyz", "not an image at all"u8.ToArray());
        var error = Assert.Throws<ImportException>(() => ImageImporter.Decode(path));
        Assert.Equal(ImportError.Unsupported, error.Error);
        Assert.Contains("not read yet", error.Message, StringComparison.Ordinal);
        // The message names what is read, HEIC and camera RAW included.
        Assert.Contains("HEIC", error.Message, StringComparison.Ordinal);
        Assert.Contains("camera RAW", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Photo.heic")]
    [InlineData("Photo.heif")]
    [InlineData("Photo.dng")]
    [InlineData("Photo.CR2")]
    [InlineData("Photo.NEF")]
    [InlineData("Photo.arw")]
    public void AHeicOrRawThatCannotBeReadIsUnreadable(string fileName)
    {
        // A file named as one of the formats the importer claims, holding nonsense: the decoder refuses it,
        // and that is a different answer from "this format is not read at all".
        var path = Write(fileName, "not really a photograph"u8.ToArray());
        var error = Assert.Throws<ImportException>(() => ImageImporter.Decode(path));
        Assert.Equal(ImportError.Unreadable, error.Error);
    }

    [Theory]
    [InlineData(".heic", true)]
    [InlineData(".HEIF", true)]
    [InlineData(".dng", true)]
    [InlineData(".rw2", true)]
    [InlineData(".xyz", false)]
    public void WhatTheImporterClaimsToReadIsWhatItSays(string extension, bool importable)
    {
        Assert.Equal(importable, ImageImporter.LooksImportable("Photo" + extension));
        Assert.Equal(importable, ImageImporter.Extensions.Contains(extension.ToLowerInvariant()));
    }

    [Fact]
    public void ASvgIsDrawnAtTheSizeItDeclares()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="20" viewBox="0 0 40 20">
              <rect x="0" y="0" width="40" height="20" fill="#ff0000"/>
            </svg>
            """;
        var path = Write("Mark.svg", System.Text.Encoding.UTF8.GetBytes(svg));

        using var imported = ImageImporter.Decode(path);
        Assert.Equal("Mark", imported.Name);
        Assert.Equal(40, imported.Width);
        Assert.Equal(20, imported.Height);
        Assert.Equal(255, imported.Image.GetPixel(20, 10).Red);
        Assert.Equal(0, imported.Image.GetPixel(20, 10).Green);
    }

    [Fact]
    public void AnSvgDrawnToFitACanvasFillsIt()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" viewBox="0 0 10 10">
              <rect x="0" y="0" width="10" height="10" fill="#0000ff"/>
            </svg>
            """;
        var path = Write("Square.svg", System.Text.Encoding.UTF8.GetBytes(svg));

        using var imported = ImageImporter.Decode(path, new SKSizeI(64, 48));
        Assert.Equal(48, imported.Width);
        Assert.Equal(48, imported.Height);
    }

    [Fact]
    public void ATiffComesInTheRightWayUpAndTheRightWayRound()
    {
        var path = Path.Combine(_root, "Scan.tiff");
        // Written with LibTiff so the reader has a real file to work on, lettering the pixels A to F.
        using (var writer = Tiff.Open(path, "w"))
        {
            Assert.NotNull(writer);
            writer.SetField(TiffTag.IMAGEWIDTH, 3);
            writer.SetField(TiffTag.IMAGELENGTH, 2);
            writer.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            writer.SetField(TiffTag.BITSPERSAMPLE, 8);
            writer.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
            writer.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            writer.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            writer.SetField(TiffTag.COMPRESSION, Compression.NONE);
            writer.SetField(TiffTag.ROWSPERSTRIP, 2);
            byte[] scanlines = [1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 0, 0, 5, 0, 0, 6, 0, 0];
            Assert.Equal(scanlines.Length, writer.WriteEncodedStrip(0, scanlines, scanlines.Length));
        }

        using var imported = ImageImporter.Decode(path);
        Assert.Equal("Scan", imported.Name);
        Assert.Equal(3, imported.Width);
        Assert.Equal(2, imported.Height);
        Assert.Equal(1, imported.Image.GetPixel(0, 0).Red); // A stays at the top left
        Assert.Equal(3, imported.Image.GetPixel(2, 0).Red); // C at the top right
        Assert.Equal(4, imported.Image.GetPixel(0, 1).Red); // D at the bottom left, so rows are not flipped
        Assert.Equal(6, imported.Image.GetPixel(2, 1).Red);
    }

    [Fact]
    public void ANewDocumentHoldsTheImageAtItsOwnSize()
    {
        using var source = Solid(new SKColor(7, 8, 9), 64, 48);
        var path = Write("Plate.png", PngCodec.Encode(source));
        using var imported = ImageImporter.Decode(path);

        using var document = ImageImporter.NewDocument(imported);
        Assert.Equal(64, document.Width);
        Assert.Equal(48, document.Height);
        var layer = Assert.Single(document.Layers);
        Assert.Equal(0, layer.Transform.X);
        Assert.Equal(0, layer.Transform.Y);
        Assert.Equal(64, layer.Transform.Width);
        Assert.Equal(48, layer.Transform.Height);
    }

    /// <summary>
    /// The eight Exif orientations, against a 3x2 picture whose pixels are lettered A to F so the way the
    /// stored pixels are turned can be read straight off the result.
    /// </summary>
    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, "ABC/DEF")]
    [InlineData(SKEncodedOrigin.TopRight, "CBA/FED")]
    [InlineData(SKEncodedOrigin.BottomRight, "FED/CBA")]
    [InlineData(SKEncodedOrigin.BottomLeft, "DEF/ABC")]
    [InlineData(SKEncodedOrigin.LeftTop, "AD/BE/CF")]
    [InlineData(SKEncodedOrigin.RightTop, "DA/EB/FC")]
    [InlineData(SKEncodedOrigin.RightBottom, "FC/EB/DA")]
    [InlineData(SKEncodedOrigin.LeftBottom, "CF/BE/AD")]
    public void AnExifOrientationTurnsTheStoredPixels(SKEncodedOrigin origin, string expected)
    {
        var letters = new[] { "A", "B", "C", "D", "E", "F" };
        using var stored = new SKBitmap(Bitmaps.ColorInfo(3, 2));
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                var shade = (byte)(letters[y * 3 + x][0] - 'A' + 1);
                stored.SetPixel(x, y, new SKColor(shade, 0, 0));
            }
        }

        using var turned = ImageImporter.Oriented(stored, origin);
        var rows = new List<string>();
        for (var y = 0; y < turned.Height; y++)
        {
            var row = "";
            for (var x = 0; x < turned.Width; x++) row += letters[turned.GetPixel(x, y).Red - 1];
            rows.Add(row);
        }
        Assert.Equal(expected, string.Join("/", rows));
    }

    [Fact]
    public void AnUprightImageIsNotCopiedNeedlessly()
    {
        using var stored = new SKBitmap(Bitmaps.ColorInfo(4, 3));
        stored.Erase(SKColors.Red);
        using var turned = ImageImporter.Oriented(stored, SKEncodedOrigin.TopLeft);
        Assert.Equal(4, turned.Width);
        Assert.Equal(3, turned.Height);
        Assert.Equal(255, turned.GetPixel(1, 1).Red);
    }
}
