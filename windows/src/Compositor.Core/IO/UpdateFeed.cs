using System.Globalization;
using System.Xml.Linq;

namespace Compositor.Core.IO;

/// <summary>
/// A version as the app and its feed write it: three numbers, and whatever else was tagged on — a build
/// number, a beta tag — which is compared as text when the numbers are equal, so 1.3.7-beta sorts before
/// 1.3.7 and 1.3.7+build.2 after it, as the parts of a version are meant to.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string Rest) : IComparable<AppVersion>
{
    /// <summary>The version written in a string, or null when it is not a version at all.</summary>
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim().TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+']);
        var numbers = cut < 0 ? trimmed : trimmed[..cut];
        var rest = cut < 0 ? "" : trimmed[cut..];
        var parts = numbers.Split('.');
        if (parts.Length == 0 || parts.Length > 3) return null;
        Span<int> values = [0, 0, 0];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 || !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }
            values[index] = value;
        }
        return new AppVersion(values[0], values[1], values[2], rest);
    }

    public int CompareTo(AppVersion other)
    {
        var numbers = Major.CompareTo(other.Major);
        if (numbers != 0) return numbers;
        numbers = Minor.CompareTo(other.Minor);
        if (numbers != 0) return numbers;
        numbers = Patch.CompareTo(other.Patch);
        if (numbers != 0) return numbers;
        // 1.3.7-beta sorts before 1.3.7, and 1.3.7+build.2 after it: a version tagged as a pre-release is not
        // the release, whatever letters it carries.
        var ranks = Rank(Rest).CompareTo(Rank(other.Rest));
        return ranks != 0 ? ranks : string.CompareOrdinal(Rest, other.Rest);
    }

    /// <summary>Where a version's tag puts it: before the release, at it, or after it.</summary>
    private static int Rank(string rest) => rest.Length == 0 ? 1 : rest[0] == '-' ? 0 : 2;

    /// <summary>The version as it is written: always three numbers, canonically, with whatever was tagged on.</summary>
    public override string ToString() => $"{Major}.{Minor}.{Patch}{Rest}";
}

/// <summary>One release as the feed lists it.</summary>
public sealed record FeedRelease(
    AppVersion Version,
    string Title,
    string? Page,
    string? Download,
    long Bytes,
    string? Published);

/// <summary>
/// The app's update feed, which is the same Sparkle appcast the Mac build reads
/// (<c>https://raw.githubusercontent.com/robbietilton/Compositor/main/appcast.xml</c>): an RSS channel of
/// releases, the newest of which is what "Check for Updates" offers. Only the reading is here — what to do
/// with a newer release is the window's business.
/// </summary>
public static class UpdateFeed
{
    /// <summary>The feed the Mac build reads, so both builds are told about the same release.</summary>
    public const string Address = "https://raw.githubusercontent.com/robbietilton/Compositor/main/appcast.xml";

    private static readonly XNamespace Sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";

    /// <summary>
    /// The newest release the feed lists, or null when it lists none or cannot be read. A feed that is not
    /// XML, or that has no items, is not an error worth failing over — it is simply nothing to report.
    /// </summary>
    public static FeedRelease? Newest(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument feed;
        try
        {
            feed = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        return Releases(feed).OrderByDescending(release => release.Version).FirstOrDefault();
    }

    /// <summary>Whether a release is worth telling the person about: anything after the version in hand.</summary>
    public static bool IsNewer(FeedRelease release, AppVersion running) => release.Version.CompareTo(running) > 0;

    /// <summary>Every release the feed lists, oldest first, which is how its items read.</summary>
    public static List<FeedRelease> Releases(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        try
        {
            return Releases(XDocument.Parse(xml));
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static List<FeedRelease> Releases(XDocument feed)
    {
        var releases = new List<FeedRelease>();
        foreach (var item in feed.Descendants("item"))
        {
            // The appcast writes the marketing version in shortVersionString and a build number in version;
            // either can be missing from a hand-written feed, so the title is the last resort.
            var text = (string?)item.Element(Sparkle + "shortVersionString")
                ?? (string?)item.Element(Sparkle + "version")
                ?? (string?)item.Element("title");
            if (AppVersion.Parse(text) is not { } version) continue;
            var enclosure = item.Element("enclosure");
            releases.Add(new FeedRelease(
                version,
                (string?)item.Element("title") ?? version.ToString(),
                (string?)item.Element("link"),
                (string?)enclosure?.Attribute("url"),
                (long?)enclosure?.Attribute("length") ?? 0,
                (string?)item.Element("pubDate")));
        }
        return releases;
    }
}
