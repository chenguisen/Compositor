using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// ⌘E: what a merge takes in, where the result goes, and that the picture it shows does not change.
/// Expected pixels are worked out from the formulas, not from a previous run.
/// </summary>
public class LayerMergeTests
{
    private static ImageLayer Patch(SKColor colour, double x, double y, int width, int height,
        double opacity = 1, LayerBlendMode blend = LayerBlendMode.Normal, string name = "Layer")
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new Model.LayerTransform(x, y, width, height, 0, false, false, LayerSampling.HighQuality), name)
        {
            Opacity = opacity,
            BlendMode = blend,
        };
    }

    private static ImageLayer Blank(string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(4, 4));
        bitmap.Erase(SKColors.Transparent);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new Model.LayerTransform(0, 0, 4, 4), name);
    }

    private static ImageLayer Folder(string name, double opacity = 1) =>
        // A folder holds no pixels of its own — the Mac build allocates them only when painting begins.
        new(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), name)
        {
            IsGroup = true,
            Opacity = opacity,
        };

    private static CanvasDocument Doc(int width, int height, params ImageLayer[] layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        document.Layers.AddRange(layers);
        return document;
    }

    /// <summary>
    /// The two pictures are the same, to the byte. A premultiplied render carried through straight alpha
    /// and back can land a channel one out, which is why a merged asset is stored the way any other is.
    /// </summary>
    private static void PaintsTheSame(SKBitmap before, SKBitmap after, int tolerance = 1)
    {
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        for (var y = 0; y < before.Height; y++)
        {
            for (var x = 0; x < before.Width; x++)
            {
                var was = before.GetPixel(x, y);
                var now = after.GetPixel(x, y);
                Assert.True(Math.Abs(was.Red - now.Red) <= tolerance && Math.Abs(was.Green - now.Green) <= tolerance
                    && Math.Abs(was.Blue - now.Blue) <= tolerance && Math.Abs(was.Alpha - now.Alpha) <= tolerance,
                    $"at {x},{y}: ({was.Red},{was.Green},{was.Blue},{was.Alpha}) became " +
                    $"({now.Red},{now.Green},{now.Blue},{now.Alpha})");
            }
        }
    }

    [Fact]
    public void MergeDownTakesTheLayerBeneathIt()
    {
        var bottom = Patch(SKColors.Blue, 0, 0, 8, 8, name: "Bottom");
        var top = Patch(SKColors.Red, 0, 0, 8, 8, name: "Top");
        using var document = Doc(8, 8, bottom, top);

        var plan = LayerMerge.Plan(document, [top.ID], top.ID);
        Assert.NotNull(plan);
        Assert.Equal("Merge Down", plan.Action);
        Assert.Equal(new[] { bottom.ID, top.ID }, plan.Members);
        Assert.Equal("Bottom", plan.Name);
        Assert.Null(plan.Parent);
        Assert.Equal(top.ID, plan.Anchor);

        Assert.NotNull(LayerMerge.Merge(document, [top.ID], top.ID));
        Assert.Single(document.Layers);
        Assert.Equal("Bottom", document.Layers[0].Name);
        Assert.NotEqual(top.ID, document.Layers[0].ID);
    }

    [Fact]
    public void TheYoungestLayerHasNothingBeneathIt()
    {
        var only = Patch(SKColors.Red, 0, 0, 8, 8);
        using var document = Doc(8, 8, only);

        Assert.Null(LayerMerge.Plan(document, [only.ID], only.ID));
        Assert.Null(LayerMerge.Merge(document, [only.ID], only.ID));
        Assert.Single(document.Layers);
    }

    [Fact]
    public void MergeDownStaysInsideTheFolder()
    {
        var beneath = Patch(SKColors.Blue, 0, 0, 8, 8, name: "Beneath");
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, name: "Inside");
        inside.ParentID = folder.ID;
        var upper = Patch(SKColors.Red, 0, 0, 8, 8, name: "Upper");
        upper.ParentID = folder.ID;
        using var document = Doc(8, 8, beneath, folder, inside, upper);

        // The top of the folder merges with the layer beneath it inside the folder, not with "Beneath".
        var plan = LayerMerge.Plan(document, [upper.ID], upper.ID);
        Assert.NotNull(plan);
        Assert.Equal(new[] { inside.ID, upper.ID }, plan.Members);
        Assert.Equal(folder.ID, plan.Parent);

        Assert.NotNull(LayerMerge.Merge(document, [upper.ID], upper.ID));
        Assert.Equal(3, document.Layers.Count);
        var merged = document.Layers.Single(layer => !layer.IsGroup && layer.ParentID == folder.ID);
        Assert.Equal("Inside", merged.Name);
        Assert.Contains(document.Layers, layer => layer.ID == beneath.ID);
    }

    [Fact]
    public void MergeDownRefusesAFolderBeneathIt()
    {
        var folder = Folder("Folder");
        var top = Patch(SKColors.Red, 0, 0, 8, 8, name: "Top");
        using var document = Doc(8, 8, folder, top);

        Assert.Null(LayerMerge.Plan(document, [top.ID], top.ID));
    }

    [Fact]
    public void MergeGroupTakesItsContentsAndTheFolderGoes()
    {
        var beneath = Patch(SKColors.Blue, 0, 0, 8, 8, name: "Beneath");
        var above = Patch(SKColors.Yellow, 0, 0, 8, 8, name: "Above");
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, name: "Inside");
        inside.ParentID = folder.ID;
        using var document = Doc(8, 8, beneath, folder, inside, above);

        var plan = LayerMerge.Plan(document, [folder.ID], folder.ID);
        Assert.NotNull(plan);
        Assert.Equal("Merge Group", plan.Action);
        Assert.Equal(new[] { folder.ID, inside.ID }, plan.Members);
        Assert.Equal("Folder", plan.Name);

        Assert.NotNull(LayerMerge.Merge(document, [folder.ID], folder.ID));
        Assert.Equal(3, document.Layers.Count);
        Assert.DoesNotContain(document.Layers, layer => layer.IsGroup);
        // The result sits in the folder's own slot, which was below "Above".
        Assert.Equal(1, document.Layers.FindIndex(layer => layer.ID != beneath.ID && layer.ID != above.ID));
    }

    [Fact]
    public void MergeGroupNeedsSomethingWithPixelsInside()
    {
        var empty = Folder("Empty");
        using var document = Doc(8, 8, empty);

        Assert.Null(LayerMerge.Plan(document, [empty.ID], empty.ID));
    }

    [Fact]
    public void MergeLayersTakesTheWholeSelection()
    {
        var bottom = Patch(SKColors.Blue, 0, 0, 8, 8, name: "Bottom");
        var middle = Patch(SKColors.Green, 0, 0, 8, 8, name: "Middle");
        var top = Patch(SKColors.Red, 0, 0, 8, 8, name: "Top");
        using var document = Doc(8, 8, bottom, middle, top);

        var plan = LayerMerge.Plan(document, [middle.ID, top.ID], top.ID);
        Assert.NotNull(plan);
        Assert.Equal("Merge Layers", plan.Action);
        Assert.Equal(new[] { middle.ID, top.ID }, plan.Members);
        // Named and placed by the topmost layer among the ones selected.
        Assert.Equal("Top", plan.Name);
        Assert.Equal(top.ID, plan.Anchor);

        Assert.NotNull(LayerMerge.Merge(document, [middle.ID, top.ID], top.ID));
        Assert.Equal(2, document.Layers.Count);
        Assert.Equal(bottom.ID, document.Layers[0].ID);
        Assert.Equal("Top", document.Layers[1].Name);
    }

    [Fact]
    public void MergeLayersNeedsSomethingWithPixelsSelected()
    {
        var one = Folder("One");
        var two = Folder("Two");
        using var document = Doc(8, 8, one, two);

        Assert.Null(LayerMerge.Plan(document, [one.ID, two.ID], two.ID));
    }

    [Fact]
    public void MergeLayersBringsWhatASelectedFolderHolds()
    {
        var folder = Folder("Folder");
        var inside = Patch(SKColors.Green, 0, 0, 8, 8, name: "Inside");
        inside.ParentID = folder.ID;
        var top = Patch(SKColors.Red, 0, 0, 8, 8, name: "Top");
        using var document = Doc(8, 8, folder, inside, top);

        var plan = LayerMerge.Plan(document, [folder.ID, top.ID], top.ID);
        Assert.NotNull(plan);
        Assert.Equal(new[] { folder.ID, inside.ID, top.ID }, plan.Members);
        Assert.Equal("Top", plan.Name);

        Assert.NotNull(LayerMerge.Merge(document, [folder.ID, top.ID], top.ID));
        Assert.Single(document.Layers);
        Assert.False(document.Layers[0].IsGroup);
    }

    [Fact]
    public void TheMergedLayerTakesTheSlotTheMembersHeld()
    {
        var a = Patch(SKColors.Blue, 0, 0, 8, 8, name: "A");
        var b = Patch(SKColors.Green, 0, 0, 8, 8, name: "B");
        var c = Patch(SKColors.Yellow, 0, 0, 8, 8, name: "C");
        var d = Patch(SKColors.Red, 0, 0, 8, 8, name: "D");
        using var document = Doc(8, 8, a, b, c, d);

        // B merges into A, so the result takes the place the pair held: where A was.
        Assert.NotNull(LayerMerge.Merge(document, [b.ID], b.ID));
        Assert.Equal(3, document.Layers.Count);
        Assert.Equal("A", document.Layers[0].Name);
        Assert.Equal(c.ID, document.Layers[1].ID);
        Assert.Equal(d.ID, document.Layers[2].ID);
    }

    [Fact]
    public void MergingTrimsToWhatIsThereAndKeepsThePlace()
    {
        var baseLayer = Patch(new SKColor(60, 90, 200), 20, 30, 10, 10, name: "Base");
        var upper = Patch(new SKColor(220, 180, 40), 22, 32, 4, 4, name: "Upper");
        using var document = Doc(64, 64, baseLayer, upper);

        Assert.NotNull(LayerMerge.Merge(document, [upper.ID], upper.ID));
        var merged = document.Layers.Single();

        // The union of the two boxes, in the same place on the canvas.
        Assert.Equal(20, merged.Transform.X);
        Assert.Equal(30, merged.Transform.Y);
        Assert.Equal(10, merged.Transform.Width);
        Assert.Equal(10, merged.Transform.Height);
        Assert.Equal(10, merged.Asset!.Width);
        Assert.Equal(10, merged.Asset.Height);
        // The upper patch, which covered the base from 2 to 6 of that box, is baked in.
        Assert.Equal(new SKColor(60, 90, 200), merged.Asset.Image.GetPixel(0, 0));
        Assert.Equal(new SKColor(220, 180, 40), merged.Asset.Image.GetPixel(3, 3));
        // Stored like every other layer's pixels, which is what the writer expects.
        Assert.Equal(SKAlphaType.Unpremul, merged.Asset.Image.AlphaType);
    }

    [Fact]
    public void MergingBakesBlendModeOpacityAndMaskIn()
    {
        var baseLayer = Patch(new SKColor(60, 90, 200), 0, 0, 16, 16, name: "Base");
        var upper = Patch(new SKColor(220, 180, 40), 2, 2, 10, 10, opacity: 0.5,
            blend: LayerBlendMode.Multiply, name: "Upper");
        var mask = new SKBitmap(Bitmaps.MaskInfo(4, 4));
        mask.Erase(new SKColor(128, 128, 128));
        upper.Mask = Model.LayerMask.AssetFrom(mask);
        using var document = Doc(16, 16, baseLayer, upper);
        using var before = DocumentRenderer.Render(document);

        Assert.NotNull(LayerMerge.Merge(document, [upper.ID], upper.ID));
        var merged = document.Layers.Single();
        Assert.Equal(LayerBlendMode.Normal, merged.BlendMode);
        Assert.Equal(1, merged.Opacity);
        Assert.Null(merged.Mask);
        using var after = DocumentRenderer.Render(document);
        PaintsTheSame(before, after);
    }

    [Fact]
    public void MergingAnAdjustmentPaintsTheSamePicture()
    {
        var baseLayer = Patch(new SKColor(60, 90, 200), 0, 0, 8, 8, name: "Base");
        var adjustment = new ImageLayer(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), "Invert")
        {
            Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert },
        };
        using var document = Doc(8, 8, baseLayer, adjustment);
        using var before = DocumentRenderer.Render(document);

        Assert.NotNull(LayerMerge.Merge(document, [adjustment.ID], adjustment.ID));
        var merged = document.Layers.Single();
        Assert.Null(merged.Adjustment);
        Assert.NotNull(merged.Asset);
        using var after = DocumentRenderer.Render(document);
        PaintsTheSame(before, after);
    }

    [Fact]
    public void MergingABlankStackKeepsTheWholeCanvas()
    {
        var one = Blank("One");
        var two = Blank("Two");
        using var document = Doc(12, 12, one, two);

        Assert.NotNull(LayerMerge.Merge(document, [two.ID], two.ID));
        var merged = document.Layers.Single();
        // Nothing was drawn, so there is nothing to trim to and the whole canvas is left.
        Assert.Equal(0, merged.Transform.X);
        Assert.Equal(0, merged.Transform.Y);
        Assert.Equal(12, merged.Transform.Width);
        Assert.Equal(12, merged.Transform.Height);
    }

    [Fact]
    public void AFolderOpacityIsBakedIntoTheMerge()
    {
        var folder = Folder("Folder", opacity: 0.5);
        var inside = Patch(new SKColor(200, 40, 40), 0, 0, 4, 4, name: "Inside");
        inside.ParentID = folder.ID;
        using var document = Doc(8, 8, folder, inside);

        Assert.NotNull(LayerMerge.Merge(document, [folder.ID], folder.ID));
        var pixel = document.Layers.Single().Asset!.Image.GetPixel(1, 1);
        // A folder multiplies into what is inside it, so the merge comes out half-transparent.
        Assert.True(Math.Abs(pixel.Alpha - 128) <= 2, $"alpha {pixel.Alpha} is not 128");
        Assert.True(Math.Abs(pixel.Red - 200) <= 2, $"red {pixel.Red} is not 200");
    }

    [Fact]
    public void AClipOutsideTheMergeFollowsItsBaseIntoTheResult()
    {
        var baseLayer = Patch(new SKColor(60, 90, 200), 0, 0, 8, 8, name: "Base");
        var clipped = Patch(new SKColor(220, 180, 40), 0, 0, 8, 8, name: "Clipped");
        clipped.MaskSourceID = baseLayer.ID;
        var above = Patch(new SKColor(40, 200, 120), 0, 0, 8, 8, name: "Above");
        above.MaskSourceID = baseLayer.ID;
        using var document = Doc(8, 8, baseLayer, clipped, above);

        // The clipped layer merges down into the base it is clipped to.
        Assert.NotNull(LayerMerge.Merge(document, [clipped.ID], clipped.ID));
        Assert.Equal(2, document.Layers.Count);
        var merged = document.Layers[0];
        // The layer above it still clips, to the result.
        Assert.Equal(merged.ID, above.MaskSourceID);
    }

    [Fact]
    public void AClipOnAMergedLayerIsCutLooseFromWhatStaysBehind()
    {
        var baseLayer = Patch(new SKColor(60, 90, 200), 0, 0, 8, 8, name: "Base");
        var clipped = Patch(new SKColor(220, 180, 40), 0, 0, 8, 8, name: "Clipped");
        clipped.MaskSourceID = baseLayer.ID;
        var above = Patch(new SKColor(40, 200, 120), 0, 0, 8, 8, name: "Above");
        above.MaskSourceID = clipped.ID;
        using var document = Doc(8, 8, baseLayer, clipped, above);

        var plan = LayerMerge.Plan(document, [above.ID], above.ID);
        Assert.NotNull(plan);
        Assert.Equal(new[] { clipped.ID, above.ID }, plan.Members);

        // The clipped layer's own base is outside the merge, so inside it the layer is simply itself.
        Assert.NotNull(LayerMerge.Merge(document, [above.ID], above.ID));
        Assert.Equal(2, document.Layers.Count);
        Assert.Equal(baseLayer.ID, document.Layers[0].ID);
        Assert.Equal("Clipped", document.Layers[1].Name);
    }

    [Fact]
    public void MergingIsRefusedWhenTheCanvasCannotBeHeldAtOnce()
    {
        var bottom = Patch(SKColors.Blue, 0, 0, 8, 8, name: "Bottom");
        var top = Patch(SKColors.Red, 0, 0, 8, 8, name: "Top");
        using var document = new CanvasDocument(Guid.NewGuid(), 20_000, 20_000);
        document.Layers.AddRange([bottom, top]);

        Assert.Null(LayerMerge.Merge(document, [top.ID], top.ID));
        Assert.Equal(2, document.Layers.Count);
    }

    [Fact]
    public void UndoBringsTheMergedLayersBack()
    {
        var bottom = Patch(SKColors.Blue, 0, 0, 8, 8, name: "Bottom");
        var top = Patch(SKColors.Red, 0, 0, 8, 8, name: "Top");
        using var document = Doc(8, 8, bottom, top);
        var history = new DocumentHistory();

        history.Begin("Merge Down", document, bottom.ID);
        Assert.NotNull(LayerMerge.Merge(document, [top.ID], top.ID));
        history.End(document, top.ID);
        Assert.Single(document.Layers);

        var undone = history.Undo();
        Assert.NotNull(undone);
        document.Adopt(undone.Value.Document!);
        Assert.Equal(2, document.Layers.Count);
        Assert.Equal(bottom.ID, document.Layers[0].ID);
        Assert.Equal(top.ID, document.Layers[1].ID);

        var redone = history.Redo();
        Assert.NotNull(redone);
        document.Adopt(redone.Value.Document!);
        Assert.Single(document.Layers);
        Assert.Equal("Bottom", document.Layers[0].Name);
    }
}
