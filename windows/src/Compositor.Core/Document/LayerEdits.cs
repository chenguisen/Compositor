using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.Document;

/// <summary>
/// Edits that change where layers sit and which way they face. Each one changes the document in place; the
/// caller brackets it with the history's begin and end so it becomes one undo step.
/// </summary>
public static class LayerEdits
{
    /// <summary>
    /// Flips the named layers about a line through the middle of the box around them — so a single layer
    /// turns about its own middle. A folder named here brings its contents with it. Masks follow the link: a
    /// linked mask flips with its layer, an unlinked one stays where it is.
    /// </summary>
    public static bool Flip(CanvasDocument document, IReadOnlyCollection<Guid> ids, bool horizontally)
    {
        if (ids.Count == 0) return false;
        var members = Members(document, ids);
        if (members.Count == 0) return false;
        // The box is the union of the transform rectangles. A rotated layer's own corners are not used, so a
        // group of rotated layers can flip about a slightly different line than the Mac build's box.
        var minX = members.Min(layer => layer.Transform.X);
        var minY = members.Min(layer => layer.Transform.Y);
        var maxX = members.Max(layer => layer.Transform.X + layer.Transform.Width);
        var maxY = members.Max(layer => layer.Transform.Y + layer.Transform.Height);
        var axis = horizontally ? (minX + maxX) / 2 : (minY + maxY) / 2;
        foreach (var layer in members)
        {
            var flipped = layer.Transform.Mirrored(horizontally, axis);
            if (layer.Mask is { } mask) mask.Placement = mask.PlacementMoving(layer.Transform, flipped);
            layer.Transform = flipped;
        }
        return true;
    }

    /// <summary>
    /// Flips the whole canvas: every layer, every placed mask and every guide, mirrored across the canvas's
    /// middle.
    /// </summary>
    public static void FlipCanvas(CanvasDocument document, bool horizontally)
    {
        var axis = horizontally ? document.Width / 2.0 : document.Height / 2.0;
        foreach (var layer in document.Layers)
        {
            layer.Transform = layer.Transform.Mirrored(horizontally, axis);
            if (layer.Mask?.Placement is { } placement) layer.Mask.Placement = placement.Mirrored(horizontally, axis);
        }
        foreach (var guide in document.Guides)
        {
            // A vertical guide sits at an X, so it is the one a horizontal flip moves.
            if (guide.Axis == GuideAxis.Vertical == horizontally) guide.Position = 2 * axis - guide.Position;
        }
    }

    /// <summary>Moves one layer by whole document pixels. A linked mask follows it.</summary>
    public static bool Move(CanvasDocument document, Guid layerID, double dx, double dy)
    {
        if (Find(document, layerID) is not { } layer) return false;
        layer.Transform = layer.Transform with { X = layer.Transform.X + dx, Y = layer.Transform.Y + dy };
        return true;
    }

    /// <summary>Puts a layer exactly where a transform says, as the transform inspector does.</summary>
    public static bool SetTransform(CanvasDocument document, Guid layerID, Model.LayerTransform transform)
    {
        if (Find(document, layerID) is not { } layer) return false;
        layer.Transform = transform;
        return true;
    }

