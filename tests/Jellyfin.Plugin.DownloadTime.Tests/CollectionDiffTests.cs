// Edge-case inventory:
// - unreleased member (null or future date) never missing
// - released but inside buffer -> not yet; exactly at boundary -> not; strictly past -> missing
// - owned member (TMDB id in set) never missing regardless of edition
// - one owned movie, all other members missing -> all flagged
// - buffer 0 -> missing right after release date passes
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class CollectionDiffTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static CollectionCatalog JohnWick(params RemoteMovie[] movies)
        => new(404609, "John Wick Collection", movies);

    [Fact]
    public void ReleasedPastBuffer_NotOwned_IsMissing()
    {
        var cat = JohnWick(
            new RemoteMovie(245891, "John Wick", AirTime.FromDate(2014, 10, 24)),
            new RemoteMovie(324552, "John Wick: Chapter 2", AirTime.FromDate(2017, 2, 10)));
        var missing = CollectionDiff.MissingMovies(new HashSet<int> { 245891 }, cat, Now, 90);
        var m = Assert.Single(missing);
        Assert.Equal(324552, m.TmdbId);
    }

    [Fact]
    public void UnreleasedOrFuture_NeverMissing()
    {
        var cat = JohnWick(
            new RemoteMovie(1, "Announced", null),
            new RemoteMovie(2, "ComingSoon", Now.AddDays(30)));
        Assert.Empty(CollectionDiff.MissingMovies(new HashSet<int>(), cat, Now, 90));
    }

    [Fact]
    public void BufferBoundary_ExactlyAtBuffer_NotMissing_PastBuffer_Missing()
    {
        var releasedExactly90DaysAgo = Now.AddDays(-90);
        var cat = JohnWick(new RemoteMovie(3, "Edge", releasedExactly90DaysAgo));
        Assert.Empty(CollectionDiff.MissingMovies(new HashSet<int>(), cat, Now, 90));
        var cat2 = JohnWick(new RemoteMovie(3, "Edge", releasedExactly90DaysAgo.AddSeconds(-1)));
        Assert.Single(CollectionDiff.MissingMovies(new HashSet<int>(), cat2, Now, 90));
    }

    [Fact]
    public void OneOwned_AllOthersMissing()
    {
        var cat = JohnWick(
            new RemoteMovie(10, "One", AirTime.FromDate(2014, 1, 1)),
            new RemoteMovie(11, "Two", AirTime.FromDate(2016, 1, 1)),
            new RemoteMovie(12, "Three", AirTime.FromDate(2019, 1, 1)));
        var missing = CollectionDiff.MissingMovies(new HashSet<int> { 10 }, cat, Now, 90);
        Assert.Equal(new[] { 11, 12 }, missing.Select(m => m.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void BufferZero_MissingRightAfterRelease()
    {
        var cat = JohnWick(new RemoteMovie(4, "Fresh", Now.AddSeconds(-1)));
        Assert.Single(CollectionDiff.MissingMovies(new HashSet<int>(), cat, Now, 0));
    }
}
