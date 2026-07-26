// Adversarial verification suite for the AniDB entry-chain rework (analysis
// doc 2026-07-26, commits 37011e7..fc976e4). Scenarios:
//  (a) merged local whose absolute numbering COUNTS a special (union counts
//      regulars only): id detection exact; interior placement lands in the
//      local scheme; anchors straddling the offset -> conservative null.
//  (b) middle-entry fetch failure: union truncates at the failure (no
//      absolute-axis lies); downstream owned episodes become "unknown to the
//      source", never false-missing; partial note names the failed entry.
//  (c) Sequel relation pointing at a Movie-type entry: the movie must NOT
//      occupy an absolute slot or an entry ordinal (merged id-less, split
//      id-less, and merged id-bearing layouts all stay gap-free).
//  (d) duplicate sequel ids + A->B->A cycle: each entry fetched once, no
//      double counting, detection exact.
//  (e) specials-only entry mid-chain: absolute axis continuous across it and
//      it consumes NO entry ordinal (split id-less layouts stay aligned).
//  (f) id-less split layout whose local season label collides with a
//      different entry's ordinal: quantified residual (tuple OR masks the
//      skipped cour, mislabeled cour reports missing) - documented gap.
//  (g) merged library owning nothing from a whole middle cour: placement
//      anchors interpolate/extrapolate across the gap correctly.
//  (h) pre-1.3 cached JSON: old anidbid-* cache file is ignored by the chain
//      walk (no crash, fresh fetch); legacy RemoteCatalog JSON without
//      AbsoluteNumber/SynthesizedSeasons deserializes with safe defaults.
//  (i) IncludeSpecials=true against a union: id-bearing specials match;
//      id-less local S0 specials match union specials by epno (synthesized
//      catalogs only); unowned specials are reported but unplaceable.
//  (j) fail-safe when locals DO carry AniDB ids but none match the chain
//      (wrong anidbid tag): suppress the all-missing explosion with a note;
//      a single correct id keeps normal reporting.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AdversarialAnimeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-adv-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DiffOptions Opts(bool specials = false) => new(Now, 24, specials);
    private static DateTimeOffset D(int y, int m, int d) => AirTime.FromDate(y, m, d);

    private static RemoteEpisode R(int season, int number, string id, int? abs, DateTimeOffset? aired, bool special = false)
        => new(season, number, id, aired, special, null, abs);

    private static OwnedEpisode O(int? s, int? n, string? id = null) => new(
        s, n, null,
        id is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["AniDB"] = id },
        null);

    private static RemoteCatalog Synth(params RemoteEpisode[] eps)
        => new("AniDB", "AniDB", "18164", true, eps, SynthesizedSeasons: true);

    // ---------------------------------------------------------------- (a) --

    /// <summary>Union for (a): entry1 = a1..a12 (abs 1-12) + special sp1; entry2 = b1..b12 (abs 13-24).</summary>
    private static RemoteCatalog UnionWithSpecialOffset()
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 12; i++) eps.Add(R(1, i, "a" + i, i, D(2024, 1, i)));
        eps.Add(R(1, 1, "sp1", null, D(2024, 1, 15), special: true));
        for (var i = 1; i <= 12; i++) eps.Add(R(2, i, "b" + i, 12 + i, D(2025, 1, i)));
        return Synth(eps.ToArray());
    }

    /// <summary>Merged local that counted the special: a_i at S1E_i, sp1 at S1E13, b_i at S1E(13+i).</summary>
    private static List<OwnedEpisode> MergedCountingSpecial(params string[] excludeIds)
    {
        var owned = new List<OwnedEpisode>();
        for (var i = 1; i <= 12; i++) owned.Add(O(1, i, "a" + i));
        owned.Add(O(1, 13, "sp1"));
        for (var i = 1; i <= 12; i++) owned.Add(O(1, 13 + i, "b" + i));
        return owned.Where(o => !excludeIds.Contains(o.ProviderIds["AniDB"])).ToList();
    }

    [Fact]
    public void A_MergedCountsSpecial_IdDetectionExact()
    {
        var diff = DiffEngine.Diff(MergedCountingSpecial("b5"), UnionWithSpecialOffset(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("b5", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void A_MergedCountsSpecial_InteriorPlacement_LandsInLocalScheme()
    {
        // b5 is abs 17; local scheme (special counted at E13) has it at S1E18.
        var union = UnionWithSpecialOffset();
        var owned = MergedCountingSpecial("b5");
        var b5 = union.Episodes.Single(e => e.SourceEpisodeId == "b5");
        Assert.Equal(new Placement(1, 18), Placer.Infer(b5, owned, union));
    }

    [Fact]
    public void A_MergedCountsSpecial_AnchorsStraddleOffset_ConservativeNull()
    {
        // Only anchors available straddle the local +1 offset (a12 -> S1E12,
        // b2 -> S1E15); spacing disagrees -> must refuse rather than misplace.
        var union = UnionWithSpecialOffset();
        var owned = new List<OwnedEpisode> { O(1, 12, "a12"), O(1, 13, "sp1"), O(1, 15, "b2") };
        var b1 = union.Episodes.Single(e => e.SourceEpisodeId == "b1");
        Assert.Null(Placer.Infer(b1, owned, union));
    }

    // ---------------------------------------------------------------- (b) --

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

    private static ScanSettings Settings() => new(true, true, true, 24, false, 90,
        new HashSet<string>(), TimeSpan.FromDays(1), TimeSpan.FromDays(7));

    private static SeriesItemInfo AnimeSeries(params OwnedEpisode[] eps)
        => new(Guid.NewGuid(), "Adversarial", @"D:\Anime\Adversarial [anidbid-18164]", true,
            new Dictionary<string, string> { ["AniDB"] = "18164" }, eps);

    private (ScanService Svc, FakeLibrary Lib, FakeChainAniDb Ani) Make()
    {
        var lib = new FakeLibrary();
        var ani = new FakeChainAniDb();
        var clock = new FakeClock(Now);
        var svc = new ScanService(lib, new DeadTvdb(), new DeadTvmaze(), ani, new DeadTmdb(), new CatalogCache(_dir, clock), clock);
        return (svc, lib, ani);
    }

    [Fact]
    public async Task B_MiddleEntryFails_UnionTruncated_NoAxisLies()
    {
        var (svc, lib, ani) = Make();
        ani.Entries["18164"] = new AniDbEntryOutcome(Entry("18164", 4, "a", 2024), new[] { "18800" }, null);
        // 18800 NOT seeded -> fetch fails; 18900 is only reachable through it.
        ani.Entries["18900"] = new AniDbEntryOutcome(Entry("18900", 2, "c", 2026), Array.Empty<string>(), null);
        lib.Series.Add(AnimeSeries(O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "a3"), O(1, 4, "a4"), O(1, 8, "c1")));

        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        // Truncated, not skipped: 18900 must never be fetched (its position in
        // the axis is unknowable without 18800), so no misnumbered axis exists.
        Assert.Equal(new[] { "18164", "18800" }, ani.Fetched.ToArray());
        // Nothing false-missing; owned c1 degrades to a stray, with both notes.
        Assert.Empty(s.Missing);
        Assert.Contains(s.Notes, n => n.Contains("18800") && n.Contains("partial", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(s.Notes, n => n.Contains("unknown to the source", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- (d) --

    [Fact]
    public async Task D_DuplicateSequelIds_SelfReference_Cycle_NoDoubleCount()
    {
        var (svc, lib, ani) = Make();
        ani.Entries["18164"] = new AniDbEntryOutcome(Entry("18164", 2, "a", 2024), new[] { "18800", "18800", "18164" }, null);
        ani.Entries["18800"] = new AniDbEntryOutcome(Entry("18800", 2, "b", 2025), new[] { "18164" }, null);
        lib.Series.Add(AnimeSeries(O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "b1")));

        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        Assert.Equal(new[] { "18164", "18800" }, ani.Fetched.ToArray()); // each entry exactly once
        Assert.Equal("AniDB (2 entries)", s.Lane);
        var m = Assert.Single(s.Missing); // b2 only - duplicates did not clone episodes
        Assert.Equal("b2", m.SourceEpisodeId);
    }

    // ---------------------------------------------------------------- (e) --

    [Fact]
    public void E_SpecialsOnlyEntry_MidChain_AxisContinuous_NoOrdinalConsumed()
    {
        var e1 = new RemoteCatalog("AniDB", "AniDB", "100", true, new[]
        {
            new RemoteEpisode(null, 1, "r1", D(2024, 1, 1), false, null),
            new RemoteEpisode(null, 2, "r2", D(2024, 1, 8), false, null),
        });
        var eS = new RemoteCatalog("AniDB", "AniDB", "200", true, new[]
        {
            new RemoteEpisode(null, 1, "s1", D(2024, 6, 1), true, null),
            new RemoteEpisode(null, 2, "s2", D(2024, 6, 2), true, null),
        });
        var e3 = new RemoteCatalog("AniDB", "AniDB", "300", true, new[]
        {
            new RemoteEpisode(null, 1, "t1", D(2025, 1, 5), false, null),
            new RemoteEpisode(null, 2, "t2", D(2025, 1, 12), false, null),
        });
        var union = AniDbChain.BuildUnion(new[] { e1, eS, e3 });

        var t1 = union.Episodes.Single(e => e.SourceEpisodeId == "t1");
        var t2 = union.Episodes.Single(e => e.SourceEpisodeId == "t2");
        var s1 = union.Episodes.Single(e => e.SourceEpisodeId == "s1");
        Assert.Equal(3, t1.AbsoluteNumber);  // axis continuous across the specials entry
        Assert.Equal(4, t2.AbsoluteNumber);
        Assert.Null(s1.AbsoluteNumber);
        // A specials-only entry is not a local season: it must not consume an
        // entry ordinal, or id-less split layouts shift by one cour.
        Assert.Equal(2, t1.Season);
        Assert.Equal(1, s1.Season); // specials attach to the preceding regular-bearing cour

        // id-less split local (S1 = entry1, S2 = entry3): nothing is missing.
        var owned = new[] { O(1, 1), O(1, 2), O(2, 1), O(2, 2) };
        Assert.Empty(DiffEngine.Diff(owned, union, Opts()).Missing);
    }

    // ---------------------------------------------------------------- (c) --

    private const string XmlTvA = """
        <anime id="100" restricted="false">
          <type>TV Series</type>
          <episodecount>2</episodecount>
          <startdate>2024-01-07</startdate>
          <enddate>2024-01-14</enddate>
          <titles><title xml:lang="en" type="main">Cour One</title></titles>
          <relatedanime><anime id="200" type="Sequel">The Movie</anime></relatedanime>
          <episodes>
            <episode id="a1"><epno type="1">1</epno><airdate>2024-01-07</airdate></episode>
            <episode id="a2"><epno type="1">2</epno><airdate>2024-01-14</airdate></episode>
          </episodes>
        </anime>
        """;

    private const string XmlMovie = """
        <anime id="200" restricted="false">
          <type>Movie</type>
          <episodecount>1</episodecount>
          <startdate>2024-06-01</startdate>
          <enddate>2024-06-01</enddate>
          <titles><title xml:lang="en" type="main">The Movie</title></titles>
          <relatedanime><anime id="300" type="Sequel">Cour Two</anime></relatedanime>
          <episodes>
            <episode id="m1"><epno type="1">1</epno><airdate>2024-06-01</airdate></episode>
          </episodes>
        </anime>
        """;

    private const string XmlTvB = """
        <anime id="300" restricted="false">
          <type>TV Series</type>
          <episodecount>2</episodecount>
          <startdate>2025-01-05</startdate>
          <enddate>2025-01-12</enddate>
          <titles><title xml:lang="en" type="main">Cour Two</title></titles>
          <episodes>
            <episode id="b1"><epno type="1">1</epno><airdate>2025-01-05</airdate></episode>
            <episode id="b2"><epno type="1">2</epno><airdate>2025-01-12</airdate></episode>
          </episodes>
        </anime>
        """;

    private static RemoteCatalog MovieChainUnion()
    {
        var clock = new FakeClock(Now);
        var a = AniDbFetcher.ParseEntry(XmlTvA, clock).Catalog!;
        var m = AniDbFetcher.ParseEntry(XmlMovie, clock).Catalog!;
        var b = AniDbFetcher.ParseEntry(XmlTvB, clock).Catalog!;
        return AniDbChain.BuildUnion(new[] { a, m, b });
    }

    [Fact]
    public void C_MovieSequelEntry_MergedIdless_NoFalseMissing()
    {
        // Local merged library has the 4 TV episodes as S1E1..4; the movie is
        // (correctly) not in the show folder. A movie in the Sequel chain must
        // not occupy an absolute slot - otherwise the final cour shifts by one
        // and the last owned episode is reported missing.
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4) };
        var diff = DiffEngine.Diff(owned, MovieChainUnion(), Opts());
        Assert.Empty(diff.Missing);
    }

    [Fact]
    public void C_MovieSequelEntry_SplitIdless_NoFalseMissing()
    {
        // Split local: S1 = cour one, S2 = cour two. The movie entry must not
        // consume entry ordinal 2, otherwise S2 never tuple-matches cour two.
        var owned = new[] { O(1, 1), O(1, 2), O(2, 1), O(2, 2) };
        var diff = DiffEngine.Diff(owned, MovieChainUnion(), Opts());
        Assert.Empty(diff.Missing);
    }

    [Fact]
    public void C_MovieSequelEntry_MergedIds_MovieNotReportedAsMissingEpisode()
    {
        // Id-bearing merged locals owning every TV episode: the unowned movie
        // must not surface as a missing "episode" (IncludeSpecials=false).
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "b1"), O(1, 4, "b2") };
        var diff = DiffEngine.Diff(owned, MovieChainUnion(), Opts());
        Assert.Empty(diff.Missing);
    }

    // ---------------------------------------------------------------- (f) --

    /// <summary>Union for (f)/(g): A = a1..a4 (S1, abs 1-4), B = b1..b3 (S2, abs 5-7), C = c1..c2 (S3, abs 8-9).</summary>
    private static RemoteCatalog ThreeEntryUnion()
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 4; i++) eps.Add(R(1, i, "a" + i, i, D(2024, 1, i)));
        for (var i = 1; i <= 3; i++) eps.Add(R(2, i, "b" + i, 4 + i, D(2025, 1, i)));
        for (var i = 1; i <= 2; i++) eps.Add(R(3, i, "c" + i, 7 + i, D(2026, 1, i)));
        return Synth(eps.ToArray());
    }

    [Fact]
    public void F_IdlessSplit_LocalSeasonLabelCollidesWithWrongEntry_ResidualDocumented()
    {
        // DOCUMENTED DESIGN GAP (analysis doc "residual known gaps"): the user
        // skipped cour B entirely and hand-labeled cour C as "Season 2".
        // Ground truth: b1..b3 missing, c1..c2 owned. The conservative tuple
        // OR lets local S2E1/S2E2 falsely own b1/b2 (masked), while owned
        // c1/c2 are false-missing. Detection is WRONG in both directions here;
        // the fallback note is the only breadcrumb. Pinned so any silent
        // behavior change is noticed. Requires id-less files AND a local
        // season label that contradicts the franchise's canonical order.
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4), O(2, 1), O(2, 2) };
        var diff = DiffEngine.Diff(owned, ThreeEntryUnion(), Opts());
        Assert.Equal(new[] { "b3", "c1", "c2" },
            diff.Missing.Select(m => m.Episode.SourceEpisodeId).OrderBy(x => x).ToArray());
        Assert.Contains(diff.Notes, n => n.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- (g) --

    [Fact]
    public void G_MergedLibrary_OwnsNothingFromMiddleCour_PlacementBridgesGap()
    {
        var union = ThreeEntryUnion();
        // Merged locals: entry1 at S1E1..4 (ids), nothing from B, c1 at S1E8.
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "a3"), O(1, 4, "a4"), O(1, 8, "c1") };

        // Detection: every B episode + c2 missing.
        var diff = DiffEngine.Diff(owned, union, Opts());
        Assert.Equal(new[] { "b1", "b2", "b3", "c2" },
            diff.Missing.Select(m => m.Episode.SourceEpisodeId).OrderBy(x => x).ToArray());

        // Placement: interpolation across the whole-cour anchor gap is exact.
        var b2 = union.Episodes.Single(e => e.SourceEpisodeId == "b2");
        var c2 = union.Episodes.Single(e => e.SourceEpisodeId == "c2");
        Assert.Equal(new Placement(1, 6), Placer.Infer(b2, owned, union));
        Assert.Equal(new Placement(1, 9), Placer.Infer(c2, owned, union));
    }

    // ---------------------------------------------------------------- (h) --

    [Fact]
    public async Task H_Pre13AnimeCacheFile_OldKeyShape_IgnoredWithoutCrash()
    {
        var (svc, lib, ani) = Make();
        // v1.2 cached the single-entry catalog under "anidbid-18164" WITHOUT
        // AbsoluteNumber/SynthesizedSeasons. Plant a fresh (in-TTL) relic.
        var legacy = """
            {"FetchedAt":"2026-07-26T11:00:00+00:00","Payload":{"SourceKey":"AniDB","IdProviderKey":"AniDB","SeriesSourceId":"18164","IsEnded":true,"Episodes":[{"Season":null,"Number":1,"SourceEpisodeId":"a1","AiredAt":"2024-01-07T23:59:00+00:00","IsSpecial":false,"Title":"stale"}]}}
            """;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "anidbid-18164.json"), legacy);

        ani.Entries["18164"] = new AniDbEntryOutcome(Entry("18164", 2, "a", 2024), Array.Empty<string>(), null);
        lib.Series.Add(AnimeSeries(O(1, 1, "a1")));

        var report = await svc.ScanAsync(Settings(), false, null, CancellationToken.None);
        var s = Assert.Single(report.Series);
        Assert.Null(s.Error);
        Assert.Equal(new[] { "18164" }, ani.Fetched.ToArray()); // relic ignored, chain fetched fresh
        var m = Assert.Single(s.Missing);                       // a2 from the FRESH catalog, not the 1-ep relic
        Assert.Equal("a2", m.SourceEpisodeId);
    }

    [Fact]
    public void H_LegacyRemoteCatalogJson_MissingNewMembers_DeserializesWithDefaults()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        var legacy = """
            {"FetchedAt":"2026-07-26T11:00:00+00:00","Payload":{"SourceKey":"AniDB","IdProviderKey":"AniDB","SeriesSourceId":"18164","IsEnded":true,"Episodes":[{"Season":null,"Number":1,"SourceEpisodeId":"274088","AiredAt":"2024-01-07T23:59:00+00:00","IsSpecial":false,"Title":"Episode 1"}]}}
            """;
        File.WriteAllText(Path.Combine(_dir, "legacy-cat.json"), legacy);

        var cat = cache.TryGet<RemoteCatalog>("legacy-cat", TimeSpan.FromDays(7));
        Assert.NotNull(cat);
        Assert.False(cat!.SynthesizedSeasons);           // defaulted, not garbage
        var ep = Assert.Single(cat.Episodes);
        Assert.Null(ep.AbsoluteNumber);                  // defaulted
        Assert.Equal("274088", ep.SourceEpisodeId);
    }

    // ---------------------------------------------------------------- (i) --

    private static RemoteCatalog UnionWithSpecial()
    {
        // Special epno 3 deliberately EXCEEDS the regular numbers so it cannot
        // be coincidentally tuple-owned by a regular local.
        return Synth(
            R(1, 1, "a1", 1, D(2024, 1, 7)),
            R(1, 2, "a2", 2, D(2024, 1, 14)),
            R(1, 3, "sp1", null, D(2024, 2, 1), special: true));
    }

    [Fact]
    public void I_IncludeSpecials_IdBearingLocalSpecial_Matches()
    {
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2"), O(0, 3, "sp1") };
        var diff = DiffEngine.Diff(owned, UnionWithSpecial(), Opts(specials: true));
        Assert.Empty(diff.Missing);
    }

    [Fact]
    public void I_IncludeSpecials_IdlessLocalSpecialInSeasonZero_NotFalseMissing()
    {
        // v1.2 catalogs were season-less, so an id-less local S0E3 matched the
        // special by epno. The union gives specials Season = entry ordinal;
        // local season 0 must still be able to own them (regression guard).
        var owned = new[] { O(1, 1), O(1, 2), O(0, 3) };
        var diff = DiffEngine.Diff(owned, UnionWithSpecial(), Opts(specials: true));
        Assert.Empty(diff.Missing);
        Assert.DoesNotContain(diff.Notes, n => n.Contains("unknown to the source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void I_IncludeSpecials_UnownedSpecial_ReportedButUnplaceable()
    {
        var union = UnionWithSpecial();
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2") };
        var diff = DiffEngine.Diff(owned, union, Opts(specials: true));
        var m = Assert.Single(diff.Missing);
        Assert.Equal("sp1", m.Episode.SourceEpisodeId);
        // No AbsoluteNumber -> no axis position -> placeholder skipped, coherently.
        Assert.Null(Placer.Infer(m.Episode, owned, union));
    }

    // ---------------------------------------------------------------- (j) --

    [Fact]
    public void J_WrongAniDbIds_ZeroMatches_FailSafeSuppressesAllMissing()
    {
        // Folder tagged with the WRONG anidbid: locals carry AniDB episode ids
        // of a different show. Zero matches must fail safe (like M7), not
        // report the entire franchise missing.
        var owned = new[] { O(1, 1, "x1"), O(1, 2, "x2"), O(1, 3, "x3") };
        var diff = DiffEngine.Diff(owned, ThreeEntryUnion(), Opts());
        Assert.Empty(diff.Missing);
        Assert.Contains(diff.Notes, n => n.Contains("fail-safe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void J_OneCorrectId_NormalReportingKept()
    {
        // A single genuine match proves identification; the rest report normally.
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "x2"), O(1, 3, "x3") };
        var diff = DiffEngine.Diff(owned, ThreeEntryUnion(), Opts());
        Assert.Equal(8, diff.Missing.Count); // a2..a4, b1..b3, c1..c2
        Assert.DoesNotContain(diff.Notes, n => n.Contains("fail-safe", StringComparison.OrdinalIgnoreCase));
    }
}
