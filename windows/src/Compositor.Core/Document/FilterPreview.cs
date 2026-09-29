using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// A filter being looked at rather than applied: the layers' pixels are filtered into a document of their own,
/// so the canvas can show the result while the amounts are still being moved. The document it came from is
/// never touched — nothing is committed until the panel says so.
/// <para>
/// Every look starts again from the layers' own pixels rather than from the last look, so moving a slider
/// does not quietly filter the filter; the pixels each look makes are its own to free, while the layers' own
/// are never its to free — which is the one thing here that is easy to get wrong, and what the tests hold it to.
/// </para>
/// </summary>
public sealed class FilterPreview : IDisposable
{
    /// <summary>One layer being watched: what it really has, and what the last look made of it.</summary>
    private sealed class Watched(Guid id, ImportedImage? pixels, LayerTransform placed, LayerMask? mask)
    {
        public Guid ID { get; } = id;
        public ImportedImage? Pixels { get; } = pixels;
        public LayerTransform Placed { get; } = placed;
        public LayerMask? Mask { get; } = mask;
        public ImportedImage? Made { get; set; }
        public LayerTransform? MadePlaced { get; set; }
        public LayerMask? MadeMask { get; set; }
    }

    private readonly List<Watched> _watched = [];
    private bool _disposed;

    private FilterPreview(CanvasDocument preview) => Document = preview;

    /// <summary>The document to draw instead of the one being edited. It shares everything but the filtered
    /// layers, so the rest of the picture is what it always was.</summary>
    public CanvasDocument Document { get; }

    /// <summary>
    /// A preview of one layer, or null when there is no such layer. The document is a copy: it shares the
    /// pixels, and disposing this preview leaves them alone.
    /// </summary>
    public static FilterPreview? Begin(CanvasDocument document, Guid layerID) => Begin(document, [layerID]);

    /// <summary>
    /// A preview of several layers at once, so a filter taking more than one can be looked at too. Null when
    /// none of them is there.
    /// </summary>
    public static FilterPreview? Begin(CanvasDocument document, IReadOnlyList<Guid> layerIDs)
    {
        var preview = new FilterPreview(document.Clone());
        foreach (var id in layerIDs)
        {
            if (preview.Document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) continue;
            preview._watched.Add(new Watched(id, layer.Asset, layer.Transform, layer.Mask));
        }
        if (preview._watched.Count == 0)
        {
            preview.Dispose();
            return null;
        }
        return preview;
    }

    /// <summary>
    /// Runs the single watched layer's own copy of it through whatever <paramref name="apply"/> does, and shows
    /// that. False when the filter refused, in which case what is on the canvas stands.
    /// </summary>
    public bool Show(Func<CanvasDocument, Guid, bool> apply)
    {
        if (_watched.Count != 1) return false;
        var watched = _watched[0];
        return Show(document => apply(document, watched.ID));
    }

    /// <summary>
    /// Runs every watched layer through <paramref name="apply"/>, each from what it really has, and shows that.
    /// False when the filter refused, in which case what was on the canvas is put back.
    /// </summary>
    public bool Show(Func<CanvasDocument, bool> apply)
    {
        if (_disposed) return false;
        // Back to what the layers really have, so the amounts are filtered once and not on top of each other.
        foreach (var watched in _watched)
        {
            if (Document.Layers.FirstOrDefault(layer => layer.ID == watched.ID) is not { } layer) continue;
            layer.Asset = watched.Pixels;
            layer.Transform = watched.Placed;
            layer.Mask = watched.Mask;
        }
        if (!apply(Document))
        {
            // Refused: the last look stays on the canvas, rather than the picture falling back to how it
            // started because a slider was dragged past its range.
            foreach (var watched in _watched) Restore(watched, viewed: true);
            return false;
        }
        // What a filter changes may be pixels, a transform, a mask or an adjustment layer's settings, so a
        // successful apply is a preview even when nothing came out with a new asset.
        foreach (var watched in _watched) Restore(watched, viewed: false);
        return true;
    }

    /// <summary>
    /// A watched layer put back on the canvas: the last look's pixels when there was one, and what the layer
    /// really has otherwise. Its own pixels are never taken over, so they are never freed here.
    /// </summary>
    private void Restore(Watched watched, bool viewed)
    {
        if (Document.Layers.FirstOrDefault(layer => layer.ID == watched.ID) is not { } layer) return;
        if (!viewed)
        {
            if (!ReferenceEquals(layer.Asset, watched.Pixels))
            {
                watched.Made?.Dispose();
                watched.Made = layer.Asset;
                watched.MadePlaced = layer.Transform;
            }
            if (!ReferenceEquals(layer.Mask, watched.Mask))
            {
                watched.MadeMask?.Dispose();
                watched.MadeMask = layer.Mask;
            }
            return;
        }
        layer.Asset = watched.Made ?? watched.Pixels;
        layer.Mask = watched.MadeMask ?? watched.Mask;
        layer.Transform = watched.MadePlaced ?? watched.Placed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watched in _watched)
        {
            watched.Made?.Dispose();
            watched.Made = null;
            watched.MadeMask?.Dispose();
            watched.MadeMask = null;
        }
        _watched.Clear();
        Document.Dispose();
    }
}
