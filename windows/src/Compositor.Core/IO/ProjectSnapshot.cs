using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.IO;

/// <summary>A manifest with the pixels its layers and masks point at.</summary>
public sealed class ProjectSnapshot : IDisposable
{
    public ProjectSnapshot(ProjectManifest manifest)
    {
        Manifest = manifest;
    }

    public ProjectSnapshot(ProjectManifest manifest, Dictionary<Guid, ImportedImage> images, Dictionary<Guid, ImportedImage> masks)
    {
        Manifest = manifest;
        Images = images;
        Masks = masks;
    }

    public ProjectManifest Manifest { get; }

    public Dictionary<Guid, ImportedImage> Images { get; } = [];

    public Dictionary<Guid, ImportedImage> Masks { get; } = [];

    /// <summary>The runtime mask of a record, or nil when the layer has none or its pixels are missing.</summary>
    public Model.LayerMask? MaskFor(ProjectLayerRecord layer)
    {
        if (layer.MaskFile is null || !Masks.TryGetValue(layer.ID, out var asset)) return null;
        return new Model.LayerMask(asset, layer.MaskEnabled ?? true,
            layer.MaskPlacement?.ToRuntime(), layer.MaskLinked ?? true);
    }

    /// <summary>The rows the layers panel draws, and the layers a renderer draws, in document order.</summary>
    public List<ImageLayer> RuntimeLayers()
    {
        var layers = new List<ImageLayer>(Manifest.Layers.Count);
        foreach (var record in Manifest.Layers)
        {
            var layer = new ImageLayer(record.ID, Images.GetValueOrDefault(record.ID), record.Transform.ToRuntime(),
                record.Name)
            {
                IsVisible = record.IsVisible,
                ParentID = record.ParentID,
                IsGroup = record.IsGroup ?? false,
                Opacity = record.Opacity ?? 1,
                BlendMode = record.BlendMode ?? LayerBlendMode.Normal,
                MaskSourceID = record.MaskSourceID,
                Mask = MaskFor(record),
                Adjustment = record.Adjustment,
                Effects = record.Effects,
                Shape = record.Shape is { } shape && Images.TryGetValue(record.ID, out var pixels)
                    ? new LayerShape(shape, pixels.Image) : null,
                Text = record.Text is { } text && Images.TryGetValue(record.ID, out var textPixels)
                    ? new LayerText(text, textPixels.Image) : null,
            };
            layers.Add(layer);
        }
        return layers;
    }

    /// <summary>The document this snapshot holds, ready to edit. Takes a copy of the pixel references.</summary>
    public CanvasDocument ToDocument()
    {
        var document = new CanvasDocument(Manifest.DocumentID, Manifest.Width, Manifest.Height, Manifest.Resolution ?? 72);
        document.Layers.AddRange(RuntimeLayers());
        document.Guides.AddRange(Manifest.Guides ?? []);
        return document;
    }

    /// <summary>
    /// The snapshot that saves a document as it stands. It shares the document's pixels, so the document
    /// keeps owning them: save this and drop it, do not dispose it while the document is alive.
    /// </summary>
    public static ProjectSnapshot FromDocument(CanvasDocument document)
    {
        var manifest = new ProjectManifest
        {
            Version = ProjectManifest.Current,
            DocumentID = document.ID,
            Width = document.Width,
            Height = document.Height,
            Resolution = document.Resolution,
            ActiveLayerID = document.Layers.Count > 0 ? document.Layers[^1].ID : null,
            Layers = [.. document.Layers.Select(CanvasDocument.Record)],
            Guides = document.Guides.Count > 0 ? [.. document.Guides] : null,
        };
        var snapshot = new ProjectSnapshot(manifest);
        foreach (var layer in document.Layers)
        {
            if (layer.Asset is { } asset) snapshot.Images[layer.ID] = asset;
            if (layer.Mask is { } mask) snapshot.Masks[layer.ID] = mask.Asset;
        }
        return snapshot;
    }

    public void Dispose()
    {
        foreach (var image in Images.Values) image.Dispose();
        foreach (var mask in Masks.Values) mask.Dispose();
        Images.Clear();
        Masks.Clear();
    }
}
