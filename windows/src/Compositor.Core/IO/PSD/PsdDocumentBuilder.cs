using System.Text;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.IO.PSD;

/// <summary>
/// Turns a parsed Photoshop file into the document layers, pixels and conversion notes an import holds, the
/// way the Mac build's `PSDDocumentBuilder` does.
/// </summary>
internal static class PsdDocumentBuilder
{
    public static PsdImport Build(PsdDocument document, int remainingPixels)
    {
        var notes = new List<PsdConversionNote>();
        var records = new List<ProjectLayerRecord>(document.Layers.Count);
        var images = new Dictionary<Guid, ImportedImage>();
        var masks = new Dictionary<Guid, ImportedImage>();
        var usedMaskPixels = 0;
        try
        {
            foreach (var layer in document.Layers)
            {
                AddLayer(document, layer, records, images, masks, notes, remainingPixels, ref usedMaskPixels);
            }
            ResolveClipping(document, records, notes);
        }
        catch
        {
            foreach (var image in images.Values) image.Dispose();
            foreach (var mask in masks.Values) mask.Dispose();
            throw;
        }

        var manifest = new ProjectManifest
        {
            DocumentID = Guid.NewGuid(),
            Width = document.Width,
            Height = document.Height,
            Resolution = document.Resolution,
            ActiveLayerID = records.Count > 0 ? records[^1].ID : null,
            Layers = records,
        };
        return new PsdImport { Manifest = manifest, Images = images, Masks = masks, Notes = notes };
    }

    private static void AddLayer(PsdDocument document, PsdRecord layer, List<ProjectLayerRecord> records,
        Dictionary<Guid, ImportedImage> images, Dictionary<Guid, ImportedImage> masks, List<PsdConversionNote> notes,
        int remainingPixels, ref int usedMaskPixels)
    {
        var name = Sanitize(layer.Name);
        var canvas = new PsdRect(0, 0, document.Width, document.Height);
        if (layer.CroppedToCanvas)
        {
            notes.Add(new PsdConversionNote(name,
                "Cropped to the canvas so the file fits in memory. Pixels outside the canvas weren't imported."));
        }
        switch (layer.Kind)
        {
            case PsdLayerKind.Text:
                notes.Add(new PsdConversionNote(name, "Editable Photoshop text becomes pixels and can't be retyped."));
                break;
            case PsdLayerKind.SmartObject:
                notes.Add(new PsdConversionNote(name, "The smart object was rasterized. Linked contents can't be edited."));
                break;
            case PsdLayerKind.Effects:
                notes.Add(new PsdConversionNote(name, "Layer effects were discarded, so the appearance may differ."));
                break;
            case PsdLayerKind.Vector:
                // Simple shapes and text stay editable on the Mac; here they keep the pixels Photoshop stored.
                notes.Add(new PsdConversionNote(name, "Vector shape was rasterized to pixels."));
                break;
        }
        if (layer.IsGroup)
        {
            if (layer.BlendKey != "pass")
            {
                notes.Add(new PsdConversionNote(name,
                    $"Folder blend mode \"{layer.BlendKey}\" isn't supported. The folder will be pass-through."));
            }
        }
        else if (PsdBlendMode.From(layer.BlendKey) is null && layer.BlendKey != "pass")
        {
            notes.Add(new PsdConversionNote(name,
                $"Blend mode \"{PsdBlendMode.Display(layer.BlendKey)}\" isn't supported and will be applied as Normal."));
        }

        var adjustment = layer.Kind == PsdLayerKind.Adjustment ? layer.Adjustment : null;
        if (layer.Kind == PsdLayerKind.Adjustment)
        {
            if (adjustment is null)
            {
                // The Mac build drops an adjustment it cannot express rather than showing its empty pixels.
                notes.Add(new PsdConversionNote(name, "This adjustment type isn't supported and was skipped."));
                return;
            }
            notes.Add(new PsdConversionNote(name, "Adjustment parameters may not match Photoshop exactly."));
        }

        // A layer with no pixels covers the canvas, as the Mac build places a folder, an adjustment or an
        // empty record: a zero-sized rectangle would be a transform the document cannot hold. Photoshop
        // leaves an adjustment layer's channels empty, and the Mac build keeps the adjustment rather than
        // the opaque pixels an empty record would otherwise become.
        var image = layer.IsGroup || adjustment is not null ? null : layer.Image;
        if (image is null) layer.Image?.Dispose();
        var placed = image is null ? canvas : layer.Bounds;
        if (Math.Abs(placed.Left) > 1_000_000 || Math.Abs(placed.Top) > 1_000_000) throw PsdFailure.TooLarge;
        var record = new ProjectLayerRecord
        {
            ID = layer.Id,
            Name = name,
            IsVisible = layer.IsVisible,
            Transform = new Format.LayerTransform
            {
                Origin = new JsonPoint(placed.Left, placed.Top),
                Size = new JsonSize(placed.Width, placed.Height),
                Sampling = LayerSampling.HighQuality,
            },
            ParentID = layer.ParentId,
            IsGroup = layer.IsGroup,
            Opacity = Math.Clamp(layer.Opacity, 0, 1),
            BlendMode = layer.IsGroup ? LayerBlendMode.Normal : PsdBlendMode.From(layer.BlendKey) ?? LayerBlendMode.Normal,
            Adjustment = adjustment,
        };

        if (image is not null)
        {
            record.ImageFile = Format.LayerMask.ExpectedImageFile(layer.Id);
            images[layer.Id] = ImportedImage.Create(image, name);
        }
        if (layer.Mask is not null)
        {
            var mask = MaskOnLayerGrid(layer, placed, image?.Width ?? canvas.Width, image?.Height ?? canvas.Height,
                remainingPixels, ref usedMaskPixels);
            record.MaskFile = Format.LayerMask.ExpectedFile(layer.Id);
            record.MaskEnabled = layer.MaskEnabled;
            record.MaskLinked = layer.MaskLinked;
            masks[layer.Id] = ImportedImage.Create(mask, "Layer Mask");
        }
        records.Add(record);
    }

