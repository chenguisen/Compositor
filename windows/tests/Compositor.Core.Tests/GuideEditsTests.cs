using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The alignment guides: the lines put on the canvas to line things up against. What they are drawn as is the
/// window's business; what they are and where they run is this.
/// </summary>
public class GuideEditsTests
{
    private static CanvasDocument Document(int width = 100, int height = 80) =>
        new(Guid.NewGuid(), width, height);

    [Fact]
    public void AGuideIsAddedWhereItIsAskedFor()
    {
        using var document = Document();
        var made = GuideEdits.Add(document, GuideAxis.Horizontal, 30);
        Assert.NotNull(made);
        var guide = Assert.Single(document.Guides);
        Assert.Equal(made, guide.ID);
        Assert.Equal(GuideAxis.Horizontal, guide.Axis);
        Assert.Equal(30, guide.Position);
    }

    [Fact]
    public void AGuideMaySitOutsideTheCanvasInThePasteboard()
    {
        using var document = Document();
        // A guide out in the pasteboard is where something is being lined up to, not a mistake.
        Assert.NotNull(GuideEdits.Add(document, GuideAxis.Vertical, -40));
        Assert.NotNull(GuideEdits.Add(document, GuideAxis.Vertical, 250));
        Assert.Equal(2, document.Guides.Count);
        // But not somewhere the store would refuse to read back.
        Assert.Null(GuideEdits.Add(document, GuideAxis.Vertical, 2_000_000));
        Assert.Null(GuideEdits.Add(document, GuideAxis.Vertical, double.NaN));
        Assert.Equal(2, document.Guides.Count);
    }

    [Fact]
    public void AGuideCanBeMovedAndTakenAway()
    {
        using var document = Document();
        var made = GuideEdits.Add(document, GuideAxis.Vertical, 10)!.Value;
        Assert.True(GuideEdits.Move(document, made, 44));
        Assert.Equal(44, document.Guides[0].Position);
        // The same place again is not a change, and somewhere it may not sit is refused.
        Assert.False(GuideEdits.Move(document, made, 44));
        Assert.False(GuideEdits.Move(document, made, double.PositiveInfinity));
        Assert.Equal(44, document.Guides[0].Position);
        Assert.False(GuideEdits.Move(document, Guid.NewGuid(), 5));

        Assert.True(GuideEdits.Remove(document, made));
        Assert.Empty(document.Guides);
        Assert.False(GuideEdits.Remove(document, made));
    }

    [Fact]
    public void ClearingTakesThemAllAndSaysHowManyThereWere()
    {
        using var document = Document();
        GuideEdits.Add(document, GuideAxis.Vertical, 10);
        GuideEdits.Add(document, GuideAxis.Horizontal, 20);
        Assert.Equal(2, GuideEdits.Clear(document));
        Assert.Empty(document.Guides);
        Assert.Equal(0, GuideEdits.Clear(document));
    }

    [Fact]
    public void AClickFindsTheNearestGuideOnItsOwnAxis()
    {
        using var document = Document();
        var across = GuideEdits.Add(document, GuideAxis.Horizontal, 30)!.Value;
        var down = GuideEdits.Add(document, GuideAxis.Vertical, 31)!.Value;
        // A vertical click is not answered by the horizontal guide that happens to be near it.
        Assert.Equal(down, GuideEdits.At(document, GuideAxis.Vertical, 30.5, 2));
        Assert.Equal(across, GuideEdits.At(document, GuideAxis.Horizontal, 30.5, 2));
        // Nothing near enough is nothing.
        Assert.Null(GuideEdits.At(document, GuideAxis.Horizontal, 60, 2));
        // Of two on the same axis, the nearer one. (With them exactly equally near it is either, so the test
        // stands a little to one side.)
        var further = GuideEdits.Add(document, GuideAxis.Horizontal, 33)!.Value;
        Assert.Equal(across, GuideEdits.At(document, GuideAxis.Horizontal, 31, 5));
        Assert.Equal(further, GuideEdits.At(document, GuideAxis.Horizontal, 32, 5));
    }

    [Fact]
    public void ADocumentHoldsNoMoreGuidesThanTheStoreWouldRead()
    {
        using var document = Document();
        for (var index = 0; index < GuideEdits.MaxGuides; index++)
        {
            Assert.NotNull(GuideEdits.Add(document, GuideAxis.Vertical, index));
        }
        Assert.Null(GuideEdits.Add(document, GuideAxis.Vertical, 5));
        Assert.Equal(GuideEdits.MaxGuides, document.Guides.Count);
    }

    [Fact]
    public void AHorizontalGuideRunsAcrossTheCanvasWhereItSits()
    {
        using var document = Document(100, 80);
        var id = GuideEdits.Add(document, GuideAxis.Horizontal, 30)!.Value;
        var guide = document.Guides.First(entry => entry.ID == id);
        // Drawn at actual size with the canvas' corner at the view's corner.
        var (x1, y1, x2, y2) = GuideEdits.ScreenLine(guide, 100, 80, 1, 0, 0);
        Assert.Equal(0, x1);
        Assert.Equal(30, y1);
        Assert.Equal(100, x2);
        Assert.Equal(30, y2);
    }

    [Fact]
    public void AVerticalGuideRunsDownTheCanvasAndBothFollowTheView()
    {
        using var document = Document(100, 80);
        var id = GuideEdits.Add(document, GuideAxis.Vertical, 40)!.Value;
        var guide = document.Guides.First(entry => entry.ID == id);
        // Zoomed to twice the size with the document point (10, 5) in the view's top-left corner.
        var (x1, y1, x2, y2) = GuideEdits.ScreenLine(guide, 100, 80, 2, 10, 5);
        Assert.Equal(60, x1);
        Assert.Equal(60, x2);
        Assert.Equal(-10, y1);
        Assert.Equal(150, y2);

        // A horizontal guide moves with the view down the screen, and still spans it across.
        var across = GuideEdits.Add(document, GuideAxis.Horizontal, 45)!.Value;
        var line = GuideEdits.ScreenLine(document.Guides.First(entry => entry.ID == across), 100, 80, 0.5, 10, 5);
        Assert.Equal(-5, line.X1);
        Assert.Equal(45, line.X2);
        Assert.Equal(20, line.Y1);
        Assert.Equal(20, line.Y2);
    }

    [Fact]
    public void GuidesMoveWithTheLayersWhenTheCanvasChanges()
    {
        using var document = Document(100, 80);
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 100, 80), "Layer 1"));
        GuideEdits.Add(document, GuideAxis.Horizontal, 30);
        GuideEdits.Add(document, GuideAxis.Vertical, 40);
        // Cropping from (10,5) moves everything with it, guides included.
        Assert.True(CanvasEdits.Crop(document, SkiaSharp.SKRectI.Create(10, 5, 50, 40)));
        Assert.Equal(25, document.Guides.First(entry => entry.Axis == GuideAxis.Horizontal).Position);
        Assert.Equal(30, document.Guides.First(entry => entry.Axis == GuideAxis.Vertical).Position);
    }
}
