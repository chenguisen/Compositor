using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The selection's Anti-alias switch, which is the Mac build's own tick in the lasso's controls: with it off a
/// marquee's edge is hard, so the coverage at the boundary is all or nothing, and with it on the boundary is
/// part of the way across — which is exactly what a fill, a filter or a clear reads to decide how much of the
/// change each pixel takes.
/// </summary>
public class SelectionAntialiasTests
{
    private static CanvasDocument Document()
    {
        var document = LayerPlacement.NewDocument(40, 30);
        return document ?? throw new InvalidOperationException("no document");
    }

    /// <summary>
    /// The coverage across the left edge of a rectangular selection, two pixels either side of it. Coverage
    /// comes back as a gray mask — white where the selection holds — so the value is the colour, not the alpha,
    /// which is opaque everywhere.
    /// </summary>
    private static (int Outside, int Edge, int Inside) Across(CanvasDocument document, SKRectI box)
    {
        using var coverage = document.Selection.Coverage(SKRectI.Create(0, 0, document.Width, document.Height));
        Assert.NotNull(coverage);
        var row = box.Top + box.Height / 2;
        return (coverage.GetPixel(box.Left - 2, row).Red,
            coverage.GetPixel(box.Left, row).Red,
            coverage.GetPixel(box.Left + 2, row).Red);
    }

    [Fact]
    public void AnOutlineWithoutAntialiasingHasAHardEdge()
    {
        using var document = Document();
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(10, 10, 20, 10), antialiased: false));
        var (outside, edge, inside) = Across(document, SKRectI.Create(10, 10, 20, 10));
        Assert.Equal(0, outside);
        Assert.Equal(255, edge);
        Assert.Equal(255, inside);
        Assert.False(document.Selection.Antialiased);
    }

    [Fact]
    public void AnOutlineWithAntialiasingIsSoftWhereItTurns()
    {
        // A rectangle's straight edges land on pixel boundaries and come out hard either way — it is a curve
        // that shows the difference, so this is the ellipse.
        using var document = Document();
        Assert.True(SelectionEdits.SelectEllipse(document, SKRectI.Create(10, 6, 20, 18)));
        using var soft = document.Selection.Coverage(SKRectI.Create(0, 0, document.Width, document.Height));
        Assert.NotNull(soft);

        using var hard = Document();
        Assert.True(SelectionEdits.SelectEllipse(hard, SKRectI.Create(10, 6, 20, 18), antialiased: false));
        using var crisp = hard.Selection.Coverage(SKRectI.Create(0, 0, hard.Width, hard.Height));
        Assert.NotNull(crisp);

        var partial = 0;
        var binary = 0;
        for (var y = 0; y < document.Height; y++)
        {
            for (var x = 0; x < document.Width; x++)
            {
                var coverage = soft.GetPixel(x, y).Red;
                if (coverage is > 0 and < 255) partial++;
                // The claim is not which side of the line a half-covered pixel lands on — that is the raster's
                // business — but that without antialiasing there is nothing half-covered at all.
                if (crisp.GetPixel(x, y).Red is 0 or 255) binary++;
            }
        }
        Assert.True(partial > 20, $"only {partial} pixels came out part-way with antialiasing on");
        Assert.Equal(document.Width * document.Height, binary);
    }

    [Fact]
    public void TheWandTakesTheSameSwitch()
    {
        using var document = Document();
        using var sample = SelectionEdits.Sample(document, null);
        Assert.NotNull(sample);
        // A blank canvas is one colour from edge to edge, so the wand takes the whole of it.
        Assert.True(SelectionEdits.SelectWand(document, sample, 5, 5, new WandOptions(), SelectionMode.Replace,
            antialiased: false));
        Assert.False(document.Selection.Antialiased);
        Assert.NotNull(document.Selection.Path);

        Assert.True(SelectionEdits.SelectWand(document, sample, 5, 5, new WandOptions(), SelectionMode.Replace));
        Assert.True(document.Selection.Antialiased);
    }

    [Fact]
    public void AddingToASelectionKeepsTheEdgeItAlreadyHad()
    {
        // What is being changed by an add is the shape, not how its edge is drawn — the Mac build's own rule.
        using var document = Document();
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(4, 4, 10, 10), antialiased: false));
        Assert.True(SelectionEdits.Apply(document, SelectionEdits.Shape(SKRectI.Create(20, 4, 10, 10), ellipse: false),
            SelectionMode.Add, antialiased: true));
        Assert.False(document.Selection.Antialiased);
    }
}
