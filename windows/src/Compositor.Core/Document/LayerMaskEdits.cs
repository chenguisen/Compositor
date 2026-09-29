using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// A layer's mask, and the clipping link that lets one layer supply coverage to another. Each changes the
/// document in place; the caller brackets it with the history so it becomes one undo step.
/// </summary>
public static class LayerMaskEdits
{
    /// <summary>
    /// A plain all-white mask, which reveals everything, or an all-black one, which hides it. Refused when
    /// the layer already has a mask. A folder takes one too, which then clips everything inside it.
    /// </summary>
    public static bool Add(CanvasDocument document, Guid layerID, bool revealing)
    {
        if (Lookup(document, layerID) is not { Mask: null } layer) return false;
        layer.Mask = Model.LayerMask.Solid(revealing);
        return true;
    }

    /// <summary>Turns a mask off, so the layer shows whole, or back on.</summary>
    public static bool SetEnabled(CanvasDocument document, Guid layerID, bool enabled)
    {
        if (Lookup(document, layerID)?.Mask is not { } mask || mask.IsEnabled == enabled) return false;
        mask.IsEnabled = enabled;
        return true;
    }

    /// <summary>Takes the mask off the layer, leaving its pixels as they were.</summary>
    public static bool Remove(CanvasDocument document, Guid layerID)
    {
        if (Lookup(document, layerID) is not { Mask: not null } layer) return false;
        layer.Mask = null;
        return true;
    }

    /// <summary>
    /// Linked, the mask moves with its layer; unlinked, each moves on its own. Nothing else changes.
    /// </summary>
    public static bool SetLinked(CanvasDocument document, Guid layerID, bool linked)
    {
        if (Lookup(document, layerID)?.Mask is not { } mask || mask.IsLinked == linked) return false;
        mask.IsLinked = linked;
        return true;
    }

    /// <summary>
    /// Whether the clipping link on a layer could be toggled: a layer already clipped can always be released
    /// from it, and one that is not can be clipped to the layer beneath it when that would leave a document
    /// that still loads.
    /// </summary>
    public static bool CanToggle(CanvasDocument document, Guid layerID)
    {
        if (Lookup(document, layerID) is not { IsGroup: false } layer) return false;
        if (layer.MaskSourceID is not null) return true;
        if (Below(document, layer) is not { IsGroup: false } below) return false;
        return CanLink(document, below.MaskSourceID ?? below.ID, layerID);
    }

    /// <summary>
    /// Clips a layer to the one beneath it, as ⌘⌥G does, or releases it when it is already clipped. The
    /// layer beneath a clipped one shares its base, so clipping a run of layers joins the same clip.
    /// </summary>
    public static bool Toggle(CanvasDocument document, Guid layerID)
    {
        if (Lookup(document, layerID) is not { IsGroup: false } layer) return false;
        if (layer.MaskSourceID is not null) return Release(document, layerID);
        if (Below(document, layer) is not { IsGroup: false } below) return false;
        return Link(document, below.MaskSourceID ?? below.ID, layerID);
    }

    /// <summary>Whether a link from one layer to another would leave a document that loads back.</summary>
    public static bool CanLink(CanvasDocument document, Guid source, Guid target)
    {
        if (source == target) return false;
        return LiveMaskGraph.CanLink(document.Layers.Select(CanvasDocument.Record).ToList(), source, target);
    }

    /// <summary>Makes one layer supply its coverage to another.</summary>
    public static bool Link(CanvasDocument document, Guid source, Guid target)
    {
        if (!CanLink(document, source, target)) return false;
        if (Lookup(document, target) is not { } layer || layer.MaskSourceID == source) return false;
        layer.MaskSourceID = source;
        return true;
    }

    /// <summary>
    /// Releases a clipping link, on a layer that holds one: the layer itself, and the run above it that
    /// shares the same clip — they were clipped to the same layer, so they let go together. What is beneath
    /// the released layer keeps its own clip.
    /// </summary>
    public static bool Release(CanvasDocument document, Guid layerID)
    {
        if (Lookup(document, layerID) is not { IsGroup: false, MaskSourceID: { } source } layer) return false;
        var siblings = document.Layers.Where(candidate => candidate.ParentID == layer.ParentID).ToList();
        var index = siblings.IndexOf(layer);
        if (index < 0) return false;
        // The run above it that clips to the same base: the layer itself, then the ones stacked on it.
        var released = 0;
        for (var at = index; at < siblings.Count; at++)
        {
            var candidate = siblings[at];
            if (candidate.ID != layerID && candidate.MaskSourceID != source) break;
            candidate.MaskSourceID = null;
            released++;
        }
        return released > 0;
    }

    private static ImageLayer? Lookup(CanvasDocument document, Guid layerID) =>
        document.Layers.FirstOrDefault(layer => layer.ID == layerID);

    /// <summary>The layer directly beneath this one among the layers it shares a folder with.</summary>
    private static ImageLayer? Below(CanvasDocument document, ImageLayer layer)
    {
        var siblings = document.Layers.Where(candidate => candidate.ParentID == layer.ParentID).ToList();
        var index = siblings.IndexOf(layer);
        return index > 0 ? siblings[index - 1] : null;
    }
}
