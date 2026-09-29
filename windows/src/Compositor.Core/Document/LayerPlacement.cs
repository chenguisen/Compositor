using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// Where a layer sits in the stack: adding a blank layer or a folder, wrapping layers in a folder, and
/// moving a layer into one. Each changes the document in place; the caller brackets it with the history so
/// it becomes one undo step.
/// </summary>
public static class LayerPlacement
{
    /// <summary>How many layers one document may hold, as the Mac build caps it.</summary>
    public const int MaxLayers = 10_000;

    /// <summary>
    /// A new empty layer above the selected one, in the folder that one is in — or, with a folder selected,
    /// at the top of that folder. Empty means no pixels at all until the first paint.
    /// </summary>
    public static Guid? AddBlank(CanvasDocument document, Guid? activeID)
    {
        if (document.Layers.Count >= MaxLayers) return null;
        var active = Active(document, activeID);
        var layer = new ImageLayer(Guid.NewGuid(), null, WholeCanvas(document), FreeName(document, "Layer"))
        {
            ParentID = active is { IsGroup: true } ? active.ID : active?.ParentID,
        };
        document.Layers.Insert(Above(document, active), layer);
        return layer.ID;
    }

    /// <summary>A new empty folder above the selected layer, in the folder that one is in.</summary>
    public static Guid? AddFolder(CanvasDocument document, Guid? activeID)
    {
        if (document.Layers.Count >= MaxLayers) return null;
        var active = Active(document, activeID);
        var folder = new ImageLayer(Guid.NewGuid(), null, WholeCanvas(document), FreeName(document, "Folder"))
        {
            IsGroup = true,
            ParentID = active is { IsGroup: true } ? active.ID : active?.ParentID,
        };
        document.Layers.Insert(Above(document, active), folder);
        return folder.ID;
    }

    /// <summary>
    /// A new adjustment layer above the selected one, in the folder that one is in. It holds no pixels: what
    /// it does is run its adjustment over everything under it, so it covers the whole canvas.
    /// </summary>
    public static Guid? AddAdjustment(CanvasDocument document, AdjustmentKind kind, Guid? activeID)
    {
        if (document.Layers.Count >= MaxLayers) return null;
        var active = Active(document, activeID);
        var layer = new ImageLayer(Guid.NewGuid(), null, WholeCanvas(document), FreeName(document, Name(kind)))
        {
            ParentID = active is { IsGroup: true } ? active.ID : active?.ParentID,
            Adjustment = new LayerAdjustment { Kind = kind },
        };
        document.Layers.Insert(Above(document, active), layer);
        return layer.ID;
    }

    /// <summary>What that kind of adjustment layer is called, as the Filter and Image menus name it.</summary>
    public static string Name(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.HueSaturation => "Hue/Saturation",
        AdjustmentKind.Levels => "Levels",
        AdjustmentKind.Curves => "Curves",
        AdjustmentKind.Exposure => "Exposure",
        AdjustmentKind.GradientMap => "Gradient Map",
        AdjustmentKind.Grain => "Grain",
        AdjustmentKind.AddNoise => "Add Noise",
        AdjustmentKind.GaussianBlur => "Gaussian Blur",
        AdjustmentKind.MotionBlur => "Motion Blur",
        AdjustmentKind.Invert => "Invert",
        AdjustmentKind.BlackWhite => "Black & White",
        _ => "Color Balance",
    };

    /// <summary>
    /// Wraps the given layers in a new folder, as the Mac build does it: a selected folder brings its whole
    /// subtree, and a layer inside a selected folder is not pulled out of it as well. The folder takes the
    /// place the topmost of them had, inside the folder they had in common.
    /// </summary>
    public static Guid? GroupSelected(CanvasDocument document, IReadOnlyCollection<Guid> ids)
    {
        if (document.Layers.Count >= MaxLayers) return null;
        var wanted = ids.ToHashSet();
        wanted.IntersectWith(document.Layers.Select(layer => layer.ID));
        if (wanted.Count == 0) return null;
        // A selected folder brings its contents, so a descendant of one is not a root of the new folder.
        var roots = wanted.Where(id => !Ancestors(document, id).Any(parent => parent is { } p && wanted.Contains(p)))
            .ToHashSet();
        var wrapped = document.HierarchyEntries().Select(entry => entry.Layer.ID).Where(roots.Contains).ToList();
        if (wrapped.Count == 0) return null;

        // The folder they all sit in — for one layer, simply its own parent.
        var candidates = Ancestors(document, wrapped[0]);
        Guid? parent = null;
        foreach (var candidate in candidates)
        {
            if (wrapped.All(id => Ancestors(document, id).Contains(candidate)))
            {
                parent = candidate;
                break;
            }
        }

        // The new folder goes where the topmost of the branches was, counting only what stays behind.
        var branches = wrapped.Select(id => Branch(document, id, parent)).ToHashSet();
        var highest = -1;
        for (var index = document.Layers.Count - 1; index >= 0 && highest < 0; index--)
        {
            if (branches.Contains(document.Layers[index].ID)) highest = index;
        }
        var insertion = highest < 0
            ? document.Layers.Count
            : document.Layers.Take(highest + 1).Count(layer => !roots.Contains(layer.ID));
        var planned = document.Layers.Where(layer => !roots.Contains(layer.ID)).ToList();
        var folder = new ImageLayer(Guid.NewGuid(), null, WholeCanvas(document), FreeName(document, "Folder"))
        {
            IsGroup = true,
            ParentID = parent,
        };
        planned.Insert(Math.Min(insertion, planned.Count), folder);
        // What was wrapped moves to the end of the array, as the Mac build leaves it: order comes from the
        // parent each record names, not from where it sits.
        var moved = document.Layers.Where(layer => wrapped.Contains(layer.ID)).ToList();
        planned.AddRange(moved);
        var parents = moved.ToDictionary(layer => layer.ID, _ => (Guid?)folder.ID);
        return Adopt(document, planned, parents, new Dictionary<Guid, Guid?>()) ? folder.ID : null;
    }

