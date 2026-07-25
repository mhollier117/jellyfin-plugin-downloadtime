// Edge-case inventory:
// - series: two seasons + season 0; episodes flagged special only in S0; air_date -> 23:59Z; status Ended -> IsEnded.
// - blank key -> fail fast, zero HTTP requests.
// - 401 -> Fail mentioning key.
// - 429 with Retry-After: waits, retries once, succeeds; 429 twice -> Fail.
// - movie without collection -> NoCollection.
// - movie with collection -> parts parsed, null release_date -> ReleasedAt null.
using System.Net;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TmdbFetcherTests
{
    private const string TvJson = """
        {"id":110316,"status":"Ended","seasons":[
          {"season_number":0,"episode_count":1},
          {"season_number":1,"episode_count":2},
          {"season_number":2,"episode_count":1}]}
        """;
    private const string S0Json = """{"episodes":[{"season_number":0,"episode_number":1,"air_date":"2021-01-01","name":"sp"}]}""";
    private const string S1Json = """
        {"episodes":[
        {"season_number":1,"episode_number":1,"air_date":"2020-12-10","name":"e1"},
        {"season_number":1,"episode_number":2,"air_date":null,"name":"e2"}]}
        """;
    private const string S2Json = """{"episodes":[{"season_number":2,"episode_number":1,"air_date":"2022-12-22","name":"e3"}]}""";

    private static TmdbFetcher Make(FakeHttpHandler h, string key = "k", List<TimeSpan>? delays = null)
        => new(new HttpClient(h), () => key, ts => { delays?.Add(ts); return Task.CompletedTask; });

    private static FakeHttpHandler SeriesHandler() => new(uri => uri.PathAndQuery switch
    {
        var p when p.StartsWith("/3/tv/110316/season/0") => FakeHttpHandler.Json(S0Json),
        var p when p.StartsWith("/3/tv/110316/season/1") => FakeHttpHandler.Json(S1Json),
        var p when p.StartsWith("/3/tv/110316/season/2") => FakeHttpHandler.Json(S2Json),
        var p when p.StartsWith("/3/tv/110316") => FakeHttpHandler.Json(TvJson),
        _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task Series_AllSeasonsFetched_SpecialsFlagged_DatesNormalized()
    {
        var f = Make(SeriesHandler());
        var cat = (await f.FetchSeriesAsync("110316", CancellationToken.None)).Catalog!;
        Assert.Equal("Tmdb", cat.SourceKey);
        Assert.Null(cat.IdProviderKey);
        Assert.True(cat.IsEnded);
        Assert.Equal(4, cat.Episodes.Count);
        Assert.True(cat.Episodes.Single(e => e.Season == 0).IsSpecial);
        var e1 = cat.Episodes.Single(e => e.Season == 1 && e.Number == 1);
        Assert.Equal(new DateTimeOffset(2020, 12, 10, 23, 59, 0, TimeSpan.Zero), e1.AiredAt);
        Assert.Null(cat.Episodes.Single(e => e.Season == 1 && e.Number == 2).AiredAt);
    }

    [Fact]
    public async Task BlankKey_FailsWithoutHttp()
    {
        var h = SeriesHandler();
        var f = Make(h, key: "");
        var outcome = await f.FetchSeriesAsync("110316", CancellationToken.None);
        Assert.NotNull(outcome.Error);
        Assert.Contains("key", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Unauthorized_FailsMentioningKey()
    {
        var f = Make(new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.Unauthorized)));
        var outcome = await f.FetchSeriesAsync("110316", CancellationToken.None);
        Assert.Contains("key", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RateLimited_RetriesOnceAfterRetryAfter()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var h = new FakeHttpHandler(uri =>
        {
            if (uri.PathAndQuery.StartsWith("/3/tv/110316/season")) return uri.PathAndQuery.StartsWith("/3/tv/110316/season/0") ? FakeHttpHandler.Json(S0Json) : uri.PathAndQuery.StartsWith("/3/tv/110316/season/1") ? FakeHttpHandler.Json(S1Json) : FakeHttpHandler.Json(S2Json);
            calls++;
            if (calls == 1)
            {
                var r = FakeHttpHandler.Status(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return r;
            }
            return FakeHttpHandler.Json(TvJson);
        });
        var f = Make(h, delays: delays);
        var outcome = await f.FetchSeriesAsync("110316", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(delays));
    }

    [Fact]
    public async Task Movie_NoCollection()
    {
        var h = new FakeHttpHandler(uri => uri.PathAndQuery.StartsWith("/3/movie/500")
            ? FakeHttpHandler.Json("""{"id":500,"belongs_to_collection":null}""")
            : FakeHttpHandler.Status(HttpStatusCode.NotFound));
        var f = Make(h);
        var outcome = await f.FetchCollectionForMovieAsync(500, CancellationToken.None);
        Assert.True(outcome.NoCollection);
        Assert.Null(outcome.Catalog);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task Movie_WithCollection_PartsParsed()
    {
        var h = new FakeHttpHandler(uri => uri.PathAndQuery switch
        {
            var p when p.StartsWith("/3/movie/245891") => FakeHttpHandler.Json("""{"id":245891,"belongs_to_collection":{"id":404609,"name":"John Wick Collection"}}"""),
            var p when p.StartsWith("/3/collection/404609") => FakeHttpHandler.Json("""
                {"id":404609,"name":"John Wick Collection","parts":[
                  {"id":245891,"title":"John Wick","release_date":"2014-10-24"},
                  {"id":324552,"title":"John Wick: Chapter 2","release_date":"2017-02-10"},
                  {"id":999999,"title":"Announced Wick","release_date":null}]}
                """),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });
        var f = Make(h);
        var outcome = await f.FetchCollectionForMovieAsync(245891, CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal(404609, outcome.Catalog!.CollectionId);
        Assert.Equal(3, outcome.Catalog.Movies.Count);
        Assert.Equal(new DateTimeOffset(2014, 10, 24, 23, 59, 0, TimeSpan.Zero), outcome.Catalog.Movies[0].ReleasedAt);
        Assert.Null(outcome.Catalog.Movies[2].ReleasedAt);
    }
}
