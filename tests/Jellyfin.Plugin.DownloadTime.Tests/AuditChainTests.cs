// Forensic-audit fixes (2026-08-03), ScanService chain side.
// D1: the sequel-chain BFS must stop when a sequel aid is ANOTHER library
//     series' root - otherwise one series report swallows the whole franchise
//     (live: Naruto claimed 804 missing; ~799 were Shippuden/Boruto episodes
//     owned under their own series).
// D3i: chain entries reachable from several series (movie/special entries)
//     are reported at most ONCE, attributed to the series whose root is
//     nearest in the chain.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AuditChainTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-audit-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

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

    private static RemoteCatalog MovieEntry(string aid, string epId, int year)
        => new("AniDB", "AniDB", aid, true, new[]
        {
            new RemoteEpisode(null, 1, epId, AirTime.FromDate(year, 6, 1), true, "Complete Movie"),
        });

    private static AniDbEntryOutcome Ok(RemoteCatalog c, params string[] sequels) => new(c, sequels, null);

    private static ScanSettings Settings(bool specials = false) => new(true, true, true, 24, specials, 90,
        new HashSet<string>(), TimeSpan.FromDays(1), TimeSpan.FromDays(7));

    private static SeriesItemInfo Anime(string name, string aid, params OwnedEpisode[] eps)
        => new(Guid.NewGuid(), name, $@"D:\Anime\{name} [anidbid-{aid}]", true,
            new Dictionary<string, string> { ["AniDB"] = aid }, eps);

    private static OwnedEpisode OA(int s, int n, string id)
        => new(s, n, null, new Dictionary<string, string> { ["AniDB"] = id }, null);

    private (ScanService Svc, FakeLibrary Lib, FakeChainAniDb Ani) Make()
    {
        var lib = new FakeLibrary();
        var ani = new FakeChainAniDb();
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, new DeadTvdb(), new DeadTvmaze(), ani, new DeadTmdb(), new CatalogCache(_dir, clock), clock);
        return (svc, lib, ani);
    }

    // ----------------------------------------------------------------- D1 --

    [Fact]
    public async Task OtherSeriesRoot_StopsChainExpansion()
    {
        var (svc, lib, ani) = Make();
        ani.Entries["239"] = Ok(Entry("239", 5, "n", 2010), "4880");
        ani.Entries["4880"] = Ok(Entry("4880", 3, "s", 2015));
        lib.Series.Add(Anime("Naruto", "239", OA(1, 1, "n1"), OA(1, 2, "n2"), OA(1, 3, "n3"), OA(1, 4, "n4")));
        lib.Series.Add(Anime("Shippuden", "4880", OA(1, 1, "s1"), OA(1, 2, "s2"), OA(1, 3, "s3")));

        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var naruto = report.Series.Single(s => s.Name == "Naruto");
        var ship = report.Series.Single(s => s.Name == "Shippuden");

        var m = Assert.Single(naruto.Missing);          // n5 only - nothing from 4880
        Assert.Equal("n5", m.SourceEpisodeId);
        Assert.Equal("AniDB", naruto.Lane);             // single-entry chain again
        Assert.Empty(ship.Missing);
    }

    // ---------------------------------------------------------------- D3i --

    [Fact]
    public async Task SharedMovieEntry_ReportedOnce_NearestRootWins()
    {
        // A: 100 -> 150(movie) -> 300(movie, distance 2)
        // B: 200 -> 300(movie, distance 1)  => 300 belongs to B.
        var (svc, lib, ani) = Make();
        ani.Entries["100"] = Ok(Entry("100", 2, "a", 2010), "150");
        ani.Entries["150"] = Ok(MovieEntry("150", "m150", 2011), "300");
        ani.Entries["300"] = Ok(MovieEntry("300", "m300", 2016));
        ani.Entries["200"] = Ok(Entry("200", 2, "b", 2015), "300");
        lib.Series.Add(Anime("Cour One", "100", OA(1, 1, "a1"), OA(1, 2, "a2")));
        lib.Series.Add(Anime("Cour Two", "200", OA(1, 1, "b1"), OA(1, 2, "b2")));

        var report = await svc.ScanAsync(Settings(specials: true), false, null, CancellationToken.None);
        var a = report.Series.Single(s => s.Name == "Cour One");
        var b = report.Series.Single(s => s.Name == "Cour Two");

        Assert.Contains(a.Missing, m => m.SourceEpisodeId == "m150");
        Assert.DoesNotContain(a.Missing, m => m.SourceEpisodeId == "m300"); // B is nearer
        Assert.Contains(b.Missing, m => m.SourceEpisodeId == "m300");
        Assert.DoesNotContain(b.Missing, m => m.SourceEpisodeId == "m150");
    }

    // ------------------------------------------------------------- D5/D6 --

    [Fact]
    public async Task MissingDto_CarriesSpecialFlag_EntryNameAndAbsolute()
    {
        var (svc, lib, ani) = Make();
        var entry1 = Entry("500", 2, "a", 2010) with { Name = "Cour One" };
        var eps2 = new List<RemoteEpisode>
        {
            new(null, 1, "c1", AirTime.FromDate(2015, 1, 5), false, "Opening Night"),
            new(null, 1, "sp1", AirTime.FromDate(2015, 6, 1), true, "BTS Clip"),
        };
        var entry2 = new RemoteCatalog("AniDB", "AniDB", "600", true, eps2, Name: "Cour Two");
        ani.Entries["500"] = Ok(entry1, "600");
        ani.Entries["600"] = Ok(entry2);
        lib.Series.Add(Anime("Cour One", "500", OA(1, 1, "a1"), OA(1, 2, "a2")));

        var report = await svc.ScanAsync(Settings(specials: true), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);

        var reg = s.Missing.Single(m => m.SourceEpisodeId == "c1");
        Assert.False(reg.IsSpecial);
        Assert.Equal("Cour Two", reg.EntryName);
        Assert.Equal(3, reg.AbsoluteNumber);

        var sp = s.Missing.Single(m => m.SourceEpisodeId == "sp1");
        Assert.True(sp.IsSpecial);
        Assert.Equal("Cour Two", sp.EntryName);
    }
}
