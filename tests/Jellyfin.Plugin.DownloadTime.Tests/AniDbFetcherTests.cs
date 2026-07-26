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

    private static AniDbFetcher Make(FakeHttpHandler handler, FakeClock clock, List<TimeSpan>? delays = null, int delayMs = 2000, string clientName = "exampleclient")
        => new(new HttpClient(handler), clock,
            async ts => { delays?.Add(ts); clock.UtcNow += ts; await Task.CompletedTask; },
            () => delayMs, () => (clientName, 1));

    [Fact]
    public async Task BlankClientName_FailsFast_NoHttp()
    {
        // Per-user AniDB registration: client strings are tied to the OWNER's
        // AniDB account, so the plugin must never ship a shared default.
        // Blank name -> clear config error, zero network calls.
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Xml("<anime id=\"1\"/>"));
        var f = Make(handler, new FakeClock(Now), clientName: "");
        var outcome = await f.FetchByAnimeIdAsync("18164", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.Contains("client", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

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
        Assert.Contains("client=exampleclient", q);
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
    public async Task GzippedResponse_IsDecompressed()
    {
        // AniDB's httpapi serves gzip-compressed XML regardless of Accept-Encoding
        // (observed live 2026-07-25: parser saw 0x1F magic byte). Fetcher must
        // detect the gzip signature and decompress before parsing.
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "anidb-anime-18164.xml"));
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(raw);
        }
        var gzBytes = ms.ToArray();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(gzBytes),
        });
        var f = Make(handler, new FakeClock(Now));
        var outcome = await f.FetchByAnimeIdAsync("18164", CancellationToken.None);
        Assert.Null(outcome.Error);
        Assert.Equal(5, outcome.Catalog!.Episodes.Count);
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

    [Fact]
    public void ParseAnime_CreditsTrailersParodies_AreNotContent()
    {
        // Live defect 2026-07-26: every Boruto "gap" was an opening/ending
        // credit sequence. AniDB epno types: 1=regular, 2=special, 3=credits,
        // 4=trailer, 5=parody, 6=other. Only 1 and 2 are downloadable content;
        // 3-6 must never be reported as missing, even with IncludeSpecials on.
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <anime id="12661" restricted="false">
              <episodes>
                <episode id="1"><epno type="1">1</epno><airdate>2017-04-05</airdate><title xml:lang="en">Boruto Uzumaki</title></episode>
                <episode id="2"><epno type="2">S1</epno><airdate>2017-04-01</airdate><title xml:lang="en">Episode S1</title></episode>
                <episode id="3"><epno type="3">C1</epno><airdate>2017-04-05</airdate><title xml:lang="en">Opening 1</title></episode>
                <episode id="4"><epno type="3">C8</epno><airdate>2017-04-12</airdate><title xml:lang="en">Ending 1</title></episode>
                <episode id="5"><epno type="4">T1</epno><airdate>2017-03-01</airdate><title xml:lang="en">Teaser PV</title></episode>
                <episode id="6"><epno type="5">P1</epno><airdate>2017-05-01</airdate><title xml:lang="en">Parody</title></episode>
                <episode id="7"><epno type="6">O1</epno><airdate>2017-06-01</airdate><title xml:lang="en">Other</title></episode>
              </episodes>
            </anime>
            """;
        var (cat, err) = AniDbFetcher.ParseAnime(xml, new FakeClock(Now));
        Assert.Null(err);
        var ids = cat!.Episodes.Select(e => e.SourceEpisodeId).ToArray();
        Assert.Equal(new[] { "1", "2" }, ids);
        Assert.False(cat.Episodes.Single(e => e.SourceEpisodeId == "1").IsSpecial);
        Assert.True(cat.Episodes.Single(e => e.SourceEpisodeId == "2").IsSpecial);
    }
}
