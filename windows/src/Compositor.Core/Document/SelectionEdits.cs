using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>How a new shape meets the selection already there.</summary>
public enum SelectionMode
{
    /// <summary>What the shape encloses becomes the selection.</summary>
    Replace,

    /// <summary>The shape is added to it.</summary>
    Add,

    /// <summary>The shape is taken out of it.</summary>
    Subtract,
}

/// <summary>What the Magic Wand matches, as its tool header sets it.</summary>
public sealed record WandOptions(int Radius = 4, int Tolerance = 32, bool Contiguous = true);

/// <summary>
/// The pixels a selection has lifted, while a drag is carrying them. The layer is left holding the hole they came
/// out of, and what it held before is kept so a drag that comes to nothing can put it back. The cut pixels are
/// held twice over: at the size a canvas draws them in document space while the pointer moves, and at the layer's
/// own size, which is what commits them where they land.
/// </summary>
public sealed class FloatingPixels : IDisposable
{
    internal FloatingPixels(Guid layerID, ImportedImage? before, SKBitmap cut, SKPoint cutAt, SKBitmap inLayer,
        DocumentSelection origin)
    {
        LayerID = layerID;
        Before = before;
        Cut = cut;
        CutAt = cutAt;
        InLayer = inLayer;
        Origin = origin;
    }

    /// <summary>The layer whose pixels these are.</summary>
    public Guid LayerID { get; }

    /// <summary>What that layer held before the lift, which is what a cancelled drag puts back.</summary>
    internal ImportedImage? Before { get; set; }

    /// <summary>The pixels being carried, over the region the selection covers, at document size.</summary>
    public SKBitmap Cut { get; }

    /// <summary>Where the cut's top-left corner sits in the document, so a canvas can draw it where it belongs.</summary>
    public SKPoint CutAt { get; }

    /// <summary>The same pixels at the layer's own size, which is what is put down where they land.</summary>
    internal SKBitmap InLayer { get; }

    /// <summary>The outline as it was, which is what a drag shifts to show where they would land.</summary>
    public DocumentSelection Origin { get; }

    public void Dispose()
    {
        Cut.Dispose();
        InLayer.Dispose();
    }
}

/// <summary>
/// Choosing what later edits act on. A selection is part of the document, so undo and redo cover changing
/// it, though it is not saved to disk.
/// </summary>
public static class SelectionEdits
{
    /// <summary>How far an expand, a contract or a feather may go, as the Mac build limits them.</summary>
    public const int MaxAmount = 500;
    public const int MaxFeather = 250;

    /// <summary>
    /// Selects the whole canvas. It is not the same as having no selection: an edit may touch everything
    /// either way, but this one is a selection the tools show, so a fill or a filter can be aimed at it.
    /// </summary>
    public static bool SelectAll(CanvasDocument document) => Adopt(document, WholeCanvas(document));

    /// <summary>Leaves nothing selected, so edits act on the whole document again.</summary>
    public static bool Deselect(CanvasDocument document)
    {
        if (document.Selection.Path is null) return false;
        document.Selection = DocumentSelection.All;
        return true;
    }

    /// <summary>
    /// Selects a rectangle of the document, held to its bounds, as the marquee tool does. A drag that never
    /// enters the canvas draws no marquee at all, so it leaves nothing selected rather than making a
    /// selection that holds every later edit back — the one place this port reads an empty shape as no
    /// selection, where the Mac build would keep an empty outline.
    /// </summary>
    public static bool Select(CanvasDocument document, SKRectI rect, bool antialiased = true)
    {
        var shape = Rectangular(document, rect);
        return shape.IsEmpty ? Deselect(document) : Adopt(document, shape, antialiased);
    }

    /// <summary>Selects the ellipse inside a box, which is what the elliptical marquee drags out.</summary>
    public static bool SelectEllipse(CanvasDocument document, SKRectI box, bool antialiased = true)
    {
        var held = SKRectI.Intersect(box, SKRectI.Create(0, 0, document.Width, document.Height));
        if (held.Width <= 0 || held.Height <= 0) return Adopt(document, new SKPath(), antialiased);
        using var builder = new SKPathBuilder();
        builder.AddOval(SKRect.Create(held.Left, held.Top, held.Width, held.Height), SKPathDirection.Clockwise);
        return Adopt(document, builder.Detach(), antialiased);
    }

