// Edge-case inventory (ScanService AniDB entry-chain walking):
// - chain BFS from identified entry follows Sequel ids transitively; union catalog
//   detects sequel-entry gaps (M3/M4); lane label "AniDB (N entries)" when N>1.
// - per-entry caching: second scan fetches nothing; fullRefresh refetches all.
// - sequel entry fetch failure -> partial union + per-series note naming the entry,
//   series NOT errored; root entry failure -> series Error.
// - cycles (A->B->A) terminate; chains cap at AniDbChain.MaxEntries (16).
// - pacing integration: a 3-entry chain through the REAL AniDbFetcher = 3 HTTP
//   requests with 2 paced waits (FakeClock; no real sleeping).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ScanServiceChainTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-chain-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private sealed class FakeLibrary : ILibraryReader
    {
        public List<SeriesItemInfo> Series { get; } = new();
        public IReadOnlyList<SeriesItemInfo> GetSeries() => Series;
        public IReadOnlyList<MovieItemInfo> GetMovies() => Array.Empty<MovieItemInfo>();
    }

    private sealed class DeadTvdb : ITvdbSource
    {
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
    }

    private sealed class DeadTvmaze : ITvmazeSource
    {
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
        public Task<FetchOutcome> FetchByImdbIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
    }

    private sealed class DeadTmdb : ITmdbSource
    {
        public Task<FetchOutcome> FetchSeriesAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
        public Task<CollectionOutcome> FetchCollectionForMovieAsync(int id, CancellationToken ct) => Task.FromResult(new CollectionOutcome(null, null, true));
    }

    private sealed class FakeChainAniDb : IAniDbSource
    {
        public Dictionary<string, AniDbEntryOutcome> Entries { get; } = new();
        public List<string> Fetched { get; } = new();
        public Task<FetchOutcome> FetchByAnimeIdAsync(string id, CancellationToken ct)
            => throw new InvalidOperationException("chain path must use FetchEntryAsync");
        public Task<AniDbEntryOutcome> FetchEntryAsync(string id, CancellationToken ct)
        {
            Fetched.Add(id);
            return Task.FromResult(Entries.TryGetValue(id, out var o)
                ? o
                : new AniDbEntryOutcome(null, Array.Empty<string>(), "entry " + id + " unavailable"));
        }
    }

    private static RemoteCatalog Entry(string aid, int count, string idPrefix, int year)
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= count; i++)
        {
            eps.Add(new RemoteEpisode(null, i, idPrefix + i, AirTime.FromDate(year, 1, i), false, null));
        }
        return new RemoteCatalog("AniDB", "AniDB", aid, true, eps);
    }

    private static AniDbEntryOutcome Ok(RemoteCatalog c, params string[] sequels) => new(c, sequels, null);

    private static ScanSettings Settings() => new(true, true, true, 24, false, 90,
        new HashSet<string>(), TimeSpan.FromDays(1), TimeSpan.FromDays(7));

    private static SeriesItemInfo AnimeSeries(params OwnedEpisode[] eps)
        => new(Guid.NewGuid(), "7th Time Loop", @"D:\Anime\7th Time Loop [anidbid-18164]", true,
            new Dictionary<string, string> { ["AniDB"] = "18164" }, eps);

    private static OwnedEpisode OA(int s, int n, string id)
        => new(s, n, null, new Dictionary<string, string> { ["AniDB"] = id }, null);

    private (ScanService Svc, FakeLibrary Lib, FakeChainAniDb Ani, FakeClock Clock) Make()
    {
        var lib = new FakeLibrary();
        var ani = new FakeChainAniDb();
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, new DeadTvdb(), new DeadTvmaze(), ani, new DeadTmdb(), new CatalogCache(_dir, clock), clock);
        return (svc, lib, ani, clock);
    }

    private void Seed3Chain(FakeChainAniDb ani)
    {
        ani.Entries["18164"] = Ok(Entry("18164", 4, "a", 2024), "18800");
        ani.Entries["18800"] = Ok(Entry("18800", 3, "b", 2025), "18900");
        ani.Entries["18900"] = Ok(Entry("18900", 2, "c", 2026));
    }

    [Fact]
    public async Task Chain_SequelGapDetected_LaneLabeled()
    {
        var (svc, lib, ani, _) = Make();
        Seed3Chain(ani);
        // merged local, ids present, owns everything except b2 (entry 2) and c2 (entry 3)
        lib.Series.Add(AnimeSeries(
            OA(1, 1, "a1"), OA(1, 2, "a2"), OA(1, 3, "a3"), OA(1, 4, "a4"),
            OA(1, 5, "b1"), OA(1, 7, "b3"), OA(1, 8, "c1")));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        Assert.Equal("AniDB (3 entries)", s.Lane);
        Assert.Equal(new[] { "b2", "c2" }, s.Missing.Select(m => m.SourceEpisodeId).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "18164", "18800", "18900" }, ani.Fetched.ToArray());
    }

    [Fact]
    public async Task Chain_PerEntryCaching_SecondScanNoFetch_FullRefreshRefetches()
    {
        var (svc, lib, ani, _) = Make();
        Seed3Chain(ani);
        lib.Series.Add(AnimeSeries(OA(1, 1, "a1")));
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(3, ani.Fetched.Count);
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(3, ani.Fetched.Count); // all three served from per-entry cache
        await svc.ScanAsync(Settings(), true, null, CancellationToken.None);
        Assert.Equal(6, ani.Fetched.Count); // fullRefresh refetches the chain
    }

    [Fact]
    public async Task Chain_SequelFails_PartialUnionWithNote_NoSeriesError()
    {
        var (svc, lib, ani, _) = Make();
        ani.Entries["18164"] = Ok(Entry("18164", 4, "a", 2024), "18800"); // 18800 NOT seeded -> fails
        lib.Series.Add(AnimeSeries(OA(1, 1, "a1"), OA(1, 2, "a2"), OA(1, 3, "a3")));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        Assert.Contains(s.Notes, n => n.Contains("18800") && n.Contains("partial", StringComparison.OrdinalIgnoreCase));
        var m = Assert.Single(s.Missing); // a4 from the reachable entry
        Assert.Equal("a4", m.SourceEpisodeId);
    }

    [Fact]
    public async Task Chain_RootFails_SeriesError()
    {
        var (svc, lib, ani, _) = Make();
        lib.Series.Add(AnimeSeries(OA(1, 1, "a1")));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.NotNull(s.Error);
        Assert.Empty(s.Missing);
    }

    [Fact]
    public async Task Chain_CycleTerminates()
    {
        var (svc, lib, ani, _) = Make();
        ani.Entries["18164"] = Ok(Entry("18164", 2, "a", 2024), "18800");
        ani.Entries["18800"] = Ok(Entry("18800", 2, "b", 2025), "18164"); // cycle back
        lib.Series.Add(AnimeSeries(OA(1, 1, "a1"), OA(1, 2, "a2"), OA(1, 3, "b1"), OA(1, 4, "b2")));
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        Assert.Equal(2, ani.Fetched.Count); // each entry fetched exactly once
        Assert.Empty(s.Missing);
    }

    [Fact]
    public async Task Chain_CapsAtMaxEntries()
    {
        var (svc, lib, ani, _) = Make();
        for (var i = 0; i < 20; i++)
        {
            var aid = (18164 + i).ToString();
            var next = (18164 + i + 1).ToString();
            ani.Entries[aid] = Ok(Entry(aid, 1, "e" + i + "_", 2024), next);
        }
        lib.Series.Add(AnimeSeries(OA(1, 1, "e0_1")));
        await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        Assert.Equal(AniDbChain.MaxEntries, ani.Fetched.Count);
    }

    [Fact]
    public async Task Pacing_ThreeEntryChain_ThreeRequestsTwoWaits()
    {
        var lib = new FakeLibrary();
        lib.Series.Add(AnimeSeries(OA(1, 1, "274088")));
        var clock = new FakeClock(Now);
        var delays = new List<TimeSpan>();
        var handler = new FakeHttpHandler(uri =>
            uri.Query.Contains("aid=18164") ? FakeHttpHandler.Xml(Fix("anidb-anime-18164.xml"))
            : uri.Query.Contains("aid=18800") ? FakeHttpHandler.Xml(Fix("anidb-anime-18800.xml"))
            : uri.Query.Contains("aid=18900") ? FakeHttpHandler.Xml(Fix("anidb-anime-18900.xml"))
            : FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound));
        var fetcher = new AniDbFetcher(new HttpClient(handler), clock,
            async ts => { delays.Add(ts); clock.UtcNow += ts; await Task.CompletedTask; },
            () => 2000, () => ("testclient", 1));
        var svc = new ScanService(lib, new DeadTvdb(), new DeadTvmaze(), fetcher, new DeadTmdb(),
            new CatalogCache(_dir, clock), clock);
        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        Assert.Equal("AniDB (3 entries)", s.Lane);
        Assert.Equal(3, handler.Requests.Count);   // one HTTP request per entry
        Assert.Equal(2, delays.Count);             // every follow-up request paced
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMilliseconds(2000), d));
    }
}
