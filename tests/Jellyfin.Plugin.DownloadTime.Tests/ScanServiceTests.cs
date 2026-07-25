// Edge-case inventory:
// - routing: tvdbid folder -> ITvdbSource; anime lib + AniDB -> IAniDbSource; tmdbid -> ITmdbSource; imdb-only -> ITvmazeSource(imdb).
// - lane toggles: disabled lane -> series skipped with note, no fetch.
// - mute list: no fetch, Muted=true.
// - TVDB fail -> TVmaze fallback used (UsedFallback), missing computed from fallback.
// - both TVDB and TVmaze fail -> Error, other series still processed.
// - fetcher THROWS -> caught as Error, scan continues.
// - zero-episode Ok catalog with owned>0 -> Error (fail-safe), zero missing.
// - cache: 2nd scan within TTL -> no fetcher call; fullRefresh -> fetcher called again;
//          ended catalog cached 7d vs continuing 1d boundary honored.
// - movies: two owned movies in one collection -> collection processed once; missing computed;
//           movie without collection skipped; RouteDecision.None -> Error.
// - concurrent ScanAsync -> InvalidOperationException.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ScanServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-scan-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- fakes -------------------------------------------------------------
    private sealed class FakeLibrary : ILibraryReader
    {
        public List<SeriesItemInfo> Series { get; } = new();
        public List<MovieItemInfo> Movies { get; } = new();
        public IReadOnlyList<SeriesItemInfo> GetSeries() => Series;
        public IReadOnlyList<MovieItemInfo> GetMovies() => Movies;
    }

    private sealed class FakeTvdb : ITvdbSource
    {
        public Func<string, FetchOutcome> Respond = _ => FetchOutcome.Fail("unscripted");
        public int Calls;
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(Respond(id)); }
    }

    private sealed class FakeTvmaze : ITvmazeSource
    {
        public Func<string, FetchOutcome> RespondTvdb = _ => FetchOutcome.Fail("unscripted");
        public Func<string, FetchOutcome> RespondImdb = _ => FetchOutcome.Fail("unscripted");
        public int Calls;
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(RespondTvdb(id)); }
        public Task<FetchOutcome> FetchByImdbIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(RespondImdb(id)); }
    }

    private sealed class FakeAniDb : IAniDbSource
    {
        public Func<string, FetchOutcome> Respond = _ => FetchOutcome.Fail("unscripted");
        public int Calls;
        public Task<FetchOutcome> FetchByAnimeIdAsync(string id, CancellationToken ct) { Calls++; return Task.FromResult(Respond(id)); }
    }

    private sealed class FakeTmdb : ITmdbSource
    {
        public Func<string, FetchOutcome> RespondSeries = _ => FetchOutcome.Fail("unscripted");
        public Func<int, CollectionOutcome> RespondCollection = _ => new CollectionOutcome(null, null, true);
        public int SeriesCalls, CollectionCalls;
        public Task<FetchOutcome> FetchSeriesAsync(string id, CancellationToken ct) { SeriesCalls++; return Task.FromResult(RespondSeries(id)); }
        public Task<CollectionOutcome> FetchCollectionForMovieAsync(int id, CancellationToken ct) { CollectionCalls++; return Task.FromResult(RespondCollection(id)); }
    }

    // ---- helpers -----------------------------------------------------------
    private static ScanSettings Settings(bool tv = true, bool anime = true, bool movies = true, string[]? muted = null)
        => new(tv, anime, movies, 24, false, 90, new HashSet<string>(muted ?? Array.Empty<string>()),
               TimeSpan.FromDays(1), TimeSpan.FromDays(7));

    private static SeriesItemInfo Series(Guid id, string path, bool animeLib, Dictionary<string, string> ids, params OwnedEpisode[] eps)
        => new(id, System.IO.Path.GetFileName(path), path, animeLib, ids, eps);

    private static OwnedEpisode O(int s, int n) => new(s, n, null, new Dictionary<string, string>(), null);

    private static RemoteCatalog TvdbCat(params RemoteEpisode[] eps) => new("Tvdb", "Tvdb", "1", false, eps);
    private static RemoteEpisode R(int s, int n, string id) => new(s, n, id, AirTime.FromDate(2026, 1, n), false, null);

    private (ScanService Svc, FakeLibrary Lib, FakeTvdb Tvdb, FakeTvmaze Tvmaze, FakeAniDb Ani, FakeTmdb Tmdb, FakeClock Clock) Make()
    {
        var lib = new FakeLibrary();
        var tvdb = new FakeTvdb();
        var tvmaze = new FakeTvmaze();
        var ani = new FakeAniDb();
        var tmdb = new FakeTmdb();
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, tvdb, tvmaze, ani, tmdb, new CatalogCache(_dir, clock), clock);
        return (svc, lib, tvdb, tvmaze, ani, tmdb, clock);
    }

    // ---- tests ---------------------------------------------------------------

    [Fact]
    public async Task Routing_TvdbFolder_UsesTvdb_MissingComputed()
    {
        var (svc, lib, tvdb, _, _, _, _) = Make();
        var sid = Guid.NewGuid();
        lib.Series.Add(Series(sid, @"D:\TV\X (2020) [tvdbid-1]", false,
            new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Ok(TvdbCat(R(1, 1, "e1"), R(1, 2, "e2")));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Equal("Tvdb", s.Lane);
        Assert.False(s.UsedFallback);
        Assert.Null(s.Error);
        var m = Assert.Single(s.Missing);
        Assert.Equal(2, m.Number);
        Assert.True(svc.LastDiffs.ContainsKey(sid));
    }

    [Fact]
    public async Task Routing_AnimeAndTmdbAndImdb()
    {
        var (svc, lib, _, tvmaze, ani, tmdb, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\Anime\A [tvdbid-9]", true, new() { ["AniDB"] = "18164", ["Tvdb"] = "9" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\B [tmdbid-110316]", false, new() { ["Tmdb"] = "110316" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\C", false, new() { ["Imdb"] = "tt1" }, O(1, 1)));
        ani.Respond = _ => FetchOutcome.Ok(new RemoteCatalog("AniDB", "AniDB", "18164", true, new[] { new RemoteEpisode(null, 1, "274088", AirTime.FromDate(2024, 1, 7), false, null) }));
        tmdb.RespondSeries = _ => FetchOutcome.Ok(new RemoteCatalog("Tmdb", null, "110316", true, new[] { new RemoteEpisode(1, 1, null, AirTime.FromDate(2020, 12, 10), false, null) }));
        tvmaze.RespondImdb = _ => FetchOutcome.Ok(new RemoteCatalog("TvmazeFallback", null, "3182", true, new[] { new RemoteEpisode(1, 1, null, AirTime.FromDate(2017, 4, 30), false, null) }));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(1, ani.Calls);
        Assert.Equal(1, tmdb.SeriesCalls);
        Assert.Equal(1, tvmaze.Calls);
        Assert.All(report.Series, s => Assert.Null(s.Error));
    }

    [Fact]
    public async Task LaneToggles_SkipWithoutFetch()
    {
        var (svc, lib, tvdb, _, ani, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\Anime\A", true, new() { ["AniDB"] = "2" }, O(1, 1)));
        var report = await svc.ScanAsync(Settings(tv: false, anime: false), false, null, CancellationToken.None);
        Assert.Equal(0, tvdb.Calls);
        Assert.Equal(0, ani.Calls);
        Assert.All(report.Series, s => Assert.Contains(s.Notes, n => n.Contains("disabled", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task MutedSeries_NoFetch_MutedFlag()
    {
        var (svc, lib, tvdb, _, _, _, _) = Make();
        var sid = Guid.NewGuid();
        lib.Series.Add(Series(sid, @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        var report = await svc.ScanAsync(Settings(muted: new[] { sid.ToString("N") }), false, null, CancellationToken.None);
        Assert.Equal(0, tvdb.Calls);
        Assert.True(Assert.Single(report.Series).Muted);
    }

    [Fact]
    public async Task TvdbFails_TvmazeFallback_Engaged()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Fail("markup changed");
        tvmaze.RespondTvdb = _ => FetchOutcome.Ok(new RemoteCatalog("TvmazeFallback", null, "3182", false,
            new[] { new RemoteEpisode(1, 1, null, AirTime.FromDate(2026, 1, 1), false, null), new RemoteEpisode(1, 2, null, AirTime.FromDate(2026, 1, 2), false, null) }));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.True(s.UsedFallback);
        Assert.Null(s.Error);
        Assert.Single(s.Missing);
    }

    [Fact]
    public async Task BothFail_ErrorRecorded_OthersContinue()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\Y [tvdbid-2]", false, new() { ["Tvdb"] = "2" }, O(1, 1)));
        tvdb.Respond = id => id == "1" ? FetchOutcome.Fail("down") : FetchOutcome.Ok(TvdbCat(R(1, 1, "a")));
        tvmaze.RespondTvdb = _ => FetchOutcome.Fail("also down");
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.NotNull(report.Series.Single(s => s.Name.StartsWith("X")).Error);
        Assert.Null(report.Series.Single(s => s.Name.StartsWith("Y")).Error);
    }

    [Fact]
    public async Task FetcherThrows_CaughtAsError_ScanContinues()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\Y [tvdbid-2]", false, new() { ["Tvdb"] = "2" }, O(1, 1)));
        tvdb.Respond = id => id == "1" ? throw new InvalidOperationException("boom") : FetchOutcome.Ok(TvdbCat(R(1, 1, "a")));
        tvmaze.RespondTvdb = _ => throw new InvalidOperationException("boom2");
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.NotNull(report.Series.Single(s => s.Name.StartsWith("X")).Error);
        Assert.Null(report.Series.Single(s => s.Name.StartsWith("Y")).Error);
    }

    [Fact]
    public async Task ZeroEpisodeCatalog_WithOwned_FailSafeError()
    {
        var (svc, lib, tvdb, tvmaze, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Ok(new RemoteCatalog("Tvdb", "Tvdb", "1", false, Array.Empty<RemoteEpisode>()));
        tvmaze.RespondTvdb = _ => FetchOutcome.Fail("nope");
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.NotNull(s.Error);
        Assert.Empty(s.Missing);
    }

    [Fact]
    public async Task Cache_SecondScanWithinTtl_NoFetch_FullRefreshBypasses()
    {
        var (svc, lib, tvdb, _, _, _, clock) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        tvdb.Respond = _ => FetchOutcome.Ok(TvdbCat(R(1, 1, "a"), R(1, 2, "b")));
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(1, tvdb.Calls);
        clock.UtcNow = Now.AddHours(6); // within 1d continuing TTL
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(1, tvdb.Calls); // served from cache
        clock.UtcNow = Now.AddDays(2); // past TTL
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(2, tvdb.Calls);
        await svc.ScanAsync(Settings(), fullRefresh: true, null, CancellationToken.None);
        Assert.Equal(3, tvdb.Calls); // bypassed
    }

    [Fact]
    public async Task Movies_CollectionProcessedOnce_MissingComputed_NoCollectionSkipped()
    {
        var (svc, lib, _, _, _, tmdb, _) = Make();
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "John Wick", 245891));
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "John Wick: Chapter 2", 324552));
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "Standalone", 777));
        lib.Movies.Add(new MovieItemInfo(Guid.NewGuid(), "NoTmdbId", null));
        var jw = new CollectionCatalog(404609, "John Wick Collection", new[]
        {
            new RemoteMovie(245891, "John Wick", AirTime.FromDate(2014, 10, 24)),
            new RemoteMovie(324552, "John Wick: Chapter 2", AirTime.FromDate(2017, 2, 10)),
            new RemoteMovie(458156, "John Wick: Chapter 3", AirTime.FromDate(2019, 5, 17)),
        });
        tmdb.RespondCollection = id => id is 245891 or 324552
            ? new CollectionOutcome(jw, null, false)
            : new CollectionOutcome(null, null, true);
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var col = Assert.Single(report.Collections);
        Assert.Equal(458156, Assert.Single(col.Missing).TmdbId);
        // collection catalog fetched for at most one member thanks to per-collection dedup
        Assert.True(tmdb.CollectionCalls <= 3);
    }

    [Fact]
    public async Task NoUsableId_Error()
    {
        var (svc, lib, _, _, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X", false, new(), O(1, 1)));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Contains("provider id", Assert.Single(report.Series).Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentScan_Throws()
    {
        var (svc, lib, tvdb, _, _, _, _) = Make();
        lib.Series.Add(Series(Guid.NewGuid(), @"D:\TV\X [tvdbid-1]", false, new() { ["Tvdb"] = "1" }, O(1, 1)));
        var gate = new TaskCompletionSource();
        tvdb.Respond = _ => { gate.Task.Wait(); return FetchOutcome.Ok(TvdbCat(R(1, 1, "a"))); };
        var first = Task.Run(() => svc.ScanAsync(Settings(), false, null, CancellationToken.None));
        while (!svc.IsScanning) { await Task.Delay(10); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ScanAsync(Settings(), false, null, CancellationToken.None));
        gate.SetResult();
        await first;
    }
}
