namespace Compositor.Core.IO;

/// <summary>
/// Watches a project's folder for someone else writing it. A project is a package — a manifest and the images
/// beside it — so what is compared is the whole folder: the name, length and write time of every file in it.
/// The Mac build watches its packages the same way, which is what lets a person work on a project with an
/// editor open beside the app and see the canvas take the change.
/// </summary>
public sealed class ProjectWatch
{
    private readonly string _path;
    private Dictionary<string, (DateTime Written, long Size)> _seen;

    private ProjectWatch(string path)
    {
        _path = path;
        _seen = Fingerprint(path);
    }

    /// <summary>A watch over the project at <paramref name="path"/>, or null when there is no such folder.</summary>
    public static ProjectWatch? For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        return new ProjectWatch(path);
    }

    /// <summary>
    /// Whether anything in the package has been written since this was taken or last asked — and what it holds
    /// now is taken as what it holds, so a change is only ever reported once.
    /// </summary>
    public bool Changed()
    {
        var now = Fingerprint(_path);
        var changed = !Same(now, _seen);
        _seen = now;
        return changed;
    }

    /// <summary>
    /// Takes what the package holds now as what it holds, which is what the app does after saving it itself:
    /// a save is not a change to be told about.
    /// </summary>
    public void Remember() => _seen = Fingerprint(_path);

    private static bool Same(Dictionary<string, (DateTime Written, long Size)> one,
        Dictionary<string, (DateTime Written, long Size)> other)
    {
        if (one.Count != other.Count) return false;
        foreach (var (name, state) in one)
        {
            if (!other.TryGetValue(name, out var was) || was != state) return false;
        }
        return true;
    }

    /// <summary>What is in the folder, by name, with the length and write time of each file.</summary>
    private static Dictionary<string, (DateTime Written, long Size)> Fingerprint(string path)
    {
        var files = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                files[Path.GetRelativePath(path, file)] = (info.LastWriteTimeUtc, info.Length);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is not a folder that has changed: nothing is claimed about it.
            return files;
        }
        return files;
    }
}
