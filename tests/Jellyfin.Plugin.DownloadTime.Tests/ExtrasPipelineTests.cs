// Extras feature wiring (2026-08-05): runtime/type capture in the lanes,
// classification on the report DTO, and default exclusion of extras.
using System.Net;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ExtrasPipelineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-ex-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ------------------------------------------------------- lane capture --

    [Fact]
    public void AniDb_CapturesRuntimeAndEpnoType_KeepsNonContentTypes()
    {
        // Types 3-6 are no longer dropped at parse: they are kept, flagged
        // special, and carry their type so the classifier can call them
        // Extra (and the report can optionally show them).
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <anime id="12661" restricted="false">
              <type>TV Series</type>
              <episodes>
                <episode id="1"><epno type="1">1</epno><length>25</length><airdate>2017-04-05</airdate><title xml:lang="en">Boruto Uzumaki</title></episode>
                <episode id="2"><epno type="2">S1</epno><length>7</length><airdate>2017-04-01</airdate><title xml:lang="en">Episode S1</title></episode>
                <episode id="3"><epno type="3">C1</epno><length>2</length><airdate>2017-04-05</airdate><title xml:lang="en">Opening 1</title></episode>
              </episodes>
            </anime>
            """;
        var (cat, err) = AniDbFetcher.ParseAnime(xml, new FakeClock(Now));
        Assert.Null(err);
        var byId = cat!.Episodes.ToDictionary(e => e.SourceEpisodeId!);
        Assert.Equal(new[] { "1", "2", "3" }, byId.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(25, byId["1"].RuntimeMinutes);
        Assert.Equal("1", byId["1"].SourceTypeCode);
        Assert.False(byId["1"].IsSpecial);
        Assert.Equal("2", byId["2"].SourceTypeCode);
        Assert.True(byId["2"].IsSpecial);
        Assert.Equal("3", byId["3"].SourceTypeCode);
        Assert.True(byId["3"].IsSpecial);   // never a regular episode
        Assert.Equal(2, byId["3"].RuntimeMinutes);
    }

    [Fact]
    public void AniDb_MissingLength_LeavesRuntimeNull()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <anime id="1"><type>TV Series</type><episodes>
              <episode id="9"><epno type="1">1</epno><title xml:lang="en">No length</title></episode>
            </episodes></anime>
            """;
        var (cat, _) = AniDbFetcher.ParseAnime(xml, new FakeClock(Now));
        Assert.Null(Assert.Single(cat!.Episodes).RuntimeMinutes);
    }

    [Fact]
    public async Task Tmdb_CapturesEpisodeRuntime()
    {
        const string tv = """{"id":7,"status":"Ended","seasons":[{"season_number":0},{"season_number":1}]}""";
        const string s0 = """
            {"episodes":[
              {"season_number":0,"episode_number":1,"air_date":"2021-01-01","name":"Under the Knife","runtime":11},
              {"season_number":0,"episode_number":2,"air_date":"2021-01-02","name":"Mystery","runtime":null}]}
            """;
        const string s1 = """{"episodes":[{"season_number":1,"episode_number":1,"air_date":"2021-02-01","name":"e1","runtime":42}]}""";
        var handler = new FakeHttpHandler(uri => uri.PathAndQuery switch
        {
            var p when p.StartsWith("/3/tv/7/season/0") => FakeHttpHandler.Json(s0),
            var p when p.StartsWith("/3/tv/7/season/1") => FakeHttpHandler.Json(s1),
            var p when p.StartsWith("/3/tv/7") => FakeHttpHandler.Json(tv),
            _ => FakeHttpHandler.Status(HttpStatusCode.NotFound),
        });
        var fetcher = new TmdbFetcher(new HttpClient(handler), () => "k", _ => Task.CompletedTask);
        var outcome = await fetcher.FetchSeriesAsync("7", CancellationToken.None);
        Assert.Null(outcome.Error);
        var eps = outcome.Catalog!.Episodes;
        Assert.Equal(11, eps.Single(e => e.Season == 0 && e.Number == 1).RuntimeMinutes);
        Assert.Null(eps.Single(e => e.Season == 0 && e.Number == 2).RuntimeMinutes);
        Assert.Equal(42, eps.Single(e => e.Season == 1).RuntimeMinutes);
    }

    // --------------------------------------------------------- config ------

    [Fact]
    public void Configuration_ExtrasDefaults()
    {
        var c = new PluginConfiguration();
        Assert.False(c.ReportExtras);
        Assert.Equal(15, c.ExtraRuntimeThresholdMinutes);
        Assert.NotEmpty(c.ExtraTitlePatterns);
        Assert.Contains(c.ExtraTitlePatterns, p => p.Contains("behind the scenes", StringComparison.OrdinalIgnoreCase));
    }

    // --------------------------------------------------------- reporting ---

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

    private sealed class DeadAniDb : IAniDbSource
    {
        public Task<FetchOutcome> FetchByAnimeIdAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Fail("unused"));
        public Task<AniDbEntryOutcome> FetchEntryAsync(string id, CancellationToken ct) => Task.FromResult(new AniDbEntryOutcome(null, Array.Empty<string>(), "unused"));
    }

    private sealed class StubTmdb : ITmdbSource
    {
        private readonly RemoteCatalog _cat;
        public StubTmdb(RemoteCatalog cat) => _cat = cat;
        public Task<FetchOutcome> FetchSeriesAsync(string id, CancellationToken ct) => Task.FromResult(FetchOutcome.Ok(_cat));
        public Task<CollectionOutcome> FetchCollectionForMovieAsync(int id, CancellationToken ct) => Task.FromResult(new CollectionOutcome(null, null, true));
    }

    private static ScanSettings Settings(bool reportExtras) => new(
        true, true, false, 24, true, 90, new HashSet<string>(),
        TimeSpan.FromDays(1), TimeSpan.FromDays(7),
        reportExtras, ContentClassifier.DefaultExtraPatterns, 15);

    private async Task<SeriesReportDto> RunAsync(bool reportExtras)
    {
        var eps = new[]
        {
            new RemoteEpisode(1, 1, "e1", AirTime.FromDate(2024, 1, 1), false, "Pilot", null, null, 42, null),
            new RemoteEpisode(0, 1, "s1", AirTime.FromDate(2024, 2, 1), true, "The Christmas Invasion", null, null, 60, null),
            new RemoteEpisode(0, 2, "x1", AirTime.FromDate(2024, 3, 1), true, "Behind the Scenes", null, null, 45, null),
        };
        var cat = new RemoteCatalog("Tmdb", null, "7", true, eps);
        var lib = new FakeLibrary();
        lib.Series.Add(new SeriesItemInfo(Guid.NewGuid(), "Show", @"D:\TV\Show", false,
            new Dictionary<string, string> { ["Tmdb"] = "7" }, Array.Empty<OwnedEpisode>()));
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, new DeadTvdb(), new DeadTvmaze(), new DeadAniDb(), new StubTmdb(cat),
            new CatalogCache(_dir, clock), clock);
        var report = await svc.ScanAsync(Settings(reportExtras), true, null, CancellationToken.None);
        return Assert.Single(report.Series);
    }

    [Fact]
    public async Task ExtrasExcludedByDefault_EpisodesAndSpecialsRemain()
    {
        var s = await RunAsync(reportExtras: false);
        Assert.Equal(new[] { "e1", "s1" }, s.Missing.Select(m => m.SourceEpisodeId).OrderBy(x => x).ToArray());
        Assert.DoesNotContain(s.Missing, m => m.Classification == nameof(ContentKind.Extra));
    }

    [Fact]
    public async Task ReportExtrasEnabled_ExtrasAppear_LabelledAndCountedApart()
    {
        var s = await RunAsync(reportExtras: true);
        Assert.Equal(3, s.Missing.Count);
        var extra = s.Missing.Single(m => m.SourceEpisodeId == "x1");
        Assert.Equal(nameof(ContentKind.Extra), extra.Classification);
        Assert.True(extra.IsSpecial); // still a season-0 item; Classification is the new axis
        Assert.Equal(45, extra.RuntimeMinutes);
        Assert.Equal(nameof(ContentKind.Special), s.Missing.Single(m => m.SourceEpisodeId == "s1").Classification);
        Assert.Equal(nameof(ContentKind.Episode), s.Missing.Single(m => m.SourceEpisodeId == "e1").Classification);
    }

    [Fact]
    public async Task ExtrasExcluded_AlsoFromPlaceholderDiffs()
    {
        // Placeholders are built from ScanService.LastDiffs; an item hidden
        // from the report must not become a virtual episode either.
        var eps = new[]
        {
            new RemoteEpisode(1, 1, "e1", AirTime.FromDate(2024, 1, 1), false, "Pilot", null, null, 42, null),
            new RemoteEpisode(0, 1, "s1", AirTime.FromDate(2024, 2, 1), true, "The Christmas Invasion", null, null, 60, null),
            new RemoteEpisode(0, 2, "x1", AirTime.FromDate(2024, 3, 1), true, "Behind the Scenes", null, null, 45, null),
        };
        var cat = new RemoteCatalog("Tmdb", null, "7", true, eps);
        var lib = new FakeLibrary();
        lib.Series.Add(new SeriesItemInfo(Guid.NewGuid(), "Show", @"D:\TV\Show", false,
            new Dictionary<string, string> { ["Tmdb"] = "7" }, Array.Empty<OwnedEpisode>()));
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, new DeadTvdb(), new DeadTvmaze(), new DeadAniDb(), new StubTmdb(cat),
            new CatalogCache(_dir, clock), clock);
        await svc.ScanAsync(Settings(reportExtras: false), true, null, CancellationToken.None);
        var (diff, _) = Assert.Single(svc.LastDiffs).Value;
        Assert.DoesNotContain(diff.Missing, m => m.Episode.SourceEpisodeId == "x1");
        Assert.Contains(diff.Missing, m => m.Episode.SourceEpisodeId == "s1");
    }

    [Fact]
    public async Task Classification_DoesNotOverloadKind()
    {
        // `Kind` keeps its Gap/New semantics; classification is a separate axis.
        var s = await RunAsync(reportExtras: true);
        Assert.All(s.Missing, m => Assert.Contains(m.Kind, new[] { nameof(MissingKind.Gap), nameof(MissingKind.New) }));
    }
}
