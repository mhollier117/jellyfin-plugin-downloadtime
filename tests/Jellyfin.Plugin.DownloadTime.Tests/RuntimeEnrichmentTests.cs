// TMDB runtime enrichment for runtime-less lanes (2026-08-05).
// The TVDB all-seasons page carries no runtime, so 499 of 770 surviving
// special claims had NO signal at all. Most series carry a Tmdb id next to
// their Tvdb id, and TMDB's season-0 endpoint reports per-episode runtime.
//
// Enrichment is a SIDECAR: the routed catalog still drives detection. We only
// attach RuntimeMinutes (and an alternate title for pattern matching) to items
// we can match CONSERVATIVELY - unique by exact air date or by normalized
// title. Ambiguity enriches nothing (ambiguity = no signal = stays Special).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class RuntimeEnrichmentTests
{
    private static DateTimeOffset D(int y, int m, int d) => AirTime.FromDate(y, m, d);

    private static RemoteEpisode Tvdb(int number, string? title, DateTimeOffset? aired)
        => new(0, number, "t" + number, aired, true, title);

    private static RemoteEpisode Tmdb(int number, string? title, DateTimeOffset? aired, int? runtime)
        => new(0, number, null, aired, true, title, RuntimeMinutes: runtime);

    private static RemoteCatalog Catalog(params RemoteEpisode[] eps)
        => new("Tvdb", "Tvdb", "153021", true, eps.Prepend(
            new RemoteEpisode(1, 1, "r1", D(2010, 10, 31), false, "Days Gone Bye")).ToArray());

    [Fact]
    public void UniqueDateMatch_AttachesRuntime()
    {
        var cat = Catalog(Tvdb(1, "Inside the Walking Dead", D(2010, 10, 11)));
        var tmdb = new[] { Tmdb(1, "A Sneak Peek with Robert Kirkman", D(2010, 10, 11), 4) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(1, result.Matched);
        Assert.Equal(4, result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1").RuntimeMinutes);
    }

    [Fact]
    public void UniqueTitleMatch_AttachesRuntime_EvenWhenDatesDiffer()
    {
        var cat = Catalog(Tvdb(1, "The Making of The Walking Dead", D(2010, 10, 31)));
        var tmdb = new[] { Tmdb(9, "the making of the walking dead", D(2012, 1, 1), 22) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(1, result.Matched);
        Assert.Equal(22, result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1").RuntimeMinutes);
    }

    [Fact]
    public void TitleMatch_IgnoresBracketMarkers_ViaTitleKey()
    {
        var cat = Catalog(Tvdb(1, "[C/F] Behind the Curtain", null));
        var tmdb = new[] { Tmdb(3, "Behind the Curtain", null, 6) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(6, result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1").RuntimeMinutes);
    }

    [Fact]
    public void AmbiguousDate_EnrichesNothing()
    {
        // two TMDB extras released the same day and no title agreement
        var cat = Catalog(Tvdb(1, "Some Featurette", D(2015, 5, 1)));
        var tmdb = new[]
        {
            Tmdb(1, "Alpha", D(2015, 5, 1), 3),
            Tmdb(2, "Beta", D(2015, 5, 1), 44),
        };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Ambiguous);
        Assert.Null(result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1").RuntimeMinutes);
    }

    [Fact]
    public void AmbiguousTitle_EnrichesNothing()
    {
        var cat = Catalog(Tvdb(1, "Recap", D(2015, 5, 1)));
        var tmdb = new[]
        {
            Tmdb(1, "Recap", D(2016, 1, 1), 3),
            Tmdb(2, "recap", D(2017, 1, 1), 44),
        };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Ambiguous);
    }

    [Fact]
    public void DateAndTitleAgreeOnSameItem_IsNotAmbiguous()
    {
        var cat = Catalog(Tvdb(1, "Sneak Peek", D(2015, 5, 1)));
        var tmdb = new[] { Tmdb(1, "Sneak Peek", D(2015, 5, 1), 5), Tmdb(2, "Other", D(2018, 1, 1), 50) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(1, result.Matched);
        Assert.Equal(5, result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1").RuntimeMinutes);
    }

    [Fact]
    public void NoMatch_LeavesItemUntouched_AndCounted()
    {
        var cat = Catalog(Tvdb(1, "Nothing Like It", D(2015, 5, 1)));
        var tmdb = new[] { Tmdb(1, "Unrelated", D(2019, 9, 9), 5) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Unmatched);
        Assert.Null(result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1").RuntimeMinutes);
    }

    [Fact]
    public void RegularEpisodes_AreNeverEnriched()
    {
        // Only season-0 content is classified; enrichment must not touch the
        // detection axis for regular episodes.
        var cat = Catalog(Tvdb(1, "A Special", D(2010, 10, 31)));
        var tmdb = new[] { Tmdb(1, "Days Gone Bye", D(2010, 10, 31), 67) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Null(result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "r1").RuntimeMinutes);
    }

    [Fact]
    public void EnrichmentNeverAddsRemovesOrRenumbersClaims()
    {
        var cat = Catalog(Tvdb(1, "One", D(2015, 5, 1)), Tvdb(2, "Two", D(2015, 5, 2)));
        var tmdb = new[] { Tmdb(1, "One", D(2015, 5, 1), 5), Tmdb(7, "Extra Item", D(2020, 1, 1), 9) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(cat.Episodes.Count, result.Catalog.Episodes.Count);
        Assert.Equal(
            cat.Episodes.Select(e => (e.Season, e.Number, e.SourceEpisodeId, e.Title, e.AiredAt)),
            result.Catalog.Episodes.Select(e => (e.Season, e.Number, e.SourceEpisodeId, e.Title, e.AiredAt)));
    }

    [Fact]
    public void ExistingRuntime_IsNotOverwritten()
    {
        var cat = new RemoteCatalog("Tvdb", "Tvdb", "1", true, new[]
        {
            new RemoteEpisode(0, 1, "t1", D(2015, 5, 1), true, "Has Runtime", RuntimeMinutes: 30),
        });
        var tmdb = new[] { Tmdb(1, "Has Runtime", D(2015, 5, 1), 3) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        Assert.Equal(30, result.Catalog.Episodes.Single().RuntimeMinutes);
    }

    // ------------------------------------------------- ScanService wiring --

    private sealed class FakeLibrary : ILibraryReader
    {
        public List<SeriesItemInfo> Series { get; } = new();
        public IReadOnlyList<SeriesItemInfo> GetSeries() => Series;
        public IReadOnlyList<MovieItemInfo> GetMovies() => Array.Empty<MovieItemInfo>();
    }

    private sealed class StubTvdb : Services.Lanes.ITvdbSource
    {
        private readonly RemoteCatalog _cat;
        public StubTvdb(RemoteCatalog cat) => _cat = cat;
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Ok(_cat));
    }

    private sealed class DeadTvmaze : Services.Lanes.ITvmazeSource
    {
        public Task<FetchOutcome> FetchByTvdbIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
        public Task<FetchOutcome> FetchByImdbIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
    }

    private sealed class DeadAniDb : Services.Lanes.IAniDbSource
    {
        public Task<FetchOutcome> FetchByAnimeIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
        public Task<Services.Lanes.AniDbEntryOutcome> FetchEntryAsync(string id, CancellationToken ct)
            => Task.FromResult(new Services.Lanes.AniDbEntryOutcome(null, Array.Empty<string>(), "unused"));
    }

    private sealed class SeasonStubTmdb : Services.Lanes.ITmdbSource
    {
        private readonly IReadOnlyList<RemoteEpisode> _season0;
        public SeasonStubTmdb(IReadOnlyList<RemoteEpisode> season0) => _season0 = season0;
        public List<(string Id, int Season)> Calls { get; } = new();
        public Task<FetchOutcome> FetchSeriesAsync(string id, CancellationToken ct)
            => throw new InvalidOperationException("enrichment must not drive detection");
        public Task<Services.Lanes.CollectionOutcome> FetchCollectionForMovieAsync(int id, CancellationToken ct)
            => Task.FromResult(new Services.Lanes.CollectionOutcome(null, null, true));
        public Task<IReadOnlyList<RemoteEpisode>> FetchSeasonEpisodesAsync(string id, int season, CancellationToken ct)
        {
            Calls.Add((id, season));
            return Task.FromResult(_season0);
        }
    }

    private static async Task<(SeriesReportDto Report, SeasonStubTmdb Tmdb)> RunScanAsync(
        RemoteCatalog tvdbCatalog, IReadOnlyList<RemoteEpisode> tmdbSeason0, bool withTmdbId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dt-enr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ids = new Dictionary<string, string> { ["Tvdb"] = "153021" };
            if (withTmdbId) ids["Tmdb"] = "1402";
            var lib = new FakeLibrary();
            lib.Series.Add(new SeriesItemInfo(Guid.NewGuid(), "Show", @"D:\TV\Show", false, ids, Array.Empty<OwnedEpisode>()));
            var tmdb = new SeasonStubTmdb(tmdbSeason0);
            var clock = new Support.FakeClock(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
            var svc = new ScanService(lib, new StubTvdb(tvdbCatalog), new DeadTvmaze(), new DeadAniDb(), tmdb,
                new CatalogCache(dir, clock), clock);
            var settings = new ScanSettings(true, true, false, 24, true, 90, new HashSet<string>(),
                TimeSpan.FromDays(1), TimeSpan.FromDays(7), false, ContentClassifier.DefaultExtraPatterns, 15);
            var report = await svc.ScanAsync(settings, true, null, CancellationToken.None);
            return (Assert.Single(report.Series), tmdb);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Scan_TvdbLaneWithTmdbId_EnrichesAndHidesShortExtras()
    {
        var cat = Catalog(
            Tvdb(1, "Making the Monsters", D(2015, 5, 1)),   // 4 min on TMDB -> Extra
            Tvdb(2, "The Christmas Special", D(2015, 12, 25))); // 60 min on TMDB -> stays Special
        var s0 = new[]
        {
            Tmdb(1, "Making the Monsters", D(2015, 5, 1), 4),
            Tmdb(2, "The Christmas Special", D(2015, 12, 25), 60),
        };
        var (report, tmdb) = await RunScanAsync(cat, s0, withTmdbId: true);
        Assert.Equal(("1402", 0), tmdb.Calls.Single());               // exactly one enrichment call
        Assert.DoesNotContain(report.Missing, m => m.SourceEpisodeId == "t1");  // extra hidden
        var kept = report.Missing.Single(m => m.SourceEpisodeId == "t2");
        Assert.Equal(nameof(ContentKind.Special), kept.Classification);
        Assert.Equal(60, kept.RuntimeMinutes);
        Assert.Contains(report.Missing, m => m.SourceEpisodeId == "r1"); // regular episode untouched
    }

    [Fact]
    public async Task Scan_NoTmdbId_NotesTheGap_AndChangesNothing()
    {
        var cat = Catalog(Tvdb(1, "Making the Monsters", D(2015, 5, 1)));
        var (report, tmdb) = await RunScanAsync(cat, Array.Empty<RemoteEpisode>(), withTmdbId: false);
        Assert.Empty(tmdb.Calls);
        Assert.Contains(report.Missing, m => m.SourceEpisodeId == "t1");
        Assert.Contains(report.Notes, n => n.Contains("no TMDB id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scan_TmdbHasNoSeasonZero_NotesTheGap()
    {
        var cat = Catalog(Tvdb(1, "Making the Monsters", D(2015, 5, 1)));
        var (report, _) = await RunScanAsync(cat, Array.Empty<RemoteEpisode>(), withTmdbId: true);
        Assert.Contains(report.Missing, m => m.SourceEpisodeId == "t1");
        Assert.Contains(report.Notes, n => n.Contains("no season 0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AlternateTitle_IsAttached_ForPatternMatching()
    {
        // TVDB title is opaque, TMDB's names it as an extra -> classifier can
        // use the alternate title even though runtime alone would not decide.
        var cat = Catalog(Tvdb(1, "Episode 104", D(2015, 5, 1)));
        var tmdb = new[] { Tmdb(1, "Behind the Scenes: Building the Set", D(2015, 5, 1), 40) };
        var result = RuntimeEnricher.Enrich(cat, tmdb);
        var enriched = result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1");
        Assert.Equal("Behind the Scenes: Building the Set", enriched.AltTitle);
        Assert.Equal(ContentKind.Extra,
            ContentClassifier.Classify(enriched, new ClassifierOptions(ContentClassifier.DefaultExtraPatterns, 15)));
    }
}
