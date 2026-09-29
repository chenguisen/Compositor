using Compositor.Core.Format;
using Compositor.Core.IO;

namespace Compositor.Core.Model;

/// <summary>
/// Clipping links: a non-group layer supplies live alpha to another. Missing references, self-links,
/// cycles, group endpoints and chains over 256 nodes are rejected.
/// </summary>
public static class LiveMaskGraph
{
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var records = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (!records.TryAdd(layer.ID, layer)) throw new ProjectException(ProjectError.Invalid);
        }
        foreach (var layer in layers)
        {
            var path = new HashSet<Guid>();
            var current = (Guid?)layer.ID;
            while (current is { } id)
            {
                if (path.Count >= 256 || !path.Add(id)) throw new ProjectException(ProjectError.Invalid);
                if (!records.TryGetValue(id, out var record)) throw new ProjectException(ProjectError.Invalid);
                if (record.MaskSourceID is { } source)
                {
                    if (record.IsGroup == true || !records.TryGetValue(source, out var sourceRecord)
                        || sourceRecord.IsGroup == true || sourceRecord.Adjustment is not null)
                    {
                        throw new ProjectException(ProjectError.Invalid);
                    }
                }
                current = record.MaskSourceID;
            }
        }
    }

    /// <summary>Whether a link from `source` to `target` would leave the graph valid.</summary>
    public static bool CanLink(IReadOnlyList<ProjectLayerRecord> layers, Guid source, Guid target)
    {
        var index = IndexOf(layers, target);
        if (index < 0) return false;
        var records = layers.ToList();
        records[index] = Copy(records[index], source);
        try
        {
            Validate(records);
            return true;
        }
        catch (ProjectException)
        {
            return false;
        }
    }

    private static int IndexOf(IReadOnlyList<ProjectLayerRecord> layers, Guid id)
    {
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].ID == id) return i;
        }
        return -1;
    }

    private static ProjectLayerRecord Copy(ProjectLayerRecord layer, Guid? source) => new()
    {
        ID = layer.ID,
        Name = layer.Name,
        IsVisible = layer.IsVisible,
        Transform = layer.Transform,
        ImageFile = layer.ImageFile,
        ParentID = layer.ParentID,
        IsGroup = layer.IsGroup,
        Opacity = layer.Opacity,
        BlendMode = layer.BlendMode,
        MaskFile = layer.MaskFile,
        MaskEnabled = layer.MaskEnabled,
        MaskSourceID = source,
        Adjustment = layer.Adjustment,
        MaskPlacement = layer.MaskPlacement,
        MaskLinked = layer.MaskLinked,
        Shape = layer.Shape,
        Effects = layer.Effects,
        Text = layer.Text,
    };
}
