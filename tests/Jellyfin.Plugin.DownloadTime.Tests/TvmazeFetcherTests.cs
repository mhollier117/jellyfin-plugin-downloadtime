// Edge-case inventory:
// - REAL fixtures: lookup by thetvdb id 301-redirects to show 3182 (status Ended);
//   /shows/3182/episodes?specials=1 -> 26 regular episodes, airstamp preferred over airdate
//   (S01E01 airstamp 2017-05-01T01:00:00Z != naive airdate rule).
// - episode with null airstamp but airdate -> AirTime rule; both null -> AiredAt null.
// - type != "regular" -> IsSpecial.
// - lookup 404 (show absent from TVmaze) -> Fail.
// - lookup by imdb id path.
using System.Net;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TvmazeFetcherTests
{
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static FakeHttpHandler Handler() => new(uri =>
        (uri.Host, uri.PathAndQuery) switch
        {
            ("api.tvmaze.com", "/lookup/shows?thetvdb=253573") => FakeHttpHandler.Redirect("https://api.tvmaze.com/shows/3182"),
            ("api.tvmaze.com", "/lookup/shows?imdb=tt1898069") => FakeHttpHandler.Redirect("https://api.tvmaze.com/shows/3182"),
            ("api.tvmaze.com", "/shows/3182") => FakeHttpHandler.Json(Fix("tvmaze-lookup-253573.json")),
            ("api.tvmaze.com", "/shows/3182/episodes?specials=1") => FakeHttpHandler.Json(Fix("tvmaze-episodes-americangods.json")),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });

    [Fact]
    public async Task LookupByTvdbId_ParsesEpisodes_AirstampPreferred()
    {
        var f = new TvmazeFetcher(new HttpClient(Handler()));
        var outcome = await f.FetchByTvdbIdAsync("253573", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        var cat = outcome.Catalog!;
        Assert.Equal("TvmazeFallback", cat.SourceKey);
        Assert.Null(cat.IdProviderKey);
        Assert.True(cat.IsEnded);
        Assert.Equal(26, cat.Episodes.Count);
        var first = cat.Episodes.First(e => e.Season == 1 && e.Number == 1);
        // airstamp 2017-05-01T01:00:00+00:00 preferred over airdate 2017-04-30
        Assert.Equal(new DateTimeOffset(2017, 5, 1, 1, 0, 0, TimeSpan.Zero), first.AiredAt);
        Assert.False(first.IsSpecial);
    }

    [Fact]
    public async Task LookupByImdbId_Works()
    {
        var f = new TvmazeFetcher(new HttpClient(Handler()));
        var outcome = await f.FetchByImdbIdAsync("tt1898069", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
    }

    [Fact]
    public async Task Lookup404_ReturnsFail()
    {
        var f = new TvmazeFetcher(new HttpClient(Handler()));
        var outcome = await f.FetchByTvdbIdAsync("111", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task NullAirstamp_UsesAirdateRule_BothNull_Undated_SpecialTyped()
    {
        var handler = new FakeHttpHandler(uri => (uri.Host, uri.PathAndQuery) switch
        {
            ("api.tvmaze.com", "/lookup/shows?thetvdb=9") => FakeHttpHandler.Redirect("https://api.tvmaze.com/shows/9"),
            ("api.tvmaze.com", "/shows/9") => FakeHttpHandler.Json("""{"id":9,"name":"X","status":"Running","externals":{"thetvdb":9}}"""),
            ("api.tvmaze.com", "/shows/9/episodes?specials=1") => FakeHttpHandler.Json("""
                [{"id":1,"season":1,"number":1,"airdate":"2024-01-07","airstamp":null,"type":"regular","name":"a"},
                 {"id":2,"season":1,"number":2,"airdate":null,"airstamp":null,"type":"regular","name":"b"},
                 {"id":3,"season":1,"number":null,"airdate":"2024-02-01","airstamp":null,"type":"significant_special","name":"sp"}]
                """),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });
        var f = new TvmazeFetcher(new HttpClient(handler));
        var cat = (await f.FetchByTvdbIdAsync("9", CancellationToken.None)).Catalog!;
        Assert.False(cat.IsEnded);
        Assert.Equal(new DateTimeOffset(2024, 1, 7, 23, 59, 0, TimeSpan.Zero), cat.Episodes[0].AiredAt);
        Assert.Null(cat.Episodes[1].AiredAt);
        Assert.True(cat.Episodes[2].IsSpecial);
    }
}