    /// <summary>Renames a layer. A name that is empty or only spaces is refused, as the Mac build refuses it.</summary>
    public static bool Rename(CanvasDocument document, Guid layerID, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || Find(document, layerID) is not { } layer || layer.Name == trimmed) return false;
        layer.Name = trimmed;
        return true;
    }

    /// <summary>Shows or hides a layer. Hiding a folder hides everything inside it.</summary>
    public static bool SetVisible(CanvasDocument document, Guid layerID, bool visible)
    {
        if (Find(document, layerID) is not { } layer || layer.IsVisible == visible) return false;
        layer.IsVisible = visible;
        return true;
    }

    /// <summary>How much of the layer shows through: 0 is nothing of it, 1 is all of it.</summary>
    public static bool SetOpacity(CanvasDocument document, Guid layerID, double opacity)
    {
        if (Find(document, layerID) is not { } layer) return false;
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1 || layer.Opacity == opacity) return false;
        layer.Opacity = opacity;
        return true;
    }

    /// <summary>How the layer is combined with what is under it, one of the modes the compositor knows.</summary>
    public static bool SetBlendMode(CanvasDocument document, Guid layerID, Format.LayerBlendMode mode)
    {
        if (Find(document, layerID) is not { } layer) return false;
        if (!Enum.IsDefined(mode) || layer.BlendMode == mode) return false;
        layer.BlendMode = mode;
        return true;
    }

    /// <summary>
    /// Moves a layer one place through the layers it shares a folder with: up is towards the top of the
    /// stack, which is the end of the array. Only the two records swap, so a folder still holds what is
    /// inside it.
    /// </summary>
    public static bool MoveBy(CanvasDocument document, Guid layerID, int offset)
    {
        if (offset == 0 || Find(document, layerID) is not { } layer) return false;
        var siblings = document.Layers.Where(candidate => candidate.ParentID == layer.ParentID).ToList();
        var place = siblings.IndexOf(layer);
        if (place < 0 || place + offset < 0 || place + offset >= siblings.Count) return false;
        var one = document.Layers.IndexOf(layer);
        var other = document.Layers.IndexOf(siblings[place + offset]);
        (document.Layers[one], document.Layers[other]) = (document.Layers[other], document.Layers[one]);
        return true;
    }

    /// <summary>
    /// Inserts a copy of a layer, and of everything a folder holds, just above it, with fresh ids and the
    /// same pixels: the copy and the original only part company once one of them is edited.
    /// </summary>
    public static Guid? Duplicate(CanvasDocument document, Guid layerID)
    {
        if (Find(document, layerID) is not { } layer) return null;
        var inside = document.Descendants(layerID);
        inside.Add(layerID);
        if (document.Layers.Count + inside.Count > 10_000) return null;
        var map = inside.ToDictionary(id => id, _ => Guid.NewGuid());
        var copies = document.Layers.Where(candidate => inside.Contains(candidate.ID)).Select(original =>
            original.Copy(
                map[original.ID],
                original.ID == layerID ? original.Name + " copy" : original.Name,
                Remap(original.ParentID),
                Remap(original.MaskSourceID))).ToList();
        document.Layers.InsertRange(document.Layers.IndexOf(layer) + 1, copies);
        return map[layerID];

        // A reference inside the copy becomes the copy of it; one from outside stays as it was.
        Guid? Remap(Guid? id) => id is { } found ? map.TryGetValue(found, out var mapped) ? mapped : found : null;
    }

    /// <summary>
    /// Deletes a layer and, for a folder, everything inside it. False when a layer that stays is clipped to
    /// one that goes: the Mac build asks whether to bake that appearance into the remaining layer's pixels
    /// or to unlink it, and this port has no such choice yet, so it refuses rather than change the picture
    /// without saying so.
    /// </summary>
    public static bool Delete(CanvasDocument document, Guid layerID)
    {
        if (Find(document, layerID) is not { } layer) return false;
        var removed = document.Descendants(layerID);
        removed.Add(layerID);
        foreach (var other in document.Layers)
        {
            if (!removed.Contains(other.ID) && other.MaskSourceID is { } source && removed.Contains(source)) return false;
        }
        document.Layers.RemoveAll(candidate => removed.Contains(candidate.ID));
        return true;
    }

    private static ImageLayer? Find(CanvasDocument document, Guid layerID) =>
        document.Layers.FirstOrDefault(layer => layer.ID == layerID);

    /// <summary>
    /// The layers a flip actually turns: the named layers that hold pixels, plus the contents of any folder
    /// named. A folder is not turned itself — its rectangle is what its mask covers, not a picture — so the
    /// box is measured around the contents, as the Mac build does.
    /// </summary>
    private static List<ImageLayer> Members(CanvasDocument document, IReadOnlyCollection<Guid> ids)
    {
        var folders = document.Layers.Where(layer => layer.IsGroup && ids.Contains(layer.ID))
            .Select(layer => layer.ID).ToHashSet();
        var wanted = new HashSet<Guid>();
        // Two passes, because a folder record may sit above or below its children in the array.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var layer in document.Layers)
            {
                if (ids.Contains(layer.ID))
                {
                    wanted.Add(layer.ID);
                    if (layer.IsGroup) folders.Add(layer.ID);
                }
                else if (layer.ParentID is { } parent && folders.Contains(parent))
                {
                    wanted.Add(layer.ID);
                }
            }
        }
        return document.Layers.Where(layer => wanted.Contains(layer.ID) && !layer.IsGroup).ToList();
    }
}
