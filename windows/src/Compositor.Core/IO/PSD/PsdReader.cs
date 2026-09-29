using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO.PSD;

/// <summary>Big-endian reads over the whole file, refusing to run off the end.</summary>
internal ref struct PsdCursor(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Offset { get; set; }

    public readonly int Length => _data.Length;

    public readonly int Remaining => _data.Length - Offset;

    /// <summary>Everything from here on, for a coder that walks it with its own offset.</summary>
    public readonly ReadOnlySpan<byte> Rest => _data[Offset..];

    public readonly void Need(int count)
    {
        if (count < 0 || Offset < 0 || Offset > _data.Length - count) throw PsdFailure.Truncated;
    }

    public void Skip(int count)
    {
        Need(count);
        Offset += count;
    }

    public byte U8()
    {
        Need(1);
        return _data[Offset++];
    }

    public ushort U16()
    {
        Need(2);
        var value = (ushort)(_data[Offset] << 8 | _data[Offset + 1]);
        Offset += 2;
        return value;
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        Need(4);
        var value = (uint)_data[Offset] << 24 | (uint)_data[Offset + 1] << 16 | (uint)_data[Offset + 2] << 8 | _data[Offset + 3];
        Offset += 4;
        return value;
    }

    public int I32() => (int)U32();

    public ulong U64()
    {
        Need(8);
        var high = (ulong)U32();
        return high << 32 | U32();
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Need(count);
        var bytes = _data.Slice(Offset, count);
        Offset += count;
        return bytes;
    }

    /// <summary>Four characters of a signature or key; a byte outside ASCII reads as nothing, as the Mac build reads it.</summary>
    public string Ascii(int count)
    {
        var bytes = Bytes(count);
        foreach (var value in bytes)
        {
            if (value > 0x7F) return "";
        }
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}

/// <summary>
/// Reads a Photoshop file: header, color mode data, image resources, layer and mask information, and the
/// merged image data when the file has no layer records.
/// </summary>
internal static class PsdReader
{
    /// <summary>Channels a layer holds that Compositor uses; spot and other channel IDs are skipped before decode.</summary>
    private static readonly int[] UnpackedChannelIds = [-1, 0, 1, 2, -2];

    /// <summary>Additional layer keys whose length is eight bytes in a large document.</summary>
    private static readonly string[] PsbLargeKeys =
        ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    private static readonly string[] TextKeys = ["TySh", "tySh", "txt2"];
    private static readonly string[] VectorKeys = ["vmsk", "vsms", "vogk"];
    private static readonly string[] SmartObjectKeys = ["SoLd", "SoLE"];
    private static readonly string[] EffectsKeys = ["lfx2", "lrFX", "lmfx"];

    internal static readonly string[] AdjustmentKeys =
        ["levl", "curv", "hue2", "hue ", "expA", "grdm", "brit", "blnc", "nvrt", "thrs", "post", "mixr", "selc", "blwh", "phfl", "vibA"];

    public static PsdDocument Read(ReadOnlySpan<byte> data, int remainingPixels, string flattenedName)
    {
        var cursor = new PsdCursor(data);
        if (cursor.Ascii(4) != "8BPS") throw PsdFailure.Truncated;
        var version = cursor.U16();
        if (version is not (1 or 2)) throw PsdFailure.Version;
        var isPsb = version == 2;
        cursor.Skip(6);
        var channelCount = cursor.U16();
        var canvasHeight = (int)cursor.U32();
        var canvasWidth = (int)cursor.U32();
        var depth = cursor.U16();
        var mode = cursor.U16();
        if (canvasWidth is < 1 or > DocumentLimits.MaxSide || canvasHeight is < 1 or > DocumentLimits.MaxSide
            || (long)canvasWidth * canvasHeight > DocumentLimits.MaxSurfacePixels)
        {
            throw PsdFailure.TooLarge;
        }
        if (depth != 8 || mode != 3) throw PsdFailure.ColorMode;

        cursor.Skip((int)cursor.U32());
        var resourcesLength = (int)cursor.U32();
        var resourcesEnd = cursor.Offset + resourcesLength;
        if (resourcesEnd > data.Length) throw PsdFailure.Truncated;
        var resolution = 72.0;
        while (cursor.Offset + 12 <= resourcesEnd)
        {
            if (cursor.Ascii(4) != "8BIM") break;
            var id = cursor.U16();
            var nameLength = cursor.U8();
            cursor.Skip(nameLength);
            if ((nameLength + 1) % 2 == 1) cursor.Skip(1);
            var length = (int)cursor.U32();
            var dataStart = cursor.Offset;
            if (id == 1005 && length >= 4)
            {
                resolution = cursor.U32() / 65536.0;
                if (!double.IsFinite(resolution) || resolution < 1) resolution = 72;
                resolution = Math.Min(9600, Math.Max(1, resolution));
            }
            cursor.Offset = dataStart + length;
            if (length % 2 == 1) cursor.Skip(1);
        }
        cursor.Offset = resourcesEnd;

        var document = new PsdDocument { Width = canvasWidth, Height = canvasHeight, Resolution = resolution };
        var layerSection = CheckedLength(isPsb ? cursor.U64() : cursor.U32());
        var layerSectionEnd = cursor.Offset + layerSection;
        if (layerSectionEnd > data.Length) throw PsdFailure.Truncated;
        if (layerSection < 4)
        {
            // A flattened file's layer section is empty, and the merged image follows it.
            cursor.Offset = layerSectionEnd;
            return Merged(ref cursor, document, channelCount, flattenedName, remainingPixels, isPsb);
        }

        var layerInfoLength = CheckedLength(isPsb ? cursor.U64() : cursor.U32());
        if (layerInfoLength == 0)
        {
            // An empty layer info holds no records either: writers that keep the sub-section's own length
            // apart from the count still mean "flattened".
            cursor.Offset = layerSectionEnd;
            return Merged(ref cursor, document, channelCount, flattenedName, remainingPixels, isPsb);
        }
        var count = Math.Abs((int)cursor.I16());
        if (count > 10_000) throw PsdFailure.TooLarge;
        var raw = new List<RawLayer>(count);
        for (var index = 0; index < count; index++) raw.Add(ReadRecord(ref cursor, data, isPsb));

        if (!FitsBudget(raw, remainingPixels))
        {
            foreach (var layer in raw) CropToCanvas(layer, canvasWidth, canvasHeight);
            if (!FitsBudget(raw, remainingPixels)) throw PsdFailure.TooLarge;
        }
        var usedPixels = 0;
        foreach (var layer in raw)
        {
            DecodeChannels(ref cursor, layer, remainingPixels - usedPixels, isPsb);
            if (layer.Image is { } image) usedPixels += image.Width * image.Height;
        }
        cursor.Offset = layerSectionEnd;
        document.Layers.AddRange(Assemble(raw, canvasWidth, canvasHeight));
        return document.Layers.Count == 0
            ? Merged(ref cursor, document, channelCount, flattenedName, remainingPixels, isPsb)
            : document;
    }

    private sealed class RawLayer
    {
        public string Name = "";
        public int Top, Left, Bottom, Right;
        public int SourceTop, SourceLeft, SourceBottom, SourceRight;
        public byte Opacity = 255;
        public byte Fill = 255;
        public bool Clipping;
        public bool Hidden;
        public string BlendKey = "norm";
        public List<(int Id, int Length)> Channels = [];
        public Dictionary<string, (int Offset, int Length)> Extra = [];
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public int SourceMaskTop, SourceMaskLeft, SourceMaskBottom, SourceMaskRight;
        public byte MaskDefault = 255;
        public bool MaskDisabled;
        public bool MaskLinked = true;
        public bool MaskFromRender;
        public bool HasMask;
        public int? Section;
        public SKBitmap? Image;
        public SKBitmap? MaskImage;
        public PsdCrop? ImageCrop;
        public PsdCrop? MaskCrop;
        public bool Cropped;

        /// <summary>Parsed adjustment settings, when the layer's blocks map onto one.</summary>
        public LayerAdjustment? Adjustment;
    }

    private static int CheckedLength(ulong value)
    {
        if (value > int.MaxValue) throw PsdFailure.TooLarge;
        return (int)value;
    }

    private static RawLayer ReadRecord(ref PsdCursor cursor, ReadOnlySpan<byte> data, bool isPsb)
    {
        var layer = new RawLayer
        {
            Top = cursor.I32(),
            Left = cursor.I32(),
            Bottom = cursor.I32(),
            Right = cursor.I32(),
        };
        layer.SourceTop = layer.Top;
        layer.SourceLeft = layer.Left;
        layer.SourceBottom = layer.Bottom;
        layer.SourceRight = layer.Right;

        var channelCount = cursor.U16();
        if (channelCount > 56) throw PsdFailure.TooLarge;
        for (var index = 0; index < channelCount; index++)
        {
            var id = cursor.I16();
            layer.Channels.Add((id, CheckedLength(isPsb ? cursor.U64() : cursor.U32())));
        }
        if (cursor.Ascii(4) != "8BIM") throw PsdFailure.Truncated;
        layer.BlendKey = cursor.Ascii(4);
        layer.Opacity = cursor.U8();
        layer.Clipping = cursor.U8() != 0;
        var flags = cursor.U8();
        layer.Hidden = (flags & 2) != 0;
        cursor.Skip(1);

        // Both lengths are measured from where the length field ends, so they are read before they are added.
        var extraLength = (int)cursor.U32();
        var extraEnd = cursor.Offset + extraLength;
        var maskLength = (int)cursor.U32();
        var maskEnd = cursor.Offset + maskLength;
        if (extraEnd > data.Length || maskEnd > data.Length) throw PsdFailure.Truncated;
        if (maskLength >= 20)
        {
            layer.HasMask = true;
            layer.MaskTop = cursor.I32();
            layer.MaskLeft = cursor.I32();
            layer.MaskBottom = cursor.I32();
            layer.MaskRight = cursor.I32();
            layer.SourceMaskTop = layer.MaskTop;
            layer.SourceMaskLeft = layer.MaskLeft;
            layer.SourceMaskBottom = layer.MaskBottom;
            layer.SourceMaskRight = layer.MaskRight;
            layer.MaskDefault = cursor.U8();
            var maskFlags = cursor.U8();
            layer.MaskDisabled = (maskFlags & 2) != 0;
            layer.MaskLinked = (maskFlags & 1) == 0;
            layer.MaskFromRender = (maskFlags & 8) != 0;
        }
        cursor.Offset = maskEnd;

        cursor.Skip((int)cursor.U32());
        var nameCount = cursor.U8();
        layer.Name = MacRoman(cursor.Bytes(nameCount));
        cursor.Skip((4 - ((nameCount + 1) % 4)) % 4);

        while (cursor.Offset + 12 <= extraEnd)
        {
            var signature = cursor.Ascii(4);
            if (signature is not ("8BIM" or "8B64")) break;
            var key = cursor.Ascii(4);
            int length;
            if (signature == "8B64" || (isPsb && PsbLargeKeys.Contains(key)))
            {
                if (cursor.Offset + 8 > extraEnd) break;
                length = CheckedLength(cursor.U64());
            }
            else
            {
                length = (int)cursor.U32();
            }
            var payload = cursor.Offset;
            cursor.Skip(length);
            if (length % 2 == 1) cursor.Skip(1);
            layer.Extra[key] = (payload, length);
            if (key == "luni" && UnicodeName(data.Slice(payload, length)) is { } unicode) layer.Name = unicode;
            if (key == "iOpa" && length >= 1) layer.Fill = data[payload];
            if ((key == "lsct" || key == "lsdk") && length >= 4) layer.Section = (int)ReadU32(data.Slice(payload));
        }
        cursor.Offset = extraEnd;
        if (layer.Section is not (1 or 2)) layer.Adjustment = PsdAdjustments.Parse(data, layer.Extra);
        return layer;
    }

    /// <summary>A layer name: Mac OS Roman, as Photoshop writes it. Unicode names arrive in `luni` instead.</summary>
    private static string MacRoman(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            chars[index] = value < 0x80 ? (char)value : MacRomanHigh[value - 0x80];
        }
        return new string(chars);
    }

    private static string? UnicodeName(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        var count = (int)ReadU32(data);
        if (count <= 0 || data.Length < 4 + count * 2) return null;
        var units = new char[count];
        for (var index = 0; index < count; index++)
        {
            units[index] = (char)(data[4 + index * 2] << 8 | data[5 + index * 2]);
        }
        return new string(units).Trim('\0');
    }

    private static uint ReadU32(ReadOnlySpan<byte> data) =>
        (uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3];

    private static int Width(RawLayer layer) => Math.Max(0, layer.Right - layer.Left);

    private static int Height(RawLayer layer) => Math.Max(0, layer.Bottom - layer.Top);

    private static bool FitsBudget(List<RawLayer> layers, int remainingPixels)
    {
        var usedPixels = 0;
        foreach (var layer in layers)
        {
            var width = Width(layer);
            var height = Height(layer);
            if (!FitsBudget(width, height, Math.Max(0, layer.MaskRight - layer.MaskLeft),
                    Math.Max(0, layer.MaskBottom - layer.MaskTop), layer.HasMask, remainingPixels - usedPixels))
            {
                return false;
            }
            if (width > 0 && height > 0) usedPixels += width * height;
        }
        return true;
    }

    private static bool FitsBudget(int width, int height, int maskWidth, int maskHeight, bool hasMask, int remainingPixels)
    {
        var budget = Math.Max(0, remainingPixels);
        if (width > 0 && height > 0
            && !(width <= DocumentLimits.MaxSide && height <= DocumentLimits.MaxSide && (long)width * height <= budget))
        {
            return false;
        }
        if (hasMask && maskWidth > 0 && maskHeight > 0
            && !(maskWidth <= DocumentLimits.MaxSide && maskHeight <= DocumentLimits.MaxSide && (long)maskWidth * maskHeight <= budget))
        {
            return false;
        }
        return true;
    }

    private static void CropToCanvas(RawLayer layer, int canvasWidth, int canvasHeight)
    {
        var bounds = new PsdRect(layer.Left, layer.Top, layer.Right, layer.Bottom);
        var cut = bounds.Crop(canvasWidth, canvasHeight);
        if (cut != bounds)
        {
            layer.ImageCrop = new PsdCrop(cut.Left - bounds.Left, cut.Top - bounds.Top, cut.Width, cut.Height);
            layer.Left = cut.Left;
            layer.Top = cut.Top;
            layer.Right = cut.Right;
            layer.Bottom = cut.Bottom;
            layer.Cropped = true;
        }
        if (!layer.HasMask) return;
        var maskBounds = new PsdRect(layer.MaskLeft, layer.MaskTop, layer.MaskRight, layer.MaskBottom);
        var maskCut = maskBounds.Crop(canvasWidth, canvasHeight);
        if (maskCut != maskBounds)
        {
            layer.MaskCrop = new PsdCrop(maskCut.Left - maskBounds.Left, maskCut.Top - maskBounds.Top, maskCut.Width, maskCut.Height);
            layer.MaskLeft = maskCut.Left;
            layer.MaskTop = maskCut.Top;
            layer.MaskRight = maskCut.Right;
            layer.MaskBottom = maskCut.Bottom;
            layer.Cropped = true;
        }
    }

    private static void DecodeChannels(ref PsdCursor cursor, RawLayer layer, int remainingPixels, bool isPsb)
    {
        var planes = new Dictionary<int, byte[]>();
        var width = Width(layer);
        var height = Height(layer);
        var maskWidth = Math.Max(0, layer.MaskRight - layer.MaskLeft);
        var maskHeight = Math.Max(0, layer.MaskBottom - layer.MaskTop);
        if (!FitsBudget(width, height, maskWidth, maskHeight, layer.HasMask, remainingPixels)) throw PsdFailure.TooLarge;
        var sourceWidth = Math.Max(0, layer.SourceRight - layer.SourceLeft);
        var sourceHeight = Math.Max(0, layer.SourceBottom - layer.SourceTop);
        var sourceMaskWidth = Math.Max(0, layer.SourceMaskRight - layer.SourceMaskLeft);
        var sourceMaskHeight = Math.Max(0, layer.SourceMaskBottom - layer.SourceMaskTop);

        foreach (var (id, length) in layer.Channels)
        {
            var start = cursor.Offset;
            try
            {
                if (!UnpackedChannelIds.Contains(id) || length < 2) continue;
                var compression = cursor.U16();
                var payload = cursor.Bytes(length - 2);
                var isMask = id == -2;
                var targetWidth = isMask ? maskWidth : width;
                var targetHeight = isMask ? maskHeight : height;
                if (targetWidth <= 0 || targetHeight <= 0) continue;
                planes[id] = PsdChannelCoder.Decode(compression,
                    isMask ? sourceMaskWidth : sourceWidth, isMask ? sourceMaskHeight : sourceHeight,
                    payload, isPsb, isMask ? layer.MaskCrop : layer.ImageCrop);
            }
            finally
            {
                cursor.Offset = start + Math.Max(0, length);
            }
        }

        if (layer.HasMask && maskWidth > 0 && maskHeight > 0
            && planes.TryGetValue(-2, out var gray) && gray.Length >= maskWidth * maskHeight)
        {
            layer.MaskImage = PsdChannelCoder.Gray(maskWidth, maskHeight, gray);
        }
        if (width <= 0 || height <= 0) return;
        planes.TryGetValue(0, out var red);
        planes.TryGetValue(1, out var green);
        planes.TryGetValue(2, out var blue);
        planes.TryGetValue(-1, out var alpha);
        var needed = width * height;
        if (red is { Length: var r } && r < needed || green is { Length: var g } && g < needed
            || blue is { Length: var b } && b < needed || alpha is { Length: var a } && a < needed)
        {
            throw PsdFailure.Truncated;
        }
        layer.Image = PsdChannelCoder.Rgba(width, height, red, green, blue, alpha);
    }

    private static List<PsdRecord> Assemble(List<RawLayer> raw, int canvasWidth, int canvasHeight)
    {
        var result = new List<PsdRecord>(raw.Count);
        var groups = new List<Guid>();
        foreach (var layer in raw)
        {
            // Photoshop stores groups bottom-to-top: the type 3 divider, then the children, then the folder
            // (type 1 or 2, which sits above them in the stack).
            if (layer.Section == 3)
            {
                groups.Add(Guid.NewGuid());
                continue;
            }
            var isGroup = layer.Section is 1 or 2;
            var id = Guid.NewGuid();
            if (isGroup && groups.Count > 0)
            {
                id = groups[^1];
                groups.RemoveAt(groups.Count - 1);
            }
            var record = new PsdRecord
            {
                Id = id,
                ParentId = groups.Count > 0 ? groups[^1] : null,
                Name = layer.Name.Length == 0 ? "Layer" : layer.Name,
                IsGroup = isGroup,
                IsVisible = !layer.Hidden,
                Clipping = layer.Clipping,
                CroppedToCanvas = layer.Cropped,
                Kind = Kind(layer, isGroup),
                BlendKey = isGroup && (layer.BlendKey == "pass" || layer.BlendKey == "norm") ? "pass" : layer.BlendKey,
                Bounds = isGroup
                    ? new PsdRect(0, 0, canvasWidth, canvasHeight)
                    : new PsdRect(layer.Left, layer.Top, layer.Right, layer.Bottom),
                Image = isGroup ? null : layer.Image,
                Mask = layer.MaskFromRender ? null : layer.MaskImage,
                MaskBounds = new PsdRect(layer.MaskLeft, layer.MaskTop, layer.MaskRight, layer.MaskBottom),
                MaskDefault = layer.MaskDefault,
                MaskEnabled = !layer.MaskDisabled,
                MaskLinked = layer.MaskLinked,
                Adjustment = layer.Adjustment,
            };
            var hasEffects = record.Kind == PsdLayerKind.Effects;
            // A fill that is not fully opaque is only kept apart from the opacity for effect layers, which
            // Photoshop stores without their effect.
            record.Opacity = hasEffects && layer.Fill != 255
                ? layer.Opacity / 255.0
                : layer.Opacity / 255.0 * (layer.Fill / 255.0);
            result.Add(record);
        }
        // A group divider with no folder to close is damage.
        if (groups.Count > 0) throw PsdFailure.Truncated;
        return result;
    }

    private static PsdLayerKind Kind(RawLayer layer, bool isGroup)
    {
        if (isGroup) return PsdLayerKind.Group;
        if (HasAny(layer, TextKeys)) return PsdLayerKind.Text;
        if (HasAny(layer, VectorKeys)) return PsdLayerKind.Vector;
        if (HasAny(layer, SmartObjectKeys)) return PsdLayerKind.SmartObject;
        if (HasAny(layer, EffectsKeys)) return PsdLayerKind.Effects;
        if (layer.Extra.Keys.Any(AdjustmentKeys.Contains)) return PsdLayerKind.Adjustment;
        return PsdLayerKind.Raster;
    }

    private static bool HasAny(RawLayer layer, string[] keys)
    {
        foreach (var key in keys)
        {
            if (layer.Extra.ContainsKey(key)) return true;
        }
        return false;
    }

    /// <summary>
    /// The image data at the end of the file, which holds the merged image rather than any layer. It comes in
    /// as one layer when the file has no layer records: Photoshop writes no records for a flattened file.
    /// </summary>
    private static PsdDocument Merged(ref PsdCursor cursor, PsdDocument document, int channelCount, string flattenedName,
        int remainingPixels, bool isPsb)
    {
        var width = document.Width;
        var height = document.Height;
        if ((long)width * height > Math.Max(0, remainingPixels)) throw PsdFailure.TooLarge;
        var planes = Math.Clamp(channelCount, 3, 4);
        var compression = cursor.U16();
        byte[]? red;
        byte[]? green;
        byte[]? blue;
        byte[]? alpha = null;
        if (compression == 0)
        {
            red = cursor.Bytes(width * height).ToArray();
            green = cursor.Bytes(width * height).ToArray();
            blue = cursor.Bytes(width * height).ToArray();
            if (planes == 4) alpha = cursor.Bytes(width * height).ToArray();
        }
        else if (compression == 1)
        {
            var data = cursor.Rest;
            var offset = 0;
            var counts = PsdChannelCoder.RowCounts(data, ref offset, height * planes, isPsb);
            red = PsdChannelCoder.PackRows(width, height, data, counts, ref offset, null);
            green = PsdChannelCoder.PackRows(width, height, data, counts, ref offset, null, height);
            blue = PsdChannelCoder.PackRows(width, height, data, counts, ref offset, null, height * 2);
            if (planes == 4) alpha = PsdChannelCoder.PackRows(width, height, data, counts, ref offset, null, height * 3);
            cursor.Skip(offset);
        }
        else
        {
            throw PsdFailure.Compression;
        }

        document.Layers.Add(new PsdRecord
        {
            Name = flattenedName.Length == 0 ? "Background" : flattenedName,
            Bounds = new PsdRect(0, 0, width, height),
            Image = PsdChannelCoder.Rgba(width, height, red, green, blue, alpha),
        });
        return document;
    }

    /// <summary>Mac OS Roman above 0x7F, which is how Photoshop writes a layer name before `luni`.</summary>
    private const string MacRomanHigh =
        "\u00C4\u00C5\u00C7\u00C9\u00D1\u00D6\u00DC\u00E1\u00E0\u00E2\u00E4\u00E3\u00E5\u00E7\u00E9\u00E8" +
        "\u00EA\u00EB\u00ED\u00EC\u00EE\u00EF\u00F1\u00F3\u00F2\u00F4\u00F6\u00F5\u00FA\u00F9\u00FB\u00FC" +
        "\u2020\u00B0\u00A2\u00A3\u00A7\u2022\u00B6\u00DF\u00AE\u00A9\u2122\u00B4\u00A8\u2260\u00C6\u00D8" +
        "\u221E\u00B1\u2264\u2265\u00A5\u00B5\u2202\u2211\u220F\u03C0\u222B\u00AA\u00BA\u03A9\u00E6\u00F8" +
        "\u00BF\u00A1\u00AC\u221A\u0192\u2248\u2206\u00AB\u00BB\u2026\u00A0\u00C0\u00C3\u00D5\u0152\u0153" +
        "\u2013\u2014\u201C\u201D\u2018\u2019\u00F7\u25CA\u00FF\u0178\u2044\u20AC\u2039\u203A\uFB01\uFB02" +
        "\u2021\u00B7\u201A\u201E\u2030\u00C2\u00CA\u00C1\u00CB\u00C8\u00CD\u00CE\u00CF\u00CC\u00D3\u00D4" +
        "\uF8FF\u00D2\u00DA\u00DB\u00D9\u0131\u02C6\u02DC\u00AF\u02D8\u02D9\u02DA\u00B8\u02DD\u02DB\u02C7";
}
