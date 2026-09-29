using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Watching a project's folder for someone else writing it: a change is noticed once, and a save by the app
/// itself is not a change. This is what lets a project be worked on with an editor open beside the app.
/// </summary>
public class ProjectWatchTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "compositor-watch-" + Guid.NewGuid().ToString("N"));

    public ProjectWatchTests() => Directory.CreateDirectory(_folder);

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

    /// <summary>A small project written to the folder, which is the thing being watched.</summary>
    private string Project(int side = 8)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(side, side));
        bitmap.Erase(new SKColor(60, 90, 140));
        var document = new CanvasDocument(Guid.NewGuid(), side, side);
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Only"),
            new LayerTransform(0, 0, side, side), "Only"));
        var path = Path.Combine(_folder, "watched.comp");
        using (document) ProjectStore.Save(ProjectSnapshot.FromDocument(document), path);
        return path;
    }

    [Fact]
    public void AProjectThatIsNotThereIsNotWatched()
    {
        Assert.Null(ProjectWatch.For(Path.Combine(_folder, "nothing.comp")));
        Assert.Null(ProjectWatch.For(null));
        Assert.Null(ProjectWatch.For(""));
    }

    [Fact]
    public void NothingChangedUntilSomethingIsWritten()
    {
        var watch = ProjectWatch.For(Project());
        Assert.NotNull(watch);
        Assert.False(watch.Changed());
        Assert.False(watch.Changed());
    }

    [Fact]
    public void AnImageWrittenBySomeoneElseIsAChangeAndIsOnlyReportedOnce()
    {
        var path = Project();
        var watch = ProjectWatch.For(path);
        Assert.NotNull(watch);
        Assert.False(watch.Changed());

        // An editor outside the app writes one of the package's images.
        var image = Directory.EnumerateFiles(Path.Combine(path, "images"), "*.png").First();
        File.WriteAllBytes(image, [.. File.ReadAllBytes(image), 0]);

        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
    }

    [Fact]
    public void AFileArrivingOrLeavingIsAChange()
    {
        var path = Project();
        var watch = ProjectWatch.For(path);
        Assert.NotNull(watch);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "a note beside the manifest");
        Assert.True(watch.Changed());
        Assert.False(watch.Changed());
        File.Delete(Path.Combine(path, "notes.txt"));
        Assert.True(watch.Changed());
    }

    [Fact]
    public void ASaveByTheAppItselfIsNotAChangeToTellAbout()
    {
        var path = Project();
        var watch = ProjectWatch.For(path);
        Assert.NotNull(watch);
        // The app saves the project, then says so: what is on disk now is what it holds, so nothing is reported.
        using (var document = ProjectStore.Load(path).ToDocument())
        {
            document.Layers[0].Name = "Renamed";
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), path);
        }
        Assert.True(watch.Changed());
        watch.Remember();
        Assert.False(watch.Changed());
    }
}
