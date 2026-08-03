// Specials-audit fixes (2026-08-03, DT 1.3.5.2):
// S-1 cache schema versioning: envelopes without the CURRENT SchemaVersion
//     are misses, so caches parsed by older code (pre-D8 demotion, pre-Name)
//     self-heal by refetching instead of serving stale semantics.
// S-2 TitleKey must ignore LEADING bracketed markers (AFM writes [C]/[AC]/
//     [F]/[C/F] prefixes on filler/canon-marked items) - live FP: local S0E8
//     "[C/F] Boruto: Naruto the Movie" failed to title-match its entry.
// S-3 synthesized specials carry an entry ORDINAL, not a season: they must
//     never be tuple-owned by same-ordinal regular numbering - only id,
//     the unique-epno S0 wildcard, or content (airdate/title) own them.
// S-4 the TVDB all-seasons page lists specials as "SPECIAL 0x<n>" under
//     "Additional Specials" - the SxxEyy label regex skipped every one.
// S-6 undated catalog specials cannot be audited by the aired rule - say so.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AuditSpecialsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-sp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DiffOptions Opts(bool specials = true) => new(Now, 24, specials);
    private static DateTimeOffset D(int y, int m, int d) => AirTime.FromDate(y, m, d);
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    // ---------------------------------------------------------------- S-1 --

    [Fact]
    public void Cache_EnvelopeWithoutSchemaVersion_IsMiss()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "old.json"),
            """{"FetchedAt":"2026-08-03T11:00:00+00:00","Payload":{"SourceKey":"AniDB","IdProviderKey":"AniDB","SeriesSourceId":"1","IsEnded":true,"Episodes":[]}}""");
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        Assert.Null(cache.TryGet<RemoteCatalog>("old", TimeSpan.FromDays(7)));
    }

    [Fact]
    public void Cache_EnvelopeWithOlderSchemaVersion_IsMiss()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "older.json"),
            """{"FetchedAt":"2026-08-03T11:00:00+00:00","SchemaVersion":1,"Payload":{"SourceKey":"AniDB","IdProviderKey":"AniDB","SeriesSourceId":"1","IsEnded":true,"Episodes":[]}}""");
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        Assert.Null(cache.TryGet<RemoteCatalog>("older", TimeSpan.FromDays(7)));
    }

    [Fact]
    public void Cache_StoreThenGet_RoundTripsAtCurrentSchema()
    {
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        var cat = new RemoteCatalog("AniDB", "AniDB", "1", true, Array.Empty<RemoteEpisode>());
        cache.Store("rt", cat);
        Assert.NotNull(cache.TryGet<RemoteCatalog>("rt", TimeSpan.FromDays(7)));
    }

    // ---------------------------------------------------------------- S-2 --

    private static OwnedEpisode S0(int n, string? title, DateTimeOffset? aired = null)
        => new(0, n, null, new Dictionary<string, string>(), aired, title);

    private static RemoteCatalog MovieUnion(params RemoteEpisode[] extra)
    {
        var eps = new List<RemoteEpisode>
        {
            new(1, 1, "r1", D(2015, 1, 1), false, null, 1),
        };
        eps.AddRange(extra);
        return new RemoteCatalog("AniDB", "AniDB", "4880", true, eps, SynthesizedSeasons: true);
    }

    [Fact]
    public void TitleMatch_LeadingBracketMarkers_Ignored()
    {
        // AFM prefix on the local file name must not defeat the title match.
        var union = MovieUnion(new RemoteEpisode(1, 1, "mv1", D(2015, 8, 7), true, "Complete Movie", null, "Boruto: Naruto the Movie"));
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(8, "[C/F] Boruto: Naruto the Movie"),
        };
        Assert.Empty(DiffEngine.Diff(owned, union, Opts()).Missing);
    }

    [Fact]
    public void TitleMatch_MultipleLeadingMarkers_Ignored()
    {
        var union = MovieUnion(new RemoteEpisode(1, 1, "mv1", D(2015, 8, 7), true, "Complete Movie", null, "Boruto: Naruto the Movie"));
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(8, "[AC] [F] Boruto: Naruto the Movie"),
        };
        Assert.Empty(DiffEngine.Diff(owned, union, Opts()).Missing);
    }

    [Fact]
    public void TitleMatch_MidTitleBrackets_Preserved()
    {
        // Only LEADING marker groups are stripped; interior brackets remain
        // part of the normalized title.
        var union = MovieUnion(new RemoteEpisode(1, 1, "mv1", D(2015, 8, 7), true, "Complete Movie", null, "Boruto: Naruto the Movie"));
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(8, "Boruto [C/F] Naruto the Movie extra"),
        };
        var m = Assert.Single(DiffEngine.Diff(owned, union, Opts()).Missing);
        Assert.Equal("mv1", m.Episode.SourceEpisodeId);
    }

    // ---------------------------------------------------------------- S-3 --

    [Fact]
    public void SynthesizedSpecial_NeverOwnedViaOrdinalTuple()
    {
        // Multi-episode local S1E2-E3 sits in tupleOwned; the chain's special
        // #2 (Season = entry ordinal 1) must NOT be owned by it - ordinals
        // are not seasons for specials.
        var union = MovieUnion(new RemoteEpisode(1, 2, "sp2", D(2015, 6, 1), true, "Recap Special", null, "Entry One"));
        var owned = new[]
        {
            new OwnedEpisode(1, 2, 3, new Dictionary<string, string> { ["Tvdb"] = "x" }, null),
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
        };
        var m = Assert.Single(DiffEngine.Diff(owned, union, Opts()).Missing);
        Assert.Equal("sp2", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void RealSeasonZeroTuple_NonSynthesized_StillMatches()
    {
        // TVDB/TMDB lanes: Season 0 is a REAL season; id-less S0 tuples keep
        // matching normally.
        var remote = new RemoteCatalog("Tvdb", null, "153021", true, new[]
        {
            new RemoteEpisode(1, 1, null, D(2010, 10, 31), false, "Days Gone Bye"),
            new RemoteEpisode(0, 1, null, D(2010, 10, 11), true, "Sneak Peek"),
        });
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string>(), null),
            new OwnedEpisode(0, 1, null, new Dictionary<string, string>(), null),
        };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    // ---------------------------------------------------------------- S-4 --

    [Fact]
    public void AllSeasonsPage_SpecialsSection_ParsedAsSeasonZero()
    {
        var (episodes, error) = TvdbScrapeFetcher.ParseAllSeasons(Fix("tvdb-allseasons-specials.html"));
        Assert.Null(error);
        var specials = episodes!.Where(e => e.Season == 0).OrderBy(e => e.Number).ToList();
        Assert.Equal(2, specials.Count);
        Assert.All(specials, e => Assert.True(e.IsSpecial));
        Assert.Equal("2993901", specials[0].SourceEpisodeId);
        Assert.Equal(1, specials[0].Number);
        Assert.Equal(D(2010, 10, 11), specials[0].AiredAt);
        Assert.Equal("2993911", specials[1].SourceEpisodeId);
        // regular section untouched
        Assert.Contains(episodes!, e => e.Season == 1 && e.Number == 1 && e.SourceEpisodeId == "3286391");
    }

    // ---------------------------------------------------------------- S-7 --

    [Fact]
    public void SpecialDateMatch_SameDaySiblings_OnlyTitleDecides()
    {
        // Shippuden shape: the movie (owned as S0E8) and a same-day bonus
        // short share the airdate 2015-08-07. Date equality is ambiguous;
        // only the title-matched movie is owned - the short stays missing.
        var union = MovieUnion(
            new RemoteEpisode(1, 1, "mv1", D(2015, 8, 7), true, "Complete Movie", null, "Boruto: Naruto the Movie"),
            new RemoteEpisode(1, 2, "sh1", D(2015, 8, 7), true, "Same-day Bonus Short", null, "Boruto: Naruto the Movie"));
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(8, "Boruto: Naruto the Movie", aired: D(2015, 8, 7)),
        };
        var m = Assert.Single(DiffEngine.Diff(owned, union, Opts()).Missing);
        Assert.Equal("sh1", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void SpecialDateMatch_UniqueDate_StillOwns()
    {
        // Unambiguous date equality remains sufficient (no title needed).
        var union = MovieUnion(
            new RemoteEpisode(1, 1, "mv1", D(2015, 8, 7), true, "Complete Movie", null, "Entry"));
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(8, "totally different local name", aired: D(2015, 8, 7)),
        };
        Assert.Empty(DiffEngine.Diff(owned, union, Opts()).Missing);
    }

    // ---------------------------------------------------------------- S-8 --

    private static RemoteCatalog RadioShortUnion() => MovieUnion(
        new RemoteEpisode(1, 1, "rs1", D(2012, 1, 25), true,
            "Han Megumi and Ise Mariya's Hunter x Hunter Hunter Studio 1", null, "Hunter x Hunter (2011)"));

    [Fact]
    public void S0Wildcard_LocalContentContradicts_Vetoed()
    {
        // HxH shape: local S0E1 is the movie Phantom Rouge (title+date known);
        // it must NOT wildcard-claim the radio short that shares number 1 -
        // both its signals contradict, so the short reports missing.
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(1, "[F] Hunter x Hunter - Phantom Rouge", aired: D(2013, 1, 12)),
        };
        var m = Assert.Single(DiffEngine.Diff(owned, RadioShortUnion(), Opts()).Missing);
        Assert.Equal("rs1", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void S0Wildcard_LocalTitleAgrees_StillOwns()
    {
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(1, "Han Megumi and Ise Mariya's Hunter x Hunter Hunter Studio 1"),
        };
        Assert.Empty(DiffEngine.Diff(owned, RadioShortUnion(), Opts()).Missing);
    }

    [Fact]
    public void S0Wildcard_LocalWithoutTitleOrDate_StillOwns()
    {
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
            S0(1, null),
        };
        Assert.Empty(DiffEngine.Diff(owned, RadioShortUnion(), Opts()).Missing);
    }

    // ---------------------------------------------------------------- S-6 --

    [Fact]
    public void UndatedCatalogSpecials_Noted()
    {
        var union = MovieUnion(
            new RemoteEpisode(1, 1, "u1", null, true, "Undated One", null, "Entry One"),
            new RemoteEpisode(1, 2, "u2", null, true, "Undated Two", null, "Entry One"));
        var owned = new[]
        {
            new OwnedEpisode(1, 1, null, new Dictionary<string, string> { ["AniDB"] = "r1" }, null),
        };
        var diff = DiffEngine.Diff(owned, union, Opts());
        Assert.Contains(diff.Notes, n => n.Contains("2 undated special", StringComparison.OrdinalIgnoreCase));
    }
}
