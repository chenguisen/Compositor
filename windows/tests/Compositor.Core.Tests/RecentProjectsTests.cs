using Compositor.Core.IO;

namespace Compositor.Core.Tests;

/// <summary>
/// File ▸ Open Recent: which projects are offered, in what order, and that a list which cannot be read is
/// not worth failing over. Windows has no system list, so the file it keeps is checked here.
/// </summary>
public class RecentProjectsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorRecent-" + Guid.NewGuid().ToString("N"));
    private readonly RecentProjects _recent;

    public RecentProjectsTests()
    {
        Directory.CreateDirectory(_root);
        _recent = new RecentProjects(Path.Combine(_root, "recent.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A project folder that exists, since only projects that are still there are offered.</summary>
    private string Project(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AListThatIsNotThereYetIsEmpty()
    {
        Assert.Empty(_recent.All());
    }

    [Fact]
    public void TheMostRecentComesFirstAndEachIsOfferedOnce()
    {
        var one = Project("one.comp");
        var two = Project("two.comp");
        Assert.Equal([one], _recent.Note(one));
        Assert.Equal([two, one], _recent.Note(two));
        // Opening one again moves it to the front rather than listing it twice.
        Assert.Equal([one, two], _recent.Note(one));
        Assert.Equal([one, two], _recent.All());
    }

    [Fact]
    public void AProjectThatHasSinceGoneIsLeftOut()
    {
        var here = Project("here.comp");
        var gone = Project("gone.comp");
        _recent.Note(here);
        _recent.Note(gone);
        Assert.Equal([gone, here], _recent.All());

        Directory.Delete(gone, recursive: true);
        Assert.Equal([here], _recent.All());
        // And it does not come back when something else is noted.
        var other = Project("other.comp");
        Assert.Equal([other, here], _recent.Note(other));
    }

    [Fact]
    public void OnlySoManyAreKept()
    {
        var made = new List<string>();
        for (var index = 0; index < RecentProjects.Most + 4; index++)
        {
            made.Add(Project($"p{index}.comp"));
        }
        foreach (var path in made) _recent.Note(path);
        var kept = _recent.All();
        Assert.Equal(RecentProjects.Most, kept.Count);
        // The newest are the ones that stayed: the last ten of the fourteen noted, newest first.
        Assert.Equal(made[^1], kept[0]);
        Assert.Equal(made[4], kept[^1]);
        Assert.DoesNotContain(made[3], kept);
    }

    [Fact]
    public void TheListSurvivesBeingReadAgain()
    {
        var path = Project("kept.comp");
        _recent.Note(path);
        var again = new RecentProjects(_recent.Path);
        Assert.Equal([path], again.All());
    }

    [Fact]
    public void ForgettingTakesThemAllAway()
    {
        _recent.Note(Project("one.comp"));
        Assert.Single(_recent.All());
        Assert.Empty(_recent.Forgot());
        Assert.Empty(new RecentProjects(_recent.Path).All());
    }

    [Fact]
    public void AListThatCannotBeReadIsNotWorthFailingOver()
    {
        File.WriteAllText(_recent.Path, "this is not json at all");
        Assert.Empty(_recent.All());
        // And it is replaced the next time a project is noted.
        var path = Project("fresh.comp");
        Assert.Equal([path], _recent.Note(path));
    }

    [Fact]
    public void AFileNameFindsAProjectTooSinceAProjectIsAFolderHere()
    {
        // Windows has no file-like package, so a project is a folder; a file is accepted as well in case one
        // is ever opened that way, and either is offered while it is there.
        var file = Path.Combine(_root, "single.comp");
        File.WriteAllText(file, "{}");
        _recent.Note(file);
        Assert.Equal([file], _recent.All());
    }
}
