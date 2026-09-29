using Compositor.Core.IO;

namespace Compositor.Core.Tests;

/// <summary>
/// The update feed: the appcast the Mac build reads, parsed into the releases it lists, and the version
/// arithmetic that decides whether there is anything to tell the person about.
/// </summary>
public class UpdateFeedTests
{
    /// <summary>The feed as it is published, verbatim, so the format is pinned by the real thing.</summary>
    private const string Published = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
          <channel>
            <title>Compositor</title>
            <item>
              <title>Version 1.3.7</title>
              <pubDate>Sun, 27 Sep 2026 23:14:02 +0000</pubDate>
              <sparkle:version>34</sparkle:version>
              <sparkle:shortVersionString>1.3.7</sparkle:shortVersionString>
              <sparkle:minimumSystemVersion>26.0</sparkle:minimumSystemVersion>
              <link>https://github.com/robbietilton/Compositor/releases/tag/v1.3.7</link>
              <enclosure url="https://github.com/robbietilton/Compositor/releases/download/v1.3.7/Compositor.dmg" sparkle:edSignature="OQ5U18KWacK4oaZXgUDhdhxkijx7zBy4zkOUSfyFod9tc8OSaGoMpXq2BWPxaURxOQsAY75udMvnn4aI2CtdBQ==" length="6595198" type="application/octet-stream"/>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void ThePublishedFeedReadsAsTheReleaseItLists()
    {
        var newest = UpdateFeed.Newest(Published);
        Assert.NotNull(newest);
        Assert.Equal("1.3.7", newest.Version.ToString());
        Assert.Equal("Version 1.3.7", newest.Title);
        Assert.Equal("https://github.com/robbietilton/Compositor/releases/tag/v1.3.7", newest.Page);
        Assert.Equal("https://github.com/robbietilton/Compositor/releases/download/v1.3.7/Compositor.dmg", newest.Download);
        Assert.Equal(6_595_198, newest.Bytes);
        Assert.Equal("Sun, 27 Sep 2026 23:14:02 +0000", newest.Published);
    }

    [Fact]
    public void AReleaseAfterTheOneInHandIsWorthTellingAboutAndOneBeforeIsNot()
    {
        var release = UpdateFeed.Newest(Published);
        Assert.NotNull(release);
        Assert.False(UpdateFeed.IsNewer(release, AppVersion.Parse("1.3.7")!.Value));
        Assert.False(UpdateFeed.IsNewer(release, AppVersion.Parse("1.4")!.Value));
        Assert.True(UpdateFeed.IsNewer(release, AppVersion.Parse("1.3.6")!.Value));
        Assert.True(UpdateFeed.IsNewer(release, AppVersion.Parse("1.2.99")!.Value));
        Assert.True(UpdateFeed.IsNewer(release, AppVersion.Parse("1.3.7-beta")!.Value));
    }

    [Fact]
    public void TheNewestOfSeveralReleasesIsTheOneOffered()
    {
        // A feed keeps its older releases below the newest: what is offered is the one worth having, not the
        // one that happens to be first.
        var feed = """
            <rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
              <channel>
                <item><title>Version 2.0</title><sparkle:shortVersionString>2.0</sparkle:shortVersionString></item>
                <item><title>Version 2.10</title><sparkle:shortVersionString>2.10</sparkle:shortVersionString></item>
                <item><title>Version 2.9</title><sparkle:shortVersionString>2.9</sparkle:shortVersionString></item>
              </channel>
            </rss>
            """;
        // A version is written canonically as three numbers, so "2.10" in the feed reads back as 2.10.0.
        Assert.Equal("2.10.0", UpdateFeed.Newest(feed)!.Version.ToString());
        Assert.Equal(3, UpdateFeed.Releases(feed).Count);
    }

    [Fact]
    public void AVersionIsThreeNumbersWithWhateverWasTaggedOn()
    {
        Assert.Equal(new AppVersion(1, 3, 7, ""), AppVersion.Parse("1.3.7"));
        Assert.Equal(new AppVersion(1, 3, 7, ""), AppVersion.Parse("v1.3.7"));
        Assert.Equal(new AppVersion(2, 0, 0, ""), AppVersion.Parse("2"));
        Assert.Equal(new AppVersion(2, 1, 0, ""), AppVersion.Parse("2.1"));
        Assert.Equal(new AppVersion(1, 4, 0, "-beta.2"), AppVersion.Parse("1.4.0-beta.2"));
        Assert.Null(AppVersion.Parse(""));
        Assert.Null(AppVersion.Parse("newest"));
        Assert.Null(AppVersion.Parse("1.2.3.4"));
        Assert.Null(AppVersion.Parse("1.x"));
    }

    [Fact]
    public void AFeedThatCannotBeReadIsNothingToReportRatherThanAFailure()
    {
        Assert.Null(UpdateFeed.Newest(null));
        Assert.Null(UpdateFeed.Newest(""));
        Assert.Null(UpdateFeed.Newest("this is not xml at all"));
        Assert.Null(UpdateFeed.Newest("<rss><channel><title>Compositor</title></channel></rss>"));
        Assert.Empty(UpdateFeed.Releases("{ unfinished"));
        // A release with no version anywhere is skipped rather than guessed at.
        Assert.Null(UpdateFeed.Newest("<rss><channel><item><title>A release</title></item></channel></rss>"));
    }
}
