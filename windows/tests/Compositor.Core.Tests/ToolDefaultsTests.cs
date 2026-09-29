using Compositor.Core.Document;
using Compositor.Core.IO;

namespace Compositor.Core.Tests;

/// <summary>
/// The view's switches kept from one launch to the next: what is written down, and what is made of a file that
/// is missing, unreadable or has been got at by hand.
/// </summary>
public class ToolDefaultsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "compositor-tools-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "tools.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SwitchesComeBackAsTheyWereLeft()
    {
        var kept = new ToolDefaults
        {
            ShowGrid = true,
            GridSpacing = 96,
            GridSubdivisions = 4,
            SnapTo = SnapTo.Canvas | SnapTo.Guides,
        };
        kept.Save(Path_);
        var read = ToolDefaults.Load(Path_);
        Assert.True(read.ShowGrid);
        Assert.Equal(96, read.GridSpacing);
        Assert.Equal(4, read.GridSubdivisions);
        Assert.Equal(SnapTo.Canvas | SnapTo.Guides, read.SnapTo);
        Assert.Equal(new LayoutGrid(96, 4), read.Grid());
    }

    [Fact]
    public void WithNothingWrittenDownTheViewOpensAsItAlwaysDid()
    {
        var read = ToolDefaults.Load(Path_);
        Assert.False(read.ShowGrid);
        Assert.Equal(new LayoutGrid(), read.Grid());
        Assert.Equal(SnapTo.All, read.SnapTo);
    }

    [Fact]
    public void AFileThatCannotBeReadIsNotWorthFailingToStartOver()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, "{ this is not json at all");
        var read = ToolDefaults.Load(Path_);
        Assert.False(read.ShowGrid);
        Assert.Equal(SnapTo.All, read.SnapTo);
    }

    [Fact]
    public void AHandEditedFileIsBroughtBackIntoRangeRatherThanFollowed()
    {
        Directory.CreateDirectory(_folder);
        // A spacing no grid allows, and a snap flag that is not one of the four.
        File.WriteAllText(Path_, """{"ShowGrid":true,"GridSpacing":0,"GridSubdivisions":900,"SnapTo":128}""");
        var read = ToolDefaults.Load(Path_);
        Assert.True(read.ShowGrid);
        // The grid's own limits are what the spacing comes back inside, so the grid the app uses is a real one.
        Assert.Equal(LayoutGrid.LeastSpacing, read.GridSpacing);
        Assert.True(read.GridSubdivisions <= LayoutGrid.MostSubdivisions, $"subdivisions {read.GridSubdivisions}");
        Assert.Equal(LayoutGrid.LeastSpacing, read.Grid().Spacing);
        // Everything outside the four flags is dropped, so nothing unknown is ever followed.
        Assert.Equal(SnapTo.None, read.SnapTo);
    }
}
