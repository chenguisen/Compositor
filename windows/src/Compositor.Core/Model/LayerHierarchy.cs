using Compositor.Core.Format;
using Compositor.Core.IO;

namespace Compositor.Core.Model;

/// <summary>
/// Folders: array order defines bottom-to-top sibling order, and renderers traverse each group as a
/// contiguous subtree. Visibility is inherited without changing child flags.
/// </summary>
public static class LayerHierarchy
{
    public readonly record struct Entry(ProjectLayerRecord Layer, int Depth, bool Visible);

    public static List<Entry> Entries(IReadOnlyList<ProjectLayerRecord> layers, bool topFirst = false,
        HashSet<Guid>? collapsed = null)
    {
        var roots = new List<ProjectLayerRecord>();
        var children = new Dictionary<Guid, List<ProjectLayerRecord>>();
        foreach (var layer in layers)
        {
            if (layer.ParentID is { } parent)
            {
                if (!children.TryGetValue(parent, out var siblings)) children[parent] = siblings = [];
                siblings.Add(layer);
            }
            else roots.Add(layer);
        }

        var result = new List<Entry>();
        Visit(null, 0, true);
        return result;

        void Visit(Guid? parent, int depth, bool visible)
        {
            if (depth > 64) return;
            var siblings = parent is { } id
                ? children.TryGetValue(id, out var found) ? found : null
                : roots;
            if (siblings is null) return;
            var ordered = topFirst ? Enumerable.Reverse(siblings) : siblings;
            foreach (var layer in ordered)
            {
                var effective = visible && layer.IsVisible;
                result.Add(new Entry(layer, depth, effective));
                if (layer.IsGroup == true && collapsed?.Contains(layer.ID) != true)
                {
                    Visit(layer.ID, depth + 1, effective);
                }
            }
        }
    }


    /// <summary>
    /// Cycles, missing or non-group parents, image-bearing groups and nesting beyond 64 ancestor levels
    /// are rejected. Group ancestors permit room for leaf nodes at the deepest level.
    /// </summary>
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var byID = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (byID.ContainsKey(layer.ID)) throw new ProjectException(ProjectError.Invalid);
            if (layer.IsGroup == true && layer.ImageFile is not null) throw new ProjectException(ProjectError.Invalid);
            byID[layer.ID] = layer;
        }
        foreach (var layer in layers)
        {
            var seen = new HashSet<Guid> { layer.ID };
            var parent = layer.ParentID;
            while (parent is { } id)
            {
                if (seen.Count > 64 || !seen.Add(id)) throw new ProjectException(ProjectError.Invalid);
                if (!byID.TryGetValue(id, out var node) || node.IsGroup != true) throw new ProjectException(ProjectError.Invalid);
                parent = node.ParentID;
            }
            // The deepest level may hold leaves only, so a folder at 64 ancestors has no room for itself.
            if (layer.IsGroup == true && seen.Count > 64) throw new ProjectException(ProjectError.Invalid);
        }
    }
}
