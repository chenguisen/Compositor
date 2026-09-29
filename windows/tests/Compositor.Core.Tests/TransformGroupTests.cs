using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Transforming several layers together: which layers a group is, the box around them, and how each one is
/// carried along as that box is dragged, scaled or turned.
/// </summary>
public class TransformGroupTests
{
    private static ImageLayer Patch(double x, double y, int width, int height, string name,
        double rotation = 0)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new LayerTransform(x, y, width, height, rotation), name);
    }

    private static CanvasDocument Doc(params ImageLayer[] layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), 400, 400);
        document.Layers.AddRange(layers);
        return document;
    }

    [Fact]
    public void AGroupIsTheSelectedLayersAndWhatTheirFoldersHold()
    {
        var loose = Patch(0, 0, 10, 10, "Loose");
        var folder = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 10, 10), "Folder")
        {
            IsGroup = true,
        };
        var inside = Patch(20, 20, 10, 10, "Inside");
        inside.ParentID = folder.ID;
        var hidden = Patch(30, 30, 10, 10, "Hidden");
        hidden.IsVisible = false;
        using var document = Doc(loose, folder, inside, hidden);

        // A selected folder brings what is inside it; a hidden layer is not moved.
        Assert.Equal(new[] { "Loose", "Inside" },
            TransformEdits.GroupMembers(document, [loose.ID, folder.ID]).Select(layer => layer.Name));
        // A folder on its own is not a layer that holds pixels, so it brings its contents and nothing else.
        Assert.DoesNotContain(TransformEdits.GroupMembers(document, [folder.ID]), layer => layer.IsGroup);
        Assert.Single(TransformEdits.GroupMembers(document, [folder.ID]));
    }

    [Fact]
    public void AGroupBoxIsTheBoxAroundTheMembers()
    {
        var one = Patch(10, 20, 40, 30, "One");
        var two = Patch(100, 200, 50, 60, "Two");
        using var document = Doc(one, two);

        var box = TransformEdits.GroupBox(document, [one.ID, two.ID]);
        Assert.NotNull(box);
        var group = box.Value;
        Assert.Equal(10, group.X);
        Assert.Equal(20, group.Y);
        Assert.Equal(140, group.Width);
        Assert.Equal(240, group.Height);
    }

    [Fact]
    public void AGroupBoxGoesRoundATurnedLayer()
    {
        // A 100 by 50 layer turned a quarter turn covers a 50 by 100 upright box.
        var turned = Patch(100, 100, 100, 50, "Turned", rotation: 90);
        using var document = Doc(turned);
        var box = TransformEdits.GroupBox(document, [turned.ID]);
        Assert.NotNull(box);
        Assert.Equal(50, box.Value.Width, 3);
        Assert.Equal(100, box.Value.Height, 3);
    }

    [Fact]
    public void AGroupOfNothingHasNoBox()
    {
        var loose = Patch(0, 0, 10, 10, "Loose");
        using var document = Doc(loose);
        Assert.Null(TransformEdits.GroupBox(document, [Guid.NewGuid()]));
    }

    [Fact]
    public void MovingTheBoxMovesEveryLayerByTheSameAmount()
    {
        var one = Patch(10, 20, 40, 30, "One");
        var two = Patch(100, 200, 50, 60, "Two");
        using var document = Doc(one, two);
        var from = TransformEdits.GroupBox(document, [one.ID, two.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform, [two.ID] = two.Transform };

        var to = TransformEdits.Move(from, 25, -5);
        Assert.True(TransformEdits.Carry(document, originals, from, to));
        Assert.Equal(35, one.Transform.X);
        Assert.Equal(15, one.Transform.Y);
        Assert.Equal(125, two.Transform.X);
        Assert.Equal(195, two.Transform.Y);
        // Nothing else about them changed.
        Assert.Equal(40, one.Transform.Width);
        Assert.Equal(originals[one.ID].Rotation, one.Transform.Rotation);
    }

    [Fact]
    public void ScalingTheBoxScalesTheLayersAboutTheSamePlace()
    {
        // Two 40 by 40 layers, their box being 200 wide from 0 to 200.
        var one = Patch(0, 0, 40, 40, "One");
        var two = Patch(160, 0, 40, 40, "Two");
        using var document = Doc(one, two);
        var from = TransformEdits.GroupBox(document, [one.ID, two.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform, [two.ID] = two.Transform };

        // The box doubled from its top left: its width goes from 200 to 400.
        var to = TransformEdits.Resize(from, TransformHandle.BottomRight,
            TransformEdits.Position(from, TransformHandle.BottomRight), new SKPoint(400, 40),
            lockRatio: false, fromCentre: false);
        Assert.True(TransformEdits.Carry(document, originals, from, to));

        // Each layer doubled, and the one at the box's left edge stayed there.
        Assert.Equal(80, one.Transform.Width, 1);
        Assert.Equal(0, one.Transform.X, 1);
        // The one on the right moved out with the box: its left edge was at 160 of 200, so it is at 320 now.
        Assert.Equal(320, two.Transform.X, 1);
        Assert.Equal(80, two.Transform.Width, 1);
    }

    [Fact]
    public void TurningTheBoxTurnsTheLayersWithIt()
    {
        var one = Patch(0, 0, 40, 40, "One");
        var two = Patch(160, 0, 40, 40, "Two");
        using var document = Doc(one, two);
        var from = TransformEdits.GroupBox(document, [one.ID, two.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform, [two.ID] = two.Transform };

        // A quarter turn about the box's middle.
        var to = TransformEdits.Rotate(from, TransformEdits.Position(from, TransformHandle.Right),
            TransformEdits.Position(from, TransformHandle.Bottom), steps: false);
        Assert.True(TransformEdits.Carry(document, originals, from, to));

        Assert.Equal(90, one.Transform.Rotation, 3);
        Assert.Equal(90, two.Transform.Rotation, 3);
        // Each layer keeps its place in the box, so the pair keeps its spacing and its middle: the line
        // between them, which was across the box, is now down it, and both meet at the box's own middle.
        Assert.Equal(160, Math.Abs(two.Transform.CenterY - one.Transform.CenterY), 3);
        Assert.Equal(0, Math.Abs(two.Transform.CenterX - one.Transform.CenterX), 3);
        Assert.Equal(100, (one.Transform.CenterX + two.Transform.CenterX) / 2, 3);
        Assert.Equal(20, (one.Transform.CenterY + two.Transform.CenterY) / 2, 3);
        // And each is the same size as it was, only turned.
        Assert.Equal(40, one.Transform.Width, 3);
        Assert.Equal(40, one.Transform.Height, 3);
    }

    [Fact]
    public void AnUnmovedBoxCarriesNothing()
    {
        var one = Patch(10, 20, 40, 30, "One");
        using var document = Doc(one);
        var box = TransformEdits.GroupBox(document, [one.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform };
        Assert.False(TransformEdits.Carry(document, originals, box, box));
        Assert.Equal(10, one.Transform.X);
    }

    [Fact]
    public void AGroupCarriesEachLayerFromWhereItWasWhenTheDragBegan()
    {
        var one = Patch(0, 0, 40, 40, "One");
        using var document = Doc(one);
        var from = TransformEdits.GroupBox(document, [one.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform };

        // Drag the box to the right in two steps: the second lands where the first did, because every step is
        // measured from the box the drag began with.
        TransformEdits.Carry(document, originals, from, TransformEdits.Move(from, 30, 0));
        Assert.Equal(30, one.Transform.X);
        TransformEdits.Carry(document, originals, from, TransformEdits.Move(from, 50, 0));
        Assert.Equal(50, one.Transform.X);
    }

    [Fact]
    public void AGroupLayerKeepsItsOwnTurnAndFlip()
    {
        var one = Patch(0, 0, 40, 40, "One", rotation: 20);
        one.Transform = one.Transform with { FlipX = true };
        var two = Patch(200, 0, 40, 40, "Two");
        using var document = Doc(one, two);
        var from = TransformEdits.GroupBox(document, [one.ID, two.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform, [two.ID] = two.Transform };

        // A plain move leaves the turned layer turned and flipped, and moves it with the box.
        var to = TransformEdits.Move(from, 10, 10);
        Assert.True(TransformEdits.Carry(document, originals, from, to));
        Assert.Equal(20, one.Transform.Rotation);
        Assert.True(one.Transform.FlipX);
        Assert.Equal(10, one.Transform.X, 3);
    }

    [Fact]
    public void CarryingOneLayerLandsItExactlyWhereTheDragSaid()
    {
        var one = Patch(20, 30, 40, 20, "One");
        using var document = Doc(one);
        var box = TransformEdits.GroupBox(document, [one.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [one.ID] = one.Transform };

        // A box of one layer is that layer's own transform, so the group path and the single-layer path have
        // to agree: moving, scaling and turning all land the layer exactly on the box the drag worked out.
        foreach (var to in new[]
                 {
                     TransformEdits.Move(box, 12, -7),
                     TransformEdits.Resize(box, TransformHandle.BottomRight,
                         TransformEdits.Position(box, TransformHandle.BottomRight), new SKPoint(80, 70), false, false),
                     TransformEdits.Rotate(box, TransformEdits.Position(box, TransformHandle.Right),
                         TransformEdits.Position(box, TransformHandle.Bottom), false),
                 })
        {
            TransformEdits.Carry(document, originals, box, to);
            Assert.Equal(to.X, one.Transform.X, 3);
            Assert.Equal(to.Y, one.Transform.Y, 3);
            Assert.Equal(to.Width, one.Transform.Width, 3);
            Assert.Equal(to.Height, one.Transform.Height, 3);
            Assert.Equal(to.Rotation, one.Transform.Rotation, 3);
        }
    }
}