    /// <summary>
    /// A freehand or polygonal lasso: the outline through the points, closed. Fewer than three points
    /// enclose nothing, so the selection is let go, as clicking the lasso on its own does in Photoshop.
    /// </summary>
    public static bool SelectLasso(CanvasDocument document, IReadOnlyList<SKPoint> points, bool antialiased = true) =>
        points.Count < 3 ? Deselect(document) : Adopt(document, Lasso(points), antialiased);

    /// <summary>
    /// Lifts the pixels inside the selection off the layer, leaving the hole they came out of, and hands them back
    /// for a drag to carry — which is what Command-dragging inside a selection does on the Mac. Null when there is
    /// nothing to lift.
    /// <para>
    /// The layer is holed here and now, so the drag shows the hole rather than pretending; what it held is kept
    /// for <see cref="DropPixels"/> to put back, and the pixels themselves for <see cref="SettlePixels"/> to put
    /// down where the drag ends.
    /// </para>
    /// </summary>
    public static FloatingPixels? LiftPixels(CanvasDocument document, Guid layerID)
    {
        if (document.Selection.Path is not { IsEmpty: false }) return null;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { IsGroup: false, Asset: { } asset } layer)
        {
            return null;
        }
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return null;
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, width, height);
        using var coverage = FillEdits.Coverage(document, out var region);
        if (region.Width <= 0 || region.Height <= 0) return null;

        // The layer's own pixels, and the copy that will keep the part taken out of them.
        var hole = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        var inLayer = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        foreach (var into in new[] { hole, inLayer })
        {
            using var canvas = new SKCanvas(into);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(asset.Image, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        // The same part as a canvas draws it, over the region the selection covers and no more. Drawn from the
        // layer's own pixels through its own transform rather than through the renderer: the renderer's colour
        // handling moves a level or two, and a preview that names one colour while carrying another is a lie.
        var cut = Bitmaps.Allocate(Bitmaps.ColorInfo(region.Width, region.Height));
        using (var canvas = new SKCanvas(cut))
        {
            // The shift into the region's own corner first, then the layer's transform into the document: built
            // as one matrix rather than two calls, because a canvas concatenates the other way round from the way
            // it reads and two calls in the order they look right are the wrong way round.
            canvas.SetMatrix(SKMatrix.CreateTranslation(-region.Left, -region.Top).PostConcat(toDocument));
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(asset.Image, new SKPoint(0, 0), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }

        var kept = inLayer.GetPixelSpan();
        var left = hole.GetPixelSpan();
        var carried = cut.GetPixelSpan();
        var lifted = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * hole.RowBytes + x * 4;
                var amount = FillEdits.Amount(coverage, region, toDocument.MapPoint(x + 0.5f, y + 0.5f));
                var solid = left[at + 3];
                for (var channel = 0; channel < 4; channel++)
                {
                    // Nothing outside the selection is carried: what is left there would otherwise be put down
                    // again shifted, smearing the layer over itself.
                    kept[at + channel] = amount <= 0
                        ? (byte)0
                        : (byte)Math.Clamp(Math.Round(kept[at + channel] * amount, MidpointRounding.AwayFromZero),
                            0, 255);
                    left[at + channel] = (byte)Math.Clamp(
                        Math.Round(left[at + channel] * (1 - amount), MidpointRounding.AwayFromZero), 0, 255);
                }
                if (amount > 0 && solid > 0) lifted++;
            }
        }
        if (lifted == 0)
        {
            hole.Dispose();
            inLayer.Dispose();
            cut.Dispose();
            return null;
        }
        // The cut is at document size over the region, so the canvas's own pixel for it is the document's.
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var at = y * cut.RowBytes + x * 4;
                var amount = FillEdits.Amount(coverage, region, new SKPoint(region.Left + x + 0.5f, region.Top + y + 0.5f));
                for (var channel = 0; channel < 4; channel++)
                {
                    carried[at + channel] = amount <= 0
                        ? (byte)0
                        : (byte)Math.Clamp(Math.Round(carried[at + channel] * amount, MidpointRounding.AwayFromZero),
                            0, 255);
                }
            }
        }
        layer.Asset = ImportedImage.Create(hole, asset.Name);
        return new FloatingPixels(layerID, asset, cut, new SKPoint(region.Left, region.Top), inLayer,
            document.Selection);
    }

    /// <summary>
    /// Puts the lifted pixels down where the drag ended, writes them into the layer and takes the outline with
    /// them. The hole is already in the layer, so this only draws what was carried onto it.
    /// </summary>
    public static bool SettlePixels(CanvasDocument document, FloatingPixels floating, int dx, int dy)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == floating.LayerID)
            is not { IsGroup: false, Asset: { } asset } layer)
        {
            return false;
        }
        var width = asset.Width;
        var height = asset.Height;
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, width, height);
        if (!toDocument.TryInvert(out var toPixel)) return false;
        var carried = toPixel.MapVector(new SKPoint(dx, dy));
        var across = (int)Math.Round(carried.X);
        var down = (int)Math.Round(carried.Y);

        var painted = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(asset.Image, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
            using var over = new SKPaint { BlendMode = SKBlendMode.SrcOver };
            canvas.DrawBitmap(floating.InLayer, new SKPoint(across, down),
                new SKSamplingOptions(SKFilterMode.Nearest), over);
        }
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        document.Selection = floating.Origin.Translated(dx, dy);
        floating.Dispose();
        return true;
    }

    /// <summary>Puts back what the layer held before the lift, for a drag that came to nothing.</summary>
    public static void DropPixels(CanvasDocument document, FloatingPixels floating)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == floating.LayerID) is { } layer
            && floating.Before is { } before)
        {
            // Put back rather than dropped, so it is not disposed with the floating pixels below.
            layer.Asset = before;
            floating.Before = null;
        }
        floating.Dispose();
    }

    /// <summary>
    /// Moves the pixels inside the selection by whole document pixels, leaving the hole they came out of, and
    /// takes the outline along with them. This is the Mac build's own nudgePixels: what Command with an arrow key
    /// does, and what dragging a selection's contents does there.
    /// <para>
    /// Whole pixels on purpose. Pressing it again lifts and puts down the same pixels on the grid they are
    /// already on, so a long walk across the canvas never resamples them; a move of half a pixel would blur and
    /// a second one would blur again.
    /// </para>
    /// </summary>
    public static bool MovePixels(CanvasDocument document, Guid layerID, int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || document.Selection.Path is not { IsEmpty: false }) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { IsGroup: false, Asset: { } asset } layer)
        {
            return false;
        }
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, width, height);
        if (!toDocument.TryInvert(out var toPixel)) return false;
        // The move is asked for in document pixels and the pixels live on the layer's own grid, so the offset is
        // carried into that grid — for a layer turned on the canvas those are not the same offset at all.
        var carried = toPixel.MapVector(new SKPoint(dx, dy));
        var across = (int)Math.Round(carried.X);
        var down = (int)Math.Round(carried.Y);

        using var coverage = FillEdits.Coverage(document, out var region);
        // The layer's own pixels, which are handed over at the end rather than disposed with: the layer keeps
        // whatever bitmap it is given, so this one is only let go of on the way out of a move that moved nothing.
        var painted = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(asset.Image, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        using var lifted = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(lifted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(asset.Image, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        // What is being moved is kept aside at its share of the coverage, and taken out of the layer by the rest
        // of it: a feathered outline lifts and leaves a soft edge, a hard one lifts whole pixels and no others.
        var kept = lifted.GetPixelSpan();
        var left = painted.GetPixelSpan();
        var moved = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * painted.RowBytes + x * 4;
                var amount = FillEdits.Amount(coverage, region, toDocument.MapPoint(x + 0.5f, y + 0.5f));
                var solid = left[at + 3];
                for (var channel = 0; channel < 4; channel++)
                {
                    // Nothing is carried from outside the selection. Scaling the copy there would leave it as it
                    // stands, and putting it down again would smear the whole layer over itself shifted.
                    kept[at + channel] = amount <= 0
                        ? (byte)0
                        : (byte)Math.Clamp(Math.Round(kept[at + channel] * amount, MidpointRounding.AwayFromZero),
                            0, 255);
                    left[at + channel] = (byte)Math.Clamp(
                        Math.Round(left[at + channel] * (1 - amount), MidpointRounding.AwayFromZero), 0, 255);
                }
                if (amount > 0 && solid > 0) moved++;
            }
        }
        if (moved == 0)
        {
            painted.Dispose();
            return false;
        }
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
            canvas.DrawBitmap(lifted, new SKPoint(across, down),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        document.Selection = document.Selection.Translated(dx, dy);
        return true;
    }

    /// <summary>The outline a lasso would close, for the tool to draw while it is still being dragged.</summary>
    public static SKPath Lasso(IReadOnlyList<SKPoint> points)
    {
        using var builder = new SKPathBuilder();
        if (points.Count == 0) return builder.Detach();
        if (points.Count < 3)
        {
            // Not an outline yet: a line from the first point to the last encloses nothing.
            builder.MoveTo(points[0]);
            if (points.Count == 2) builder.LineTo(points[1]);
            return builder.Detach();
        }
        builder.AddPoly(points is SKPoint[] array ? array : points.ToArray(), close: true);
        return builder.Detach();
    }

    /// <summary>
    /// The Magic Wand: everything like the pixel at a point. It reads the sample the caller made — the canvas
    /// as shown, or one layer's own pixels — matches from the seed, and outlines the result along exact pixel
    /// edges. Nothing matching leaves an empty selection in Replace mode, as the Mac build does.
    /// </summary>
    public static bool SelectWand(CanvasDocument document, SKBitmap sample, int x, int y, WandOptions options,
        SelectionMode mode, bool antialiased = true)
    {
        if (sample.Width <= 0 || sample.Height <= 0 || x < 0 || y < 0 || x >= sample.Width || y >= sample.Height)
        {
            return false;
        }
        var mask = new byte[(long)sample.Width * sample.Height];
        var matched = WandPixels.WandMask(sample.GetPixelSpan(), sample.Width, sample.Height, sample.RowBytes,
            x, y, Math.Clamp(options.Radius, 0, 100), Math.Clamp(options.Tolerance, 0, 255), options.Contiguous, mask);
        if (matched <= 0) return mode == SelectionMode.Replace ? Deselect(document) : false;
        if (WandPixels.WandTrace(mask, sample.Width, sample.Height, out var points, out _, out var loops,
            out var loopCount) != 0 || loopCount == 0)
        {
            // Too detailed to draw, or nothing came back: the selection is left as it was.
            return false;
        }
        using var builder = new SKPathBuilder();
        var index = 0;
        for (var loop = 0; loop < loopCount; loop++)
        {
            var length = loops[loop];
            var corners = new SKPoint[length];
            for (var corner = 0; corner < length; corner++)
            {
                corners[corner] = new SKPoint(points[(index + corner) * 2], points[(index + corner) * 2 + 1]);
            }
            builder.AddPoly(corners, close: true);
            index += length;
        }
        return Apply(document, builder.Detach(), mode, antialiased);
    }

    /// <summary>
    /// Select ▸ Colour Range: everything like the colours asked for, anywhere in the sample. The sample is the
    /// canvas as shown, as the Mac build's is, so a colour is matched wherever it appears — not just where it
    /// runs together. Colours to leave out take precedence over the ones to look for.
    /// </summary>
    public static bool SelectColorRange(CanvasDocument document, SKBitmap sample, IReadOnlyList<SKColor> include,
        IReadOnlyList<SKColor> exclude, int fuzziness, bool invert, SelectionMode mode)
    {
        if (sample.Width <= 0 || sample.Height <= 0 || include.Count == 0) return false;
        var mask = new byte[(long)sample.Width * sample.Height];
        var matched = WandPixels.ColorRangeMask(sample.GetPixelSpan(), sample.Width, sample.Height, sample.RowBytes,
            Colours(include), include.Count, Colours(exclude), exclude.Count,
            Math.Clamp(fuzziness, 0, 200), invert, mask);
        if (matched <= 0) return mode == SelectionMode.Replace ? Deselect(document) : false;
        if (Outline(mask, sample.Width, sample.Height) is not { } path) return false;
        using (path) return Apply(document, path, mode);
    }

    /// <summary>
    /// Select ▸ Layer's Pixels: what the layer shows — where it is at least half opaque — becomes the
    /// selection, in its place on the document. A folder holds no pixels of its own, so there is nothing to
    /// take. False when there is no such layer, when it shows nothing, or when the shape is too detailed to
    /// outline.
    /// </summary>
    public static bool SelectLayerPixels(CanvasDocument document, Guid layerID, SelectionMode mode = SelectionMode.Replace)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { IsGroup: false, Asset: { } asset } layer)
        {
            return false;
        }
        return SelectPixels(document, mode, asset.Width, asset.Height, channels: 4, picksBelow: false,
            BrushEdits.PixelToDocument(layer.Transform, asset.Width, asset.Height), asset.Image);
    }

    /// <summary>
    /// Select ▸ Mask's Black Areas: where the layer's mask hides it — anything darker than half — becomes the
    /// selection, in the mask's own place, which is the layer's when the mask has no placement of its own.
    /// </summary>
    public static bool SelectMaskDark(CanvasDocument document, Guid layerID, SelectionMode mode = SelectionMode.Replace)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return false;
        var width = mask.Asset.Width;
        var height = mask.Asset.Height;
        // A mask's black is what it hides, so it is the dark pixels that are taken.
        return SelectPixels(document, mode, width, height, channels: 1, picksBelow: true,
            BrushEdits.PixelToDocument(layer.MaskTransform, width, height), mask.Asset.Image);
    }

    /// <summary>
    /// The pixels of an image that pass a test — its alpha, or its gray — outlined along their exact edges and
    /// carried into the document, where the layer or mask sits.
    /// </summary>
    private static bool SelectPixels(CanvasDocument document, SelectionMode mode, int width, int height,
        int channels, bool picksBelow, SKMatrix toDocument, SKBitmap image)
    {
        if (width <= 0 || height <= 0) return false;
        var mask = new byte[(long)width * height];
        var pixels = image.GetPixelSpan();
        var stride = image.RowBytes;
        var picked = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // The last channel of a pixel is its alpha; a mask is one gray channel and nothing else.
                var value = pixels[y * stride + x * channels + channels - 1];
                if (picksBelow ? value >= 128 : value < 128) continue;
                mask[y * width + x] = 1;
                picked++;
            }
        }
        // Nothing passing leaves an empty selection in Replace mode, as the wand does.
        if (picked == 0) return mode == SelectionMode.Replace ? Deselect(document) : false;
        if (Outline(mask, width, height) is not { } path) return false;
        using (path)
        {
            path.Transform(toDocument);
            return Apply(document, path, mode);
        }
    }

    /// <summary>The colours as the kernel wants them: three straight-sRGB bytes each, one after another.</summary>
    private static byte[] Colours(IReadOnlyList<SKColor> colours)    {
        var bytes = new byte[colours.Count * 3];
        for (var index = 0; index < colours.Count; index++)
        {
            bytes[index * 3] = colours[index].Red;
            bytes[index * 3 + 1] = colours[index].Green;
            bytes[index * 3 + 2] = colours[index].Blue;
        }
        return bytes;
    }

    /// <summary>
    /// The outline of a mask, along exact pixel edges, or null when there is nothing to draw or it is too
    /// detailed to be worth drawing. The caller keeps what comes back.
    /// </summary>
    private static SKPath? Outline(byte[] mask, int width, int height)
    {
        if (WandPixels.WandTrace(mask, width, height, out var points, out _, out var loops, out var loopCount) != 0
            || loopCount == 0)
        {
            return null;
        }
        var builder = new SKPathBuilder();
        var index = 0;
        for (var loop = 0; loop < loopCount; loop++)
        {
            var length = loops[loop];
            var corners = new SKPoint[length];
            for (var corner = 0; corner < length; corner++)
            {
                corners[corner] = new SKPoint(points[(index + corner) * 2], points[(index + corner) * 2 + 1]);
            }
            builder.AddPoly(corners, close: true);
            index += length;
        }
        return builder.Detach();
    }

    /// <summary>The whole canvas minus what is selected, which is Select ▸ Inverse.</summary>
    public static bool Invert(CanvasDocument document)
    {
        if (document.Selection.Path is not { } path) return false;
        var inverted = Combine(WholeCanvas(document), path, SKPathOp.Difference);
        if (inverted is null) return false;
        document.Selection = document.Selection.WithPath(inverted);
        return true;
    }

    /// <summary>
    /// Moves the outline only, never the pixels: Select ▸ Move, and the arrow-key nudges. Whole pixels, so
    /// edges stay crisp.
    /// </summary>
    public static bool Move(CanvasDocument document, double dx, double dy)
    {
        if (document.Selection.Path is null) return false;
        var moved = document.Selection.Translated(Math.Round(dx), Math.Round(dy));
        if (moved.Matches(document.Selection)) return false;
        document.Selection = moved;
        return true;
    }

    /// <summary>
    /// Grows the outline by whole pixels with rounded corners, held to the canvas — Photoshop's Expand.
    /// </summary>
    public static bool Expand(CanvasDocument document, int amount) =>
        Resize(document, amount, "Expand Selection");

    /// <summary>
    /// Shrinks the outline by whole pixels, including away from the canvas edges. Contracting past the middle
    /// leaves an explicit empty selection.
    /// </summary>
    public static bool Contract(CanvasDocument document, int amount) =>
        Resize(document, -amount, "Contract Selection");

    /// <summary>
    /// Softens the edge by whole pixels, as Select ▸ Modify ▸ Feather does. Applying it again softens
    /// further, the way Expand and Contract stack up.
    /// </summary>
    public static bool Feather(CanvasDocument document, int amount)
    {
        if (document.Selection.Path is null || document.Selection.IsEmpty || amount <= 0) return false;
        // Two soft edges together spread a little less than their sum, as blurs do.
        var softened = Math.Sqrt(document.Selection.Feather * document.Selection.Feather + (double)amount * amount);
        document.Selection = document.Selection.WithFeather(Math.Min(MaxFeather, softened));
        return true;
    }

    /// <summary>
    /// A shape met against what is selected already: replacing it, adding to it, or taking out of it.
    /// Subtracting from nothing changes nothing, as in the Mac build.
    /// </summary>
    public static bool Apply(CanvasDocument document, SKPath shape, SelectionMode mode, bool antialiased = true)
    {
        var canvas = WholeCanvas(document);
        var clipped = Combine(shape, canvas, SKPathOp.Intersect);
        // Adding to a selection or taking out of one keeps the edge it already had: what is being changed is the
        // shape, not how its edge is drawn. That is the Mac build's own rule — it carries the selection's own
        // antialiased flag through an add and a subtract, and takes the tool's only when the shape replaces one.
        var kept = document.Selection.Antialiased;
        switch (mode)
        {
            case SelectionMode.Replace:
                return Adopt(document, clipped ?? new SKPath(), antialiased);
            case SelectionMode.Add:
                if (clipped is null) return false;
                if (document.Selection.Path is not { } current) return Adopt(document, clipped, antialiased);
                return Adopt(document, Combine(current, clipped, SKPathOp.Union) ?? new SKPath(), kept);
            default:
                if (document.Selection.Path is not { } held || clipped is null) return false;
                return Adopt(document, Combine(held, clipped, SKPathOp.Difference) ?? new SKPath(), kept);
        }
    }

    /// <summary>
    /// The shape a marquee drags out, un-clipped: the app hands it to <see cref="Apply"/> when it is adding
    /// to or taking out of a selection, which holds the result to the canvas itself.
    /// </summary>
    public static SKPath Shape(SKRectI box, bool ellipse)
    {
        using var builder = new SKPathBuilder();
        if (box.Width > 0 && box.Height > 0)
        {
            var rectangle = SKRect.Create(box.Left, box.Top, box.Width, box.Height);
            if (ellipse) builder.AddOval(rectangle, SKPathDirection.Clockwise);
            else builder.AddRect(rectangle, SKPathDirection.Clockwise);
        }
        return builder.Detach();
    }

    /// <summary>
    /// What the wand and the object tool read: the canvas as shown, or one layer's own pixels drawn at its
    /// transform without its mask, as a command-click selection reads them. Null when the layer has none.
    /// The caller owns the bitmap.
    /// </summary>
    public static SKBitmap? Sample(CanvasDocument document, Guid? layerID)
    {
        if (layerID is not { } id) return DocumentRenderer.Render(document);
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: { } } layer) return null;
        var sample = DocumentRenderer.Allocate(document.Width, document.Height);
        using (var canvas = new SKCanvas(sample)) DocumentRenderer.DrawLayerPixels(canvas, layer);
        return sample;
    }

    private static SKPath WholeCanvas(CanvasDocument document)
    {
        using var builder = new SKPathBuilder();
        if (document.Width > 0 && document.Height > 0)
        {
            builder.AddRect(SKRect.Create(0, 0, document.Width, document.Height), SKPathDirection.Clockwise);
        }
        return builder.Detach();
    }

    private static SKPath Rectangular(CanvasDocument document, SKRectI rect)
    {
        var held = SKRectI.Intersect(rect, SKRectI.Create(0, 0, document.Width, document.Height));
        using var builder = new SKPathBuilder();
        if (held.Width > 0 && held.Height > 0)
        {
            builder.AddRect(SKRect.Create(held.Left, held.Top, held.Width, held.Height), SKPathDirection.Clockwise);
        }
        return builder.Detach();
    }

    /// <summary>
    /// A band <c>|delta|</c> wide on each side of the outline, added to it or taken out of it. The band is
    /// what a round-capped stroke of twice that width would cover.
    /// </summary>
    private static bool Resize(CanvasDocument document, int amount, string name)
    {
        if (document.Selection.Path is not { } current || document.Selection.IsEmpty || amount == 0
            || Math.Abs(amount) > MaxAmount)
        {
            return false;
        }
        SKPath band;
        using (var builder = new SKPathBuilder())
        using (var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Abs(amount) * 2,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeMiter = 10,
        })
        {
            paint.GetFillPath(current, builder);
            band = builder.Detach();
        }
        if (band.IsEmpty) return false;
        var result = amount > 0
            ? Combine(Combine(current, band, SKPathOp.Union) ?? current, WholeCanvas(document), SKPathOp.Intersect)
            : Combine(current, band, SKPathOp.Difference);
        if (result is null) return false;
        document.Selection = document.Selection.WithPath(result);
        return true;
    }

    /// <summary>One outline combined with another, or null when Skia cannot work it out.</summary>
    private static SKPath? Combine(SKPath left, SKPath right, SKPathOp operation) => left.Op(right, operation);

    /// <summary>Adopts a shape as the whole selection, unless it is the selection already.</summary>
    /// <summary>
    /// Puts a new outline in place of whatever was selected. <paramref name="antialiased"/> is how the shape's
    /// edge is drawn when it is turned into coverage: a marquee dragged with it off has hard edges, which is
    /// what the Mac build's Anti-alias tick in the lasso's own controls is for.
    /// </summary>
    private static bool Adopt(CanvasDocument document, SKPath shape, bool antialiased = true)
    {
        var replaced = DocumentSelection.FromPath(shape, antialiased);
        if (replaced.Matches(document.Selection)) return false;
        document.Selection = replaced;
        return true;
    }
}
