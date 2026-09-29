using System.Text;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.IO.PSD;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Photoshop files, written by hand here so the reader is checked against the format rather than against
/// itself: the header, one image resource, the layer and mask section with folders, masks and PackBits
/// rows, the merged image data, and the PNG save and render an import feeds.
/// </summary>
public class PsdImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorPsd-" + Guid.NewGuid().ToString("N"));

    public PsdImportTests() => Directory.CreateDirectory(_root);

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

    private const int CanvasWidth = 8;
    private const int CanvasHeight = 6;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LayersFoldersMasksAndClippingComeIn(int version)
    {
        var import = PsdImporter.Read(Write($"Layered{version}.psd", LayeredFile(version)));

        var manifest = import.Manifest;
        Assert.Equal(11, manifest.Version);
        Assert.Equal(CanvasWidth, manifest.Width);
        Assert.Equal(CanvasHeight, manifest.Height);
        Assert.Equal(300.0, manifest.Resolution ?? 0, 6);

        // Bottom to top, each folder's children before the folder itself.
        var names = manifest.Layers.Select(layer => layer.Name).ToList();
        Assert.Equal(new[] { "Background", "Clipped", "Unicode \u2713 Name", "Folder 1", "Hidden" }, names);

        var background = manifest.Layers[0];
        Assert.False(background.IsGroupValue);
        Assert.Null(background.ParentID);
        Assert.True(background.IsVisible);
        Assert.Equal(1.0, background.OpacityValue);
        Assert.Equal(LayerBlendMode.Normal, background.BlendModeValue);
        Assert.Equal(0, (int)background.Transform.Origin.X);
        Assert.Equal(CanvasWidth, (int)background.Transform.Size.Width);
        Assert.Equal(CanvasHeight, (int)background.Transform.Size.Height);
        Assert.Equal(LayerMask.ExpectedImageFile(background.ID), background.ImageFile);
        Assert.Equal(LayerMask.ExpectedFile(background.ID), background.MaskFile);
        Assert.True(background.MaskEnabled);
        Assert.True(background.MaskLinked);
        Assert.Null(background.MaskPlacement);
        Assert.Equal(new[] { CanvasWidth, CanvasHeight }, Pixels(import.Images[background.ID]));
        // The stored patch is the left half; the mask the document holds is on the layer's own grid.
        Assert.Equal(new[] { CanvasWidth, CanvasHeight }, Pixels(import.Masks[background.ID]));

        // Photoshop's clipping flag becomes the id of the layer below.
        var clipped = manifest.Layers[1];
        Assert.Equal(background.ID, clipped.MaskSourceID);
        Assert.Equal(LayerBlendMode.Normal, clipped.BlendModeValue);
        Assert.Equal(LayerMask.ExpectedImageFile(clipped.ID), clipped.ImageFile);
        Assert.Equal(0, (int)clipped.Transform.Origin.X);
        Assert.Equal(2, (int)clipped.Transform.Size.Width);
        Assert.Equal(2, (int)clipped.Transform.Size.Height);

        // A Unicode name wins over the Pascal one, and both multiply and the folder's opacity survive.
        var child = manifest.Layers[2];
        Assert.Equal("Unicode \u2713 Name", child.Name);
        Assert.Equal(LayerBlendMode.Multiply, child.BlendModeValue);
        Assert.Equal(128 / 255.0, child.OpacityValue, 6);
        Assert.Equal(2, (int)child.Transform.Origin.X);
        Assert.Equal(1, (int)child.Transform.Origin.Y);
        Assert.Equal(2, (int)child.Transform.Size.Width);
        Assert.Equal(2, (int)child.Transform.Size.Height);
        Assert.Null(child.MaskFile);

        var folder = manifest.Layers[3];
        Assert.True(folder.IsGroupValue);
        Assert.Equal(child.ParentID, folder.ID);
        Assert.Null(folder.ParentID);
        Assert.Null(folder.ImageFile);
        Assert.Equal(128 / 255.0, folder.OpacityValue, 6);
        // A folder is pass-through, so its unsupported blend mode becomes Normal and is reported.
        Assert.Equal(LayerBlendMode.Normal, folder.BlendModeValue);

        // Linear Dodge is Compositor's "Linear Dodge (Add)"; the fill opacity multiplies into the opacity.
        var hidden = manifest.Layers[4];
        Assert.Equal(LayerBlendMode.LinearDodgeAdd, hidden.BlendModeValue);
        Assert.False(hidden.IsVisible);
        Assert.Equal(128 / 255.0, hidden.OpacityValue, 6);

        Assert.Equal(2, import.Notes.Count);
        Assert.Equal(new[] { "Clipped", "Folder 1" }, import.Notes.Select(note => note.Layer).ToList());
        Assert.Contains("Blend mode \"diss\"", import.Notes[0].What, StringComparison.Ordinal);
        Assert.Contains("Folder blend mode \"mul \"", import.Notes[1].What, StringComparison.Ordinal);
        ProjectStore.Validate(manifest);
    }

    [Fact]
    public void TheUnpackedPixelsAreTheFilesOwn()
    {
        var import = PsdImporter.Read(Write("Pixels.psd", LayeredFile(1)));
        var background = import.Manifest.Layers[0].ID;
        var image = import.Images[background].Image;
        // Row 5 of every plane is a literal pair then a repeat, the other rows single repeats: PackBits both ways.
        Assert.Equal(new SKColor(200, 100, 50, 255), image.GetPixel(5, 5));
        Assert.Equal(new SKColor(10, 20, 30, 255), image.GetPixel(0, 0));
        Assert.Equal(new SKColor(10, 20, 30, 255), image.GetPixel(7, 5));

        // The clipped layer's own pixels came from RLE rows too.
        var clipped = import.Images[import.Manifest.Layers[1].ID].Image;
        Assert.Equal(new SKColor(0, 0, 255, 255), clipped.GetPixel(0, 0));

        // The mask is the patch where it sits and Photoshop's default value everywhere else.
        var mask = import.Masks[background].Image;
        Assert.Equal(SKColorType.Gray8, mask.ColorType);
        Assert.Equal((byte)0, mask.GetPixel(0, 0).Red);
        Assert.Equal((byte)255, mask.GetPixel(1, 1).Red);
        Assert.Equal((byte)255, mask.GetPixel(0, 5).Red);
        Assert.Equal((byte)255, mask.GetPixel(4, 0).Red);
        Assert.Equal((byte)255, mask.GetPixel(7, 5).Red);

        // The child's alpha channel, straight and top row first: 0 and 255, then 255 and 128.
        var child = import.Images[import.Manifest.Layers[2].ID].Image;
        Assert.Equal((byte)0, child.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, child.GetPixel(1, 0).Alpha);
        Assert.Equal((byte)255, child.GetPixel(0, 1).Alpha);
        Assert.Equal((byte)128, child.GetPixel(1, 1).Alpha);
        Assert.Equal((byte)255, child.GetPixel(1, 0).Green);

        // The hidden layer's channels were written raw rather than compressed.
        var hidden = import.Images[import.Manifest.Layers[4].ID].Image;
        Assert.Equal(new SKColor(255, 0, 255, 255), hidden.GetPixel(3, 2));
    }

    [Fact]
    public void AFileWithNoLayerRecordsComesInAsItsMergedImage()
    {
        var import = PsdImporter.Read(Write("Flat.psd", FlattenedFile(1)));

        var layer = Assert.Single(import.Manifest.Layers);
        Assert.Equal("Flat", layer.Name);
        Assert.Equal(4, import.Manifest.Width);
        Assert.Equal(3, import.Manifest.Height);
        Assert.Equal(0, (int)layer.Transform.Origin.X);
        Assert.Equal(4, (int)layer.Transform.Size.Width);
        Assert.Equal(3, (int)layer.Transform.Size.Height);
        Assert.Equal(LayerMask.ExpectedImageFile(layer.ID), layer.ImageFile);
        Assert.Empty(import.Notes);

        // Three planes, no alpha channel: the merged image comes in opaque.
        var image = import.Images[layer.ID].Image;
        Assert.Equal(new SKColor(10, 20, 30, 255), image.GetPixel(0, 0));
        Assert.Equal(new SKColor(200, 20, 30, 255), image.GetPixel(3, 2));
        ProjectStore.Validate(import.Manifest);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void AFlattenedFileWithAnEmptyLayerSectionComesInTheSameWay(int section)
    {
        var import = PsdImporter.Read(Write($"Empty{section}.psd", FlattenedFile(1, section)));

        var layer = Assert.Single(import.Manifest.Layers);
        Assert.Equal($"Empty{section}", layer.Name);
        Assert.Equal(new SKColor(200, 20, 30, 255), import.Images[layer.ID].Image.GetPixel(3, 2));
    }

    [Theory]
    [InlineData(22, 16, "8-bit")]     // 16 bits per channel
    [InlineData(25, 4, "8-bit")]      // CMYK
    [InlineData(4, 3, "format version")]
    public void AFileThatIsNotEightBitRgbIsRefused(int offset, byte value, string expected)
    {
        var bytes = FlattenedFile(1);
        bytes[offset] = 0;
        bytes[offset + 1] = value;
        var error = Assert.Throws<ImportException>(() => PsdImporter.Read(Write($"Refused{offset}.psd", bytes)));
        Assert.Equal(ImportError.Unsupported, error.Error);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportSavesAndRendersThroughTheProjectFormat()
    {
        var import = PsdImporter.Read(Write("Round.psd", LayeredFile(1)));
        var package = Path.Combine(_root, "Round.comp");
        ProjectStore.Save(import.Snapshot(), package);

        using var loaded = ProjectStore.Load(package);
        using var document = loaded.ToDocument();
        using var rendered = DocumentRenderer.Render(document);

        Assert.Equal(CanvasWidth, rendered.Width);
        Assert.Equal(CanvasHeight, rendered.Height);
        // The clip takes its coverage from the layer below, whose mask hides this pixel, so none of the
        // clipped layer's blue shows through there.
        Assert.Equal((byte)0, rendered.GetPixel(0, 0).Alpha);
        // Inside the clip's rectangle the blue is drawn over the base.
        Assert.Equal(new SKColor(0, 0, 255, 255), rendered.GetPixel(1, 1));
        // Outside the stored mask patch Photoshop's default value stands, so the layer shows there.
        Assert.Equal(new SKColor(10, 20, 30, 255), rendered.GetPixel(4, 0));
        Assert.Equal(new SKColor(200, 100, 50, 255), rendered.GetPixel(5, 5));
        Assert.Equal(new SKColor(10, 20, 30, 255), rendered.GetPixel(7, 5));
        // The child's transparent corner leaves the background alone.
        Assert.Equal(new SKColor(10, 20, 30, 255), rendered.GetPixel(2, 1));
        // The folder's opacity multiplies into the child, so its green arrives dimmed over the background.
        var mixed = rendered.GetPixel(3, 1);
        Assert.Equal((byte)255, mixed.Alpha);
        Assert.True(mixed.Green > mixed.Red, $"the child did not draw at (3,1): {mixed}");
        Assert.True(mixed.Blue < 30, $"multiply did not darken (3,1): {mixed}");
    }

    [Fact]
    public void AnAdjustmentLayerKeepsItsSettings()
    {
        var import = PsdImporter.Read(Write("Levels.psd", LevelsFile()));

        var adjustment = import.Manifest.Layers[1].Adjustment;
        Assert.NotNull(adjustment);
        Assert.Equal(AdjustmentKind.Levels, adjustment.Kind);
        Assert.Null(import.Manifest.Layers[1].ImageFile);
        Assert.Equal(CanvasWidth, (int)import.Manifest.Layers[1].Transform.Size.Width);
        var rgb = adjustment.Levels.Ranges[0];
        Assert.Equal(10.0, rgb.Black, 6);
        Assert.Equal(245.0, rgb.White, 6);
        Assert.Equal(1.2, rgb.Gamma, 6);
        Assert.Equal(5.0, rgb.OutputBlack, 6);
        Assert.Equal(250.0, rgb.OutputWhite, 6);
        Assert.Equal(rgb, adjustment.Levels.Ranges[3]);
        Assert.Contains(import.Notes, note => note.Layer == "Levels" && note.What.Contains("may not match", StringComparison.Ordinal));
        ProjectStore.Validate(import.Manifest);
    }

    [Fact]
    public void AnImportPastTheBudgetIsRefused()
    {
        var path = Write("Heavy.psd", LayeredFile(1));
        var error = Assert.Throws<ImportException>(() => PsdImporter.Read(path, remainingPixels: 12));
        Assert.Equal(ImportError.TooLarge, error.Error);
    }

    private static int[] Pixels(Model.ImportedImage image) => new[] { image.Width, image.Height };

    // ---------------------------------------------------------------------------------------------
    // A Photoshop file, written a field at a time.
    // ---------------------------------------------------------------------------------------------

    private sealed class LayerSpec
    {
        public string Name = "";
        public string? Unicode;
        public int Top, Left, Bottom, Right;
        public string Blend = "norm";
        public byte Opacity = 255;
        public byte Fill = 255;
        public bool Clipping;
        public bool Hidden;
        public int? Section;
        public int[] ChannelIds = [];
        public List<byte[]> Planes = [];
        public bool Raw;
        public byte[]? Levels;
        public byte[]? MaskPlane;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public byte MaskDefault = 255;
    }

    private static byte[] Plane(int width, int height, byte value, params (int At, int Value)[] exceptions)
    {
        var plane = new byte[width * height];
        Array.Fill(plane, value);
        foreach (var (at, replacement) in exceptions) plane[at] = (byte)replacement;
        return plane;
    }

    /// <summary>A plane of one value, for the merged image data the reader walks row by row from its counts.</summary>
    private static byte[] Flat(int length, byte value) => Plane(length, 1, value);

    private static List<LayerSpec> LayeredLayers() =>
    [
        new LayerSpec
        {
            Name = "Background",
            Left = 0,
            Top = 0,
            Right = CanvasWidth,
            Bottom = CanvasHeight,
            ChannelIds = [0, 1, 2],
            Planes =
            [
                Plane(CanvasWidth, CanvasHeight, 10, (45, 200)),
                Plane(CanvasWidth, CanvasHeight, 20, (45, 100)),
                Plane(CanvasWidth, CanvasHeight, 30, (45, 50)),
            ],
            // The mask covers the left half and hides one pixel of it; the rest of the layer is untouched.
            MaskPlane = Plane(4, CanvasHeight, 255, (0, 0)),
            MaskLeft = 0,
            MaskTop = 0,
            MaskRight = 4,
            MaskBottom = CanvasHeight,
        },
        new LayerSpec
        {
            Name = "Clipped",
            Left = 0,
            Top = 0,
            Right = 2,
            Bottom = 2,
            Blend = "diss",
            Clipping = true,
            ChannelIds = [0, 1, 2],
            Planes = [Plane(2, 2, 0), Plane(2, 2, 0), Plane(2, 2, 255)],
        },
        new LayerSpec { Name = "</Layer group>", Section = 3 },
        new LayerSpec
        {
            Name = "Pascal Name",
            Unicode = "Unicode \u2713 Name",
            Left = 2,
            Top = 1,
            Right = 4,
            Bottom = 3,
            Blend = "mul ",
            Opacity = 128,
            ChannelIds = [0, 1, 2, -1],
            Planes = [Plane(2, 2, 0), Plane(2, 2, 255), Plane(2, 2, 0), [0, 255, 255, 128]],
        },
        new LayerSpec
        {
            Name = "Folder 1",
            Section = 1,
            Blend = "mul ",
            Opacity = 128,
        },
        new LayerSpec
        {
            Name = "Hidden",
            Right = CanvasWidth,
            Bottom = CanvasHeight,
            Blend = "lddg",
            Hidden = true,
            Fill = 128,
            Raw = true,
            ChannelIds = [0, 1, 2],
            Planes = [Plane(CanvasWidth, CanvasHeight, 255), Plane(CanvasWidth, CanvasHeight, 0), Plane(CanvasWidth, CanvasHeight, 255)],
        },
    ];

    private static byte[] LayeredFile(int version)
    {
        var psb = version == 2;
        using var file = new MemoryStream();
        var writer = new BeWriter(file);
        Header(writer, version, channels: 4);
        writer.U32(0);
        // One image resource: the resolution, in fixed-point pixels per inch.
        writer.U32(16);
        writer.Ascii("8BIM");
        writer.U16(1005);
        writer.Byte(0);
        writer.Byte(0);
        writer.U32(4);
        writer.U32(300 * 65536);

        var section = LayerSection(LayeredLayers(), psb);
        writer.Length(psb, section.Length);
        writer.Bytes(section);

        writer.U16(0);
        writer.Bytes(Flat(CanvasWidth * CanvasHeight, 10));
        writer.Bytes(Flat(CanvasWidth * CanvasHeight, 20));
        writer.Bytes(Flat(CanvasWidth * CanvasHeight, 30));
        return file.ToArray();
    }

    private static byte[] LevelsFile()
    {
        var layers = LayeredLayers();
        // Photoshop leaves an adjustment layer's channels empty; pixels are written here so that the
        // import has to show it keeps the adjustment rather than an image.
        layers.Insert(1, new LayerSpec
        {
            Name = "Levels",
            Right = CanvasWidth,
            Bottom = CanvasHeight,
            ChannelIds = [0, 1, 2],
            Planes = [Plane(CanvasWidth, CanvasHeight, 0), Plane(CanvasWidth, CanvasHeight, 0), Plane(CanvasWidth, CanvasHeight, 0)],
            Levels = LevelsBlock(),
        });
        using var file = new MemoryStream();
        var writer = new BeWriter(file);
        Header(writer, 1, channels: 3);
        writer.U32(0);
        writer.U32(0);
        var section = LayerSection(layers, psb: false);
        writer.Length(false, section.Length);
        writer.Bytes(section);
        writer.U16(0);
        writer.Bytes(Flat(CanvasWidth * CanvasHeight, 10));
        writer.Bytes(Flat(CanvasWidth * CanvasHeight, 20));
        writer.Bytes(Flat(CanvasWidth * CanvasHeight, 30));
        return file.ToArray();
    }

    private static byte[] LevelsBlock()
    {
        var payload = new byte[292];
        payload[1] = 2;
        for (var channel = 0; channel < 4; channel++)
        {
            var at = 2 + channel * 10;
            payload[at + 1] = 10;
            payload[at + 3] = 245;
            payload[at + 5] = 5;
            payload[at + 7] = 250;
            payload[at + 9] = 120;
        }
        return payload;
    }

    private static byte[] FlattenedFile(int version, int section = 0)
    {
        var psb = version == 2;
        const int width = 4;
        const int height = 3;
        using var file = new MemoryStream();
        var writer = new BeWriter(file);
        Header(writer, version, channels: 3, width: width, height: height);
        writer.U32(0);
        writer.U32(0);
        // No layer records at all: the merged image data is all the file has. Some writers still spend a
        // few bytes on the empty sub-sections, with a zero layer-info length saying there are none.
        if (section == 0)
        {
            writer.U32(0);
        }
        else
        {
            writer.U32(section);
            writer.U32(0);
            writer.Bytes(new byte[Math.Max(0, section - 4)]);
        }
        writer.U16(1);
        writer.Bytes(PackedPlanes(
            [Plane(width, height, 10, (11, 200)), Plane(width, height, 20), Plane(width, height, 30)], width, height, psb));
        return file.ToArray();
    }

    private static void Header(BeWriter writer, int version, int channels, int width = CanvasWidth, int height = CanvasHeight)
    {
        writer.Ascii("8BPS");
        writer.U16(version);
        writer.Bytes(new byte[6]);
        writer.U16(channels);
        writer.U32(height);
        writer.U32(width);
        writer.U16(8);
        writer.U16(3);
    }

    /// <summary>The layer and mask information section: the layer records and their channels, then the global mask.</summary>
    private static byte[] LayerSection(List<LayerSpec> layers, bool psb)
    {
        var blocks = layers.Select(layer => ChannelBlocks(layer, psb)).ToList();
        using var info = new MemoryStream();
        var writer = new BeWriter(info);
        writer.I16(layers.Count);
        for (var index = 0; index < layers.Count; index++) WriteRecord(writer, layers[index], blocks[index], psb);
        foreach (var layer in blocks)
        {
            foreach (var block in layer) writer.Bytes(block);
        }

        using var section = new MemoryStream();
        var outer = new BeWriter(section);
        var infoBytes = info.ToArray();
        outer.Length(psb, infoBytes.Length);
        outer.Bytes(infoBytes);
        outer.U32(0);
        return section.ToArray();
    }

    private static void WriteRecord(BeWriter writer, LayerSpec layer, byte[][] blocks, bool psb)
    {
        writer.I32(layer.Top);
        writer.I32(layer.Left);
        writer.I32(layer.Bottom);
        writer.I32(layer.Right);
        writer.U16(blocks.Length);
        var ids = ChannelIds(layer);
        for (var index = 0; index < blocks.Length; index++)
        {
            writer.I16(ids[index]);
            writer.Length(psb, blocks[index].Length);
        }
        writer.Ascii("8BIM");
        writer.Ascii(layer.Blend);
        writer.Byte(layer.Opacity);
        writer.Byte(layer.Clipping ? (byte)1 : (byte)0);
        writer.Byte(layer.Hidden ? (byte)2 : (byte)0);
        writer.Byte(0);
        var extra = Extra(layer);
        writer.U32(extra.Length);
        writer.Bytes(extra);
    }

    /// <summary>The mask block, blending ranges, Pascal name and additional layer information of one record.</summary>
    private static byte[] Extra(LayerSpec layer)
    {
        using var stream = new MemoryStream();
        var writer = new BeWriter(stream);
        if (layer.MaskPlane is not null)
        {
            // Twenty bytes: the rectangle, the default value, the flags, and Photoshop's two of padding.
            writer.U32(20);
            writer.I32(layer.MaskTop);
            writer.I32(layer.MaskLeft);
            writer.I32(layer.MaskBottom);
            writer.I32(layer.MaskRight);
            writer.Byte(layer.MaskDefault);
            writer.Byte(0);
            writer.U16(0);
        }
        else
        {
            writer.U32(0);
        }
        writer.U32(0);
        var name = Encoding.ASCII.GetBytes(layer.Name);
        writer.Byte((byte)name.Length);
        writer.Bytes(name);
        writer.Bytes(new byte[(4 - ((name.Length + 1) % 4)) % 4]);
        if (layer.Section is { } section)
        {
            writer.Ascii("8BIM");
            writer.Ascii("lsct");
            writer.U32(4);
            writer.U32(section);
        }
        if (layer.Unicode is { } unicode)
        {
            var units = Encoding.BigEndianUnicode.GetBytes(unicode);
            writer.Ascii("8BIM");
            writer.Ascii("luni");
            writer.U32(4 + units.Length);
            writer.U32(units.Length / 2);
            writer.Bytes(units);
        }
        if (layer.Fill != 255)
        {
            writer.Ascii("8BIM");
            writer.Ascii("iOpa");
            writer.U32(1);
            writer.Byte(layer.Fill);
            writer.Byte(0);
        }
        if (layer.Levels is { } levels)
        {
            writer.Ascii("8BIM");
            writer.Ascii("levl");
            writer.U32(levels.Length);
            writer.Bytes(levels);
        }
        return stream.ToArray();
    }

    /// <summary>The channels a record names: its own, its mask last, or four empty ones when it has no pixels.</summary>
    private static int[] ChannelIds(LayerSpec layer)
    {
        var ids = layer.ChannelIds.ToList();
        if (layer.MaskPlane is not null) ids.Add(-2);
        if (ids.Count == 0) ids.AddRange([-1, 0, 1, 2]);
        return [.. ids];
    }

    /// <summary>Each channel's compression and payload, in the order the record names them.</summary>
    private static byte[][] ChannelBlocks(LayerSpec layer, bool psb)
    {
        var width = layer.Right - layer.Left;
        var height = layer.Bottom - layer.Top;
        var blocks = new List<byte[]>();
        foreach (var plane in layer.Planes) blocks.Add(Block(plane, width, height, layer.Raw, psb));
        if (layer.MaskPlane is { } mask)
        {
            blocks.Add(Block(mask, layer.MaskRight - layer.MaskLeft, layer.MaskBottom - layer.MaskTop, raw: false, psb));
        }
        // A record with no pixels still carries its channel list, each channel no more than its compression.
        if (blocks.Count == 0 && layer.MaskPlane is null)
        {
            for (var index = 0; index < 4; index++)
            {
                using var empty = new MemoryStream();
                new BeWriter(empty).U16(0);
                blocks.Add(empty.ToArray());
            }
        }
        return [.. blocks];
    }

    private static byte[] Block(byte[] plane, int width, int height, bool raw, bool psb)
    {
        using var stream = new MemoryStream();
        var writer = new BeWriter(stream);
        writer.U16(raw ? 0 : 1);
        writer.Bytes(raw ? plane : PackedPlanes([plane], width, height, psb));
        return stream.ToArray();
    }

    /// <summary>The byte counts of every row of every plane, then the rows themselves.</summary>
    private static byte[] PackedPlanes(List<byte[]> planes, int width, int height, bool psb)
    {
        using var stream = new MemoryStream();
        var writer = new BeWriter(stream);
        var rows = planes
            .SelectMany(plane => Enumerable.Range(0, height).Select(row => PackBits(plane.AsSpan(row * width, width))))
            .ToList();
        foreach (var row in rows) writer.RowLength(psb, row.Length);
        foreach (var row in rows) writer.Bytes(row);
        return stream.ToArray();
    }

    /// <summary>A minimal PackBits row: runs of three or more repeat, everything else is written out.</summary>
    private static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        var packed = new List<byte>();
        var index = 0;
        while (index < row.Length)
        {
            var run = 1;
            while (index + run < row.Length && row[index + run] == row[index] && run < 128) run++;
            if (run >= 3)
            {
                packed.Add((byte)(1 - run));
                packed.Add(row[index]);
                index += run;
                continue;
            }
            var start = index;
            while (index < row.Length && index - start < 128 && !StartsRun(row, index)) index++;
            packed.Add((byte)(index - start - 1));
            for (var at = start; at < index; at++) packed.Add(row[at]);
        }
        return [.. packed];
    }

    private static bool StartsRun(ReadOnlySpan<byte> row, int at) =>
        at + 2 < row.Length && row[at] == row[at + 1] && row[at] == row[at + 2];

    private sealed class BeWriter(Stream stream)
    {
        private readonly Stream _stream = stream;

        public void Byte(byte value) => _stream.WriteByte(value);

        public void Bytes(byte[] value) => _stream.Write(value, 0, value.Length);

        public void U16(int value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void I16(int value) => U16(value & 0xFFFF);

        public void U32(int value)
        {
            for (var shift = 24; shift >= 0; shift -= 8) _stream.WriteByte((byte)(value >> shift));
        }

        public void I32(int value) => U32(value);

        public void U64(long value)
        {
            for (var shift = 56; shift >= 0; shift -= 8) _stream.WriteByte((byte)(value >> shift));
        }

        /// <summary>A section or channel length: four bytes, or eight in a large document.</summary>
        public void Length(bool psb, long value)
        {
            if (psb) U64(value);
            else U32((int)value);
        }

        /// <summary>A PackBits row's byte count: two bytes, or four in a large document.</summary>
        public void RowLength(bool psb, long value)
        {
            if (psb) U32((int)value);
            else U16((int)value);
        }

        public void Ascii(string value) => Bytes(Encoding.ASCII.GetBytes(value));
    }
}
