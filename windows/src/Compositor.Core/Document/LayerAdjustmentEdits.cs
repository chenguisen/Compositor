using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// An adjustment layer's settings, read back for the panel and written back when they are changed. The
/// settings are a value, so replacing them is the whole edit and the history snapshot stays cheap.
/// </summary>
public static class LayerAdjustmentEdits
{
    /// <summary>The kind's settings as the layer holds them, or null when the layer is not an adjustment.</summary>
    public static LayerAdjustment? Settings(CanvasDocument document, Guid layerID) =>
        document.Layers.FirstOrDefault(layer => layer.ID == layerID)?.Adjustment;

    /// <summary>
    /// Puts new settings on an adjustment layer. False when the layer is not one, or when the settings are
    /// not ones the renderer may use — in which case it is left as it was.
    /// </summary>
    public static bool Set(CanvasDocument document, Guid layerID, LayerAdjustment settings)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Adjustment: not null } layer) return false;
        if (!settings.IsValid) return false;
        if (ReferenceEquals(layer.Adjustment, settings)) return false;
        layer.Adjustment = settings;
        return true;
    }
}