    /// <summary>
    /// Photoshop's per-layer clipping flag means "clip to the layer below", which the document model
    /// expresses as `maskSourceID` naming the base layer. The base is the nearest layer below in the same
    /// folder that is neither a folder nor an adjustment layer; anything else leaves the clip unsupported.
    /// </summary>
    private static void ResolveClipping(PsdDocument document, List<ProjectLayerRecord> records, List<PsdConversionNote> notes)
    {
        var byId = new Dictionary<Guid, int>();
        for (var index = 0; index < records.Count; index++) byId[records[index].ID] = index;
        // Keyed by the folder a layer sits in, with an empty id standing for the top level, as the Mac build
        // keys it by an optional parent id.
        var baseForParent = new Dictionary<Guid, Guid?>();
        foreach (var layer in document.Layers)
        {
            if (!byId.TryGetValue(layer.Id, out var at)) continue;
            var record = records[at];
            var scope = record.ParentID ?? Guid.Empty;
            if (layer.Clipping)
            {
                if (!record.IsGroupValue && baseForParent.TryGetValue(scope, out var source) && source is { } baseId
                    && byId.TryGetValue(baseId, out var baseAt) && !records[baseAt].IsGroupValue && records[baseAt].Adjustment is null)
                {
                    record.MaskSourceID = baseId;
                }
                else
                {
                    notes.Add(new PsdConversionNote(record.Name, record.IsGroupValue
                        ? "Clipping a folder isn't supported, so clipping was skipped."
                        : "This clipping mask's base isn't supported, so clipping was skipped."));
                }
            }
            else if (!record.IsGroupValue && record.Adjustment is null)
            {
                baseForParent[scope] = record.ID;
            }
            else
            {
                baseForParent[scope] = null;
            }
        }
    }

    /// <summary>
    /// The stored mask patch on the layer's own pixel grid: Photoshop keeps only the part of a mask that
    /// isn't its default value, and the patch alone, stretched over the layer, would put the mask in the
    /// wrong place. An asset always matches its transform, so the patch lands at whole pixels.
    /// </summary>
    private static SKBitmap MaskOnLayerGrid(PsdRecord layer, PsdRect placed, int gridWidth, int gridHeight,
        int remainingPixels, ref int usedMaskPixels)
    {
        var patch = layer.Mask!;
        if (gridWidth < 1 || gridHeight < 1 || placed.IsEmpty || layer.MaskBounds.IsEmpty) return patch;

        var x = layer.MaskBounds.Left - placed.Left;
        var y = layer.MaskBounds.Top - placed.Top;
        if (x == 0 && y == 0 && patch.Width == gridWidth && patch.Height == gridHeight) return patch;

        var pixels = (long)gridWidth * gridHeight;
        if (pixels > DocumentLimits.MaxSurfacePixels || pixels > Math.Max(0, remainingPixels) - usedMaskPixels)
        {
            throw PsdFailure.TooLarge;
        }
        usedMaskPixels += (int)pixels;
        var mask = Bitmaps.Allocate(Bitmaps.MaskInfo(gridWidth, gridHeight));
        var gray = mask.GetPixelSpan();
        for (var row = 0; row < gridHeight; row++)
        {
            // Outside the patch Photoshop's default value stands.
            gray.Slice(row * mask.RowBytes, gridWidth).Fill(layer.MaskDefault);
        }
        var sourceX = Math.Max(0, -x);
        var sourceY = Math.Max(0, -y);
        var destinationX = Math.Max(0, x);
        var destinationY = Math.Max(0, y);
        var width = Math.Min(patch.Width - sourceX, gridWidth - destinationX);
        var height = Math.Min(patch.Height - sourceY, gridHeight - destinationY);
        if (width > 0 && height > 0)
        {
            var source = patch.GetPixelSpan();
            for (var row = 0; row < height; row++)
            {
                source.Slice((sourceY + row) * patch.RowBytes + sourceX, width)
                    .CopyTo(gray.Slice((destinationY + row) * mask.RowBytes + destinationX, width));
            }
        }
        patch.Dispose();
        return mask;
    }

    /// <summary>A layer name the format accepts: one is always there, and it is not longer than a manifest allows.</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Layer";
        if (Encoding.UTF8.GetByteCount(name) <= 16_384) return name;
        var length = name.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(name.AsSpan(0, length)) > 16_384) length--;
        return length > 0 ? name[..length] : "Layer";
    }
}