    /// <summary>Whether a layer may be moved to a folder: not into itself, nor into anything inside it.</summary>
    public static bool CanPlace(CanvasDocument document, Guid id, Guid? parent)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is null) return false;
        if (parent is not { } target) return true;
        return target != id && !document.Descendants(id).Contains(target)
            && document.Layers.FirstOrDefault(layer => layer.ID == target) is { IsGroup: true };
    }

    /// <summary>
    /// Moves a layer into a folder, above a given layer or at the very bottom. Clipping is settled the way
    /// the Mac build settles it: a layer dropped into a clipped stack joins the clip it landed in, and one
    /// that no longer has its base beneath it stops clipping.
    /// </summary>
    public static bool Place(CanvasDocument document, Guid id, Guid? parent, Guid? above = null,
        bool atBottom = false)
    {
        if (!CanPlace(document, id, parent) || above == id) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } moved) return false;
        var planned = document.Layers.Where(candidate => candidate.ID != id).ToList();
        var insertion = atBottom ? 0 : planned.Count;
        if (above is { } target)
        {
            var at = planned.FindIndex(candidate => candidate.ID == target && candidate.ParentID == parent);
            if (at < 0) return false;
            insertion = at + 1;
        }
        planned.Insert(insertion, moved);
        var parents = new Dictionary<Guid, Guid?> { [id] = parent };
        var sources = new Dictionary<Guid, Guid?>();
        AdoptIntoClip(planned, parents, sources, id);
        ReleaseDetached(planned, parents, sources);
        return Adopt(document, planned, parents, sources);
    }

    /// <summary>Moves a layer out of its folder, to just above the folder in the folder's own parent.</summary>
    public static bool MoveOutOfFolder(CanvasDocument document, Guid id)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { ParentID: { } parent }) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == parent) is not { } folder) return false;
        return Place(document, id, folder.ParentID, above: folder.ID);
    }

    /// <summary>
    /// Validates a planned stack, with the parents and clips the plan implies, and adopts it only if a
    /// document made from it would load back. Nothing is touched when it would not.
    /// </summary>
    private static bool Adopt(CanvasDocument document, List<ImageLayer> planned,
        IReadOnlyDictionary<Guid, Guid?> parents, IReadOnlyDictionary<Guid, Guid?> sources)
    {
        var records = new List<ProjectLayerRecord>(planned.Count);
        foreach (var layer in planned)
        {
            var record = CanvasDocument.Record(layer);
            if (parents.TryGetValue(layer.ID, out var parent)) record.ParentID = parent;
            if (sources.TryGetValue(layer.ID, out var source)) record.MaskSourceID = source;
            records.Add(record);
        }
        try
        {
            LayerHierarchy.Validate(records);
            LiveMaskGraph.Validate(records);
        }
        catch (ProjectException)
        {
            return false;
        }
        foreach (var layer in planned)
        {
            if (parents.TryGetValue(layer.ID, out var parent)) layer.ParentID = parent;
            if (sources.TryGetValue(layer.ID, out var source)) layer.MaskSourceID = source;
        }
        document.Layers.Clear();
        document.Layers.AddRange(planned);
        return true;
    }

    /// <summary>
    /// A layer dropped between a base and something clipped to it is clipped to that base too, as in
    /// Photoshop. An unclipped layer left in the middle of a stack breaks it up instead, which is why this
    /// runs before <see cref="ReleaseDetached"/>.
    /// </summary>
    private static void AdoptIntoClip(List<ImageLayer> planned, Dictionary<Guid, Guid?> parents,
        Dictionary<Guid, Guid?> sources, Guid id)
    {
        if (planned.FirstOrDefault(layer => layer.ID == id) is not { IsGroup: false } layer) return;
        var parent = Parent(planned, parents, layer);
        var siblings = planned.Where(candidate => Parent(planned, parents, candidate) == parent).ToList();
        var index = siblings.IndexOf(layer);
        if (index <= 0 || index + 1 >= siblings.Count) return;
        if (siblings[index + 1].MaskSourceID is not { } source || source == id) return;
        var below = siblings[index - 1];
        if (below.ID != source && below.MaskSourceID != source) return;
        sources[id] = source;
    }

    /// <summary>
    /// A layer keeps clipping only while its base is still the one beneath it in its stack. The first
    /// unclipped layer, or folder, starts a new stack, so anything below that point stops clipping — as in
    /// the Mac build, and as Photoshop does.
    /// </summary>
    private static void ReleaseDetached(List<ImageLayer> planned, Dictionary<Guid, Guid?> parents,
        Dictionary<Guid, Guid?> sources)
    {
        foreach (var stack in planned.GroupBy(layer => Parent(planned, parents, layer)))
        {
            Guid? baseID = null;
            foreach (var layer in stack)
            {
                if (Clipped(sources, layer) is { } source)
                {
                    if (source != baseID)
                    {
                        sources[layer.ID] = null;
                        baseID = layer.ID;
                    }
                }
                else
                {
                    baseID = layer.IsGroup ? null : layer.ID;
                }
            }
        }
    }

    /// <summary>The clip a layer holds once the plan is applied, if it holds one.</summary>
    private static Guid? Clipped(Dictionary<Guid, Guid?> sources, ImageLayer layer) =>
        sources.TryGetValue(layer.ID, out var source) ? source : layer.MaskSourceID;

    /// <summary>A layer's folder once the plan is applied.</summary>
    private static Guid? Parent(List<ImageLayer> planned, Dictionary<Guid, Guid?> parents, ImageLayer layer) =>
        parents.TryGetValue(layer.ID, out var parent) ? parent : layer.ParentID;

    /// <summary>The layers the panel has selected, as one.</summary>
    private static ImageLayer? Active(CanvasDocument document, Guid? activeID) =>
        activeID is { } id ? document.Layers.FirstOrDefault(layer => layer.ID == id) : null;

    /// <summary>Where a new layer goes: just above the active one, and inside a folder above its contents.</summary>
    private static int Above(CanvasDocument document, ImageLayer? active)
    {
        if (active is null) return document.Layers.Count;
        var insertion = document.Layers.IndexOf(active) + 1;
        if (active.IsGroup)
        {
            var inside = document.Descendants(active.ID);
            for (var index = document.Layers.Count - 1; index >= 0; index--)
            {
                if (!inside.Contains(document.Layers[index].ID)) continue;
                insertion = Math.Max(insertion, index + 1);
                break;
            }
        }
        return insertion;
    }

    /// <summary>The folders a layer sits in, innermost first, with a null for the top level at the end.</summary>
    private static List<Guid?> Ancestors(CanvasDocument document, Guid id)
    {
        var result = new List<Guid?>();
        var parent = document.Layers.FirstOrDefault(layer => layer.ID == id)?.ParentID;
        for (var depth = 0; parent is { } current && depth < 64; depth++)
        {
            result.Add(current);
            parent = document.Layers.FirstOrDefault(layer => layer.ID == current)?.ParentID;
        }
        result.Add(null);
        return result;
    }

    /// <summary>The layer whose parent is <paramref name="parent"/> and from which <paramref name="id"/> hangs.</summary>
    private static Guid Branch(CanvasDocument document, Guid id, Guid? parent)
    {
        var branch = id;
        for (var depth = 0; depth < 64; depth++)
        {
            var next = document.Layers.FirstOrDefault(layer => layer.ID == branch)?.ParentID;
            if (next is null || next == parent) break;
            branch = next.Value;
        }
        return branch;
    }

    /// <summary>A whole-canvas box, which is what a layer with no pixels of its own is given.</summary>
    private static Model.LayerTransform WholeCanvas(CanvasDocument document) =>
        new(0, 0, document.Width, document.Height);

    /// <summary>The first name of the form "{prefix} N" the document is not using.</summary>
    private static string FreeName(CanvasDocument document, string prefix)
    {
        var taken = document.Layers.Select(layer => layer.Name).ToHashSet();
        var number = 1;
        while (taken.Contains($"{prefix} {number}")) number++;
        return $"{prefix} {number}";
    }
}
