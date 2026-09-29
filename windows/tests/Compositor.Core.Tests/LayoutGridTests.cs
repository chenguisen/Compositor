using Compositor.Core.Document;

namespace Compositor.Core.Tests;

/// <summary>
/// The layout grid: how far apart its lines are and where they fall on the screen. It is a view setting, so
/// there is nothing to save with the project — this is the geometry the canvas draws from.
/// </summary>
public class LayoutGridTests
{
    [Fact]
    public void TheDefaultsAreTheOnesTheMacBuildOpensWith()
    {
        var grid = new LayoutGrid();
        Assert.Equal(64, grid.Spacing);
        Assert.Equal(8, grid.Subdivisions);
        Assert.Equal(8, grid.Step);
    }

    [Fact]
    public void AmountsOutsideTheirRangeAreBroughtBackIntoIt()
    {
        Assert.Equal(LayoutGrid.LeastSpacing, new LayoutGrid(0).Spacing);
        Assert.Equal(LayoutGrid.MostSpacing, new LayoutGrid(9_000).Spacing);
        Assert.Equal(LayoutGrid.LeastSubdivisions, new LayoutGrid(64, 0).Subdivisions);
        Assert.Equal(LayoutGrid.MostSubdivisions, new LayoutGrid(64, 900).Subdivisions);
        // A square is never split finer than a pixel: four pixels apart splits at most four ways.
        Assert.Equal(4, new LayoutGrid(4, 64).Subdivisions);
        Assert.Equal(1.0, new LayoutGrid(2, 64).Step, 6);
    }

    [Fact]
    public void TheLinesStartAtTheDocumentsOwnOrigin()
    {
        var grid = new LayoutGrid(64, 8);
        // At actual size with the document's corner at the view's corner: a line every eight pixels.
        var lines = grid.Lines(64, 64, 1, 0, 0);
        // A line every eight pixels, the first of them eight in: the document's own edge is a major line.
        Assert.Equal(8, lines.VerticalFine[0], 6);
        Assert.Equal(56, lines.VerticalFine[^1], 6);
        Assert.Equal(0, lines.VerticalMajor[0], 6);
        // The same positions down the picture, and the majors are counted out every sixty-four.
        Assert.Equal(8, lines.HorizontalFine[0], 6);
        Assert.Equal(2, lines.VerticalMajor.Length);
        Assert.Equal([0, 64], lines.VerticalMajor);
    }

    [Fact]
    public void ALineIsMajorEverySoManyPixelsAndNoOthers()
    {
        var grid = new LayoutGrid(64, 8);
        var lines = grid.Lines(200, 200, 1, 0, 0);
        // 0, 64, 128 and 192 across; and the same down.
        Assert.Equal(4, lines.VerticalMajor.Length);
        Assert.Equal(4, lines.HorizontalMajor.Length);
        // The fine lines are the ones between them: 8 apart, so 8 lines to a square less the major one.
        Assert.Equal(22, lines.VerticalFine.Length);
        Assert.DoesNotContain(64, lines.VerticalFine);
    }

    [Fact]
    public void TheGridMovesWithTheViewAndZoomsWithIt()
    {
        var grid = new LayoutGrid(64, 8);
        // The document point 8 sits eight pixels in at actual size with no scroll.
        var plain = grid.Lines(128, 128, 1, 0, 0);
        Assert.Equal(8, plain.VerticalFine[0], 6);
        // Scrolled eight pixels in, that line is at the view's edge, and the next is eight further on.
        var scrolled = grid.Lines(128, 128, 1, 8, 8);
        Assert.Equal(0, scrolled.VerticalFine[0], 6);
        Assert.Equal(8, scrolled.VerticalFine[1], 6);
        // Zoomed to twice the size, eight pixels apart becomes sixteen on the screen.
        var zoomed = grid.Lines(128, 128, 2, 0, 0);
        Assert.Equal(16, zoomed.VerticalFine[0], 6);
    }

    [Fact]
    public void AViewThatIsNotOverTheDocumentDrawsNothing()
    {
        var grid = new LayoutGrid(64, 8);
        Assert.Empty(grid.Lines(0, 0, 1, 0, 0).VerticalFine);
        Assert.Empty(grid.Lines(64, 64, 0, 0, 0).VerticalFine);
    }
}
