using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// A filter being looked at rather than applied: the layer's pixels are filtered into a document of its own,
/// so the canvas can show the result while the amounts are still being moved. The document it came from is
/// never touched — nothing is committed until the panel says so.
/// <para>
/// Every look starts again from the layer's own pixels rather than from the last look, so moving a slider
/// does not quietly filter the filter; the pixels each look makes are its own to free, while the layer's own
/// are never its to free — which is the one thing here that is easy to get wrong, and what the tests hold it to.
/// </para>
/// </summary>
public sealed class FilterPreview : IDisposable
{
    private readonly Guid _layerID;
    private readonly ImportedImage? _pixels;
    private readonly LayerTransform _placed;
    private readonly LayerMask? _mask;
    private ImportedImage? _made;
    private LayerMask? _madeMask;
    private bool _disposed;

    private FilterPreview(CanvasDocument preview, ImageLayer layer, Guid layerID)
    {
        Document = preview;
        _layerID = layerID;
        _pixels = layer.Asset;
        _placed = layer.Transform;
        _mask = layer.Mask;
    }

    /// <summary>The document to draw instead of the one being edited. It shares everything but the filtered
    /// layer, so the rest of the picture is what it always was.</summary>
    public CanvasDocument Document { get; }

    /// <summary>
    /// A preview of a layer, or null when there is no such layer. The document is a copy: it shares the
    /// pixels, and disposing this preview leaves them alone.
    /// </summary>
    public static FilterPreview? Begin(CanvasDocument document, Guid layerID)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { } layer) return null;
        return new FilterPreview(document.Clone(), layer, layerID);
    }

    /// <summary>
    /// Runs the layer's own copy of it through whatever <paramref name="apply"/> does, and shows that. False
    /// when the filter refused, in which case what is on the canvas stands.
    /// </summary>
    public bool Show(Func<CanvasDocument, Guid, bool> apply)
    {
        if (_disposed) return false;
        if (Document.Layers.FirstOrDefault(layer => layer.ID == _layerID) is not { } layer) return false;
        // Back to what the layer really has, so the amounts are filtered once and not on top of each other.
        layer.Asset = _pixels;
        layer.Transform = _placed;
        layer.Mask = _mask;
        if (!apply(Document, _layerID))
        {
            // Refused: what was on the canvas stays on it, rather than the picture falling back to how it
            // started because a slider was dragged past its range.
            layer.Asset = _made ?? _pixels;
            return false;
        }
        // What a filter changes may be pixels, a transform, a mask or an adjustment layer's settings, so a
        // successful apply is a preview even when nothing came out with a new asset.
        if (!ReferenceEquals(layer.Asset, _pixels))
        {
            _made?.Dispose();
            _made = layer.Asset;
        }
        if (!ReferenceEquals(layer.Mask, _mask))
        {
            _madeMask?.Dispose();
            _madeMask = layer.Mask;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _made?.Dispose();
        _made = null;
        _madeMask?.Dispose();
        _madeMask = null;
        Document.Dispose();
    }
}
