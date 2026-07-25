// Edge-case inventory:
// - golden XML: 4 regular episodes (one undated -> AiredAt null), 1 special (epno type=2, "S1" -> Number 1, IsSpecial);
//   AniDB episode ids as SourceEpisodeId; Season null everywhere; airdate -> AirTime rule;
//   enddate 2024-03-24 past clock -> IsEnded true.
// - error XML (<error>banned</error>) -> Fail, never an empty catalog.
// - HTTP non-200 -> Fail.
// - request URL carries client/clientver/protover/request/aid params.
// - PACING: two consecutive fetches -> second waits >= requestDelayMs (measured via FakeClock + recorded delayFn).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AniDbFetcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static AniDbFetcher Make(FakeHttpHandler handler, FakeClock clock, List<TimeSpan>? delays = null, int delayMs = 2000)
        => new(new HttpClient(handler), clock,
            async ts => { delays?.Add(ts); clock.UtcNow += ts; await Task.CompletedTask; },
            () => delayMs, () => ("downloadtime", 1));

    [Fact]
    public void ParseAnime_Golden()
    {
        var (cat, err) = AniDbFetcher.ParseAnime(Fix("anidb-anime-18164.xml"), new FakeClock(Now));
        Assert.Null(err);
        Assert.Equal("AniDB", cat!.SourceKey);
        Assert.Equal("AniDB", cat.IdProviderKey);
        Assert.Equal("18164", cat.SeriesSourceId);
        Assert.True(cat.IsEnded);
        Assert.Equal(5, cat.Episodes.Count);
        Assert.All(cat.Episodes, e => Assert.Null(e.Season));
        var ep2 = cat.Episodes.Single(e => e.SourceEpisodeId == "274089");
        Assert.Equal(2, ep2.Number);
        Assert.False(ep2.IsSpecial);
        Assert.Equal(new DateTimeOffset(2024, 1, 14, 23, 59, 0, TimeSpan.Zero), ep2.AiredAt);
        var special = cat.Episodes.Single(e => e.SourceEpisodeId == "290001");
        Assert.True(special.IsSpecial);
        Assert.Equal(1, special.Number);
        var undated = cat.Episodes.Single(e => e.SourceEpisodeId == "290002");
        Assert.Null(undated.AiredAt);
    }

    [Fact]
    public void ParseAnime_ErrorXml_Fails()
    {
        var (cat, err) = AniDbFetcher.ParseAnime(Fix("anidb-error-banned.xml"), new FakeClock(Now));
        Assert.Null(cat);
        Assert.Contains("banned", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_BuildsUrl_AndParses()
    {
        var handler = new FakeHttpHandler(uri =>
            uri.Host == "api.anidb.net" ? FakeHttpHandler.Xml(Fix("anidb-anime-18164.xml"))
                                        : FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound));
        var f = Make(handler, new FakeClock(Now));
        var outcome = await f.FetchByAnimeIdAsync("18164", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        var q = handler.Requests[0].Query;
        Assert.Contains("request=anime", q);
        Assert.Contains("client=downloadtime", q);
        Assert.Contains("clientver=1", q);
        Assert.Contains("protover=1", q);
        Assert.Contains("aid=18164", q);
    }

    [Fact]
    public async Task Pacing_SecondRequestWaits()
    {
        var clock = new FakeClock(Now);
        var delays = new List<TimeSpan>();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Xml(Fix("anidb-anime-18164.xml")));
        var f = Make(handler, clock, delays);
        await f.FetchByAnimeIdAsync("1", CancellationToken.None);
        Assert.Empty(delays); // first request never waits
        clock.UtcNow += TimeSpan.FromMilliseconds(500); // only 0.5s elapsed
        await f.FetchByAnimeIdAsync("2", CancellationToken.None);
        var wait = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), wait); // tops up to 2000ms
        clock.UtcNow += TimeSpan.FromSeconds(10); // plenty elapsed
        await f.FetchByAnimeIdAsync("3", CancellationToken.None);
        Assert.Single(delays); // no new wait
    }

    [Fact]
    public async Task Http503_Fails()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable));
        var f = Make(handler, new FakeClock(Now));
        var outcome = await f.FetchByAnimeIdAsync("1", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }
}
