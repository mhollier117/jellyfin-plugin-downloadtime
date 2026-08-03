// Forensic-audit fixes (2026-08-03), DiffEngine side. Live shapes:
// D2a  The Walking Dead: multi-episode file S2E4 (IndexNumberEnd=12) carries
//      ONE Tvdb id; catalog S2E5..E12 have distinct ids -> 8 false missing.
//      A multi-episode file's Covers span must always tuple-match.
// D2b  NCIS:LA / TBBT / Weeds: single file whose upstream id went stale
//      (unknown to the catalog) while its (S,E) is correct -> false missing.
//      Stale-id locals join tuple matching - but only when the catalog-
//      verified locals are the majority (J_OneCorrectId pin: a wrong-tag
//      library with 1 lucky match must NOT numbering-claim everything).
// D4   Bleach: with duplicated (unreliable) ids, id matches must stop
//      proving ownership in EITHER direction - corrupt-eid files were
//      suppressing 19 genuinely-missing episodes.
// D3iii The S0-wildcard (local S0EN owns ANY union special #N) collides when
//      several chain entries each have a special #N: content (airdate/title)
//      must decide; a bare number is only proof when unambiguous.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AuditDiffTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private static DiffOptions Opts(bool specials = false) => new(Now, 24, specials);
    private static DateTimeOffset D(int y, int m, int d) => AirTime.FromDate(y, m, d);

    private static OwnedEpisode O(int? s, int? n, int? end = null, string? id = null, string key = "Tvdb",
        DateTimeOffset? aired = null) => new(
        s, n, end,
        id is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [key] = id },
        aired);

    // ---------------------------------------------------------------- D2a --

    [Fact]
    public void MultiEpisodeFile_WithSingleId_CoversWholeSpan()
    {
        // TWD shape: S2E4-E12 in one file, id of E4 only; catalog knows E4..E12
        // with distinct ids. Nothing in the span is missing.
        var eps = Enumerable.Range(4, 9)
            .Select(n => new RemoteEpisode(2, n, "t" + n, D(2011, 11, n), false, $"S2E{n}"))
            .ToArray();
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "153021", true, eps);
        var owned = new[] { O(2, 4, end: 12, id: "t4") };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    // ---------------------------------------------------------------- D2b --

    [Fact]
    public void StaleUpstreamId_CatalogVerifiedMajority_TupleStillOwns()
    {
        // NCIS:LA shape: most locals verify against the catalog; one file's id
        // is unknown upstream but its (S,E) is right -> not missing.
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "126665", true, new[]
        {
            new RemoteEpisode(12, 22, "k22", D(2019, 5, 2), false, null),
            new RemoteEpisode(12, 23, "k23", D(2019, 5, 9), false, null),
            new RemoteEpisode(12, 24, "NEW-ID", D(2019, 5, 16), false, "The Stockholm Syndrome"),
        });
        var owned = new[]
        {
            O(12, 22, id: "k22"),
            O(12, 23, id: "k23"),
            O(12, 24, id: "stale-old-id"),
        };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void StaleIds_InMajority_DoNotNumberingClaim()
    {
        // Wrong-tag-adjacent guard: when stale ids OUTNUMBER verified ones,
        // numbering fallback stays off for them (J_OneCorrectId pin).
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "1", true, new[]
        {
            new RemoteEpisode(1, 1, "a1", D(2024, 1, 1), false, null),
            new RemoteEpisode(1, 2, "a2", D(2024, 1, 8), false, null),
            new RemoteEpisode(1, 3, "a3", D(2024, 1, 15), false, null),
        });
        var owned = new[]
        {
            O(1, 1, id: "a1"),
            O(1, 2, id: "x2"),
            O(1, 3, id: "x3"),
        };
        Assert.Equal(2, DiffEngine.Diff(owned, remote, Opts()).Missing.Count);
    }

    // ----------------------------------------------------------------- D4 --

    [Fact]
    public void UnreliableIds_IdMatchesNoLongerSuppressMissing()
    {
        // Bleach shape (scaled): merged locals covering abs 1,2,5,6; the files
        // at 5/6 BOTH carry ep-3's eid (duplicated -> unreliable). Episodes 3
        // and 4 are genuinely absent and must BOTH be reported - the corrupt
        // eid on owned files must not vouch for ep 3.
        var eps = Enumerable.Range(1, 6)
            .Select(n => new RemoteEpisode(1, n, "a" + n, D(2024, 1, n), false, null, n))
            .ToArray();
        var union = new RemoteCatalog("AniDB", "AniDB", "2369", true, eps, SynthesizedSeasons: true);
        var owned = new[]
        {
            O(1, 1, id: "a1", key: "AniDB"),
            O(1, 2, id: "a2", key: "AniDB"),
            O(1, 5, id: "a3", key: "AniDB"),
            O(1, 6, id: "a3", key: "AniDB"),
        };
        var diff = DiffEngine.Diff(owned, union, Opts());
        Assert.Equal(new[] { "a3", "a4" },
            diff.Missing.Select(m => m.Episode.SourceEpisodeId).OrderBy(x => x).ToArray());
    }

    // --------------------------------------------------------------- D3iii --

    private static RemoteCatalog TwoMovieUnion()
    {
        // Two chain entries, each with special #1 (movie parts).
        return new RemoteCatalog("AniDB", "AniDB", "239", true, new[]
        {
            new RemoteEpisode(1, 1, "r1", D(2010, 1, 1), false, null, 1),
            new RemoteEpisode(1, 1, "m1", D(2014, 12, 6), true, "Movie One"),
            new RemoteEpisode(2, 1, "r2", D(2015, 1, 1), false, null, 2),
            new RemoteEpisode(2, 1, "m2", D(2015, 8, 7), true, "Movie Two"),
        }, SynthesizedSeasons: true);
    }

    [Fact]
    public void S0Wildcard_AmbiguousEpno_ContentDecides()
    {
        // Local S0E1 airdate matches Movie One only -> Movie Two is missing,
        // not silently wildcard-suppressed.
        var owned = new[]
        {
            O(1, 1, id: "r1", key: "AniDB"),
            O(1, 2, id: "r2", key: "AniDB"),
            O(0, 1, aired: D(2014, 12, 6)),
        };
        var diff = DiffEngine.Diff(owned, TwoMovieUnion(), Opts(specials: true));
        var m = Assert.Single(diff.Missing);
        Assert.Equal("m2", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void MovieSpecial_TitleMatch_OwnsWithoutAirdate()
    {
        // Shippuden shape: the movie is owned as local S0E7 under its release
        // title; the AniDB movie entry's episode is just "Complete Movie" but
        // the ENTRY name matches the local title -> owned.
        var union = new RemoteCatalog("AniDB", "AniDB", "4880", true, new[]
        {
            new RemoteEpisode(1, 1, "r1", D(2010, 1, 1), false, null, 1),
            new RemoteEpisode(1, 1, "mv1", null, true, "Complete Movie", null, "The Last: Naruto the Movie"),
            new RemoteEpisode(1, 1, "mv2", D(2012, 7, 28), true, "Complete Movie", null, "Road to Ninja"),
        }, SynthesizedSeasons: true);
        var owned = new[]
        {
            O(1, 1, id: "r1", key: "AniDB"),
            new OwnedEpisode(0, 7, null, new Dictionary<string, string>(), null, "The Last - Naruto the Movie!"),
        };
        var diff = DiffEngine.Diff(owned, union, Opts(specials: true));
        var m = Assert.Single(diff.Missing);
        Assert.Equal("mv2", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void S0Wildcard_UniqueEpno_StillOwns()
    {
        // Only ONE union special carries this epno -> the bare-number wildcard
        // remains sufficient (v1.2 regression guard stays intact).
        var union = new RemoteCatalog("AniDB", "AniDB", "239", true, new[]
        {
            new RemoteEpisode(1, 1, "r1", D(2010, 1, 1), false, null, 1),
            new RemoteEpisode(1, 7, "sp7", D(2014, 12, 6), true, "Some special"),
        }, SynthesizedSeasons: true);
        var owned = new[] { O(1, 1, id: "r1", key: "AniDB"), O(0, 7) };
        Assert.Empty(DiffEngine.Diff(owned, union, Opts(specials: true)).Missing);
    }
}

/// <summary>Audit D6/D7 lane-side additions (2026-08-03).</summary>
public class AuditLaneTests
{
    [Fact]
    public void AniDbParse_CapturesMainTitle_AsCatalogName()
    {
        var xml = """
            <anime id="700" restricted="false">
              <type>TV Series</type>
              <titles><title xml:lang="x-jat" type="main">Shingeki no Kyojin</title>
                      <title xml:lang="en" type="official">Attack on Titan</title></titles>
              <startdate>2013-04-07</startdate>
              <enddate>2013-09-28</enddate>
              <episodes>
                <episode id="e1"><epno type="1">1</epno><airdate>2013-04-07</airdate></episode>
              </episodes>
            </anime>
            """;
        var clock = new Support.FakeClock(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
        var (catalog, _, error) = Services.Lanes.AniDbFetcher.ParseEntry(xml, clock);
        Assert.Null(error);
        Assert.Equal("Shingeki no Kyojin", catalog!.Name);
    }

    private static RemoteEpisode Dated(int s, int n, int year)
        => new(s, n, null, AirTime.FromDate(year, 1, Math.Min(28, n)), false, null);

    [Fact]
    public void TvdbInferEnded_OldLastAirdate_Ended()
    {
        var eps = new[] { Dated(1, 1, 2010), Dated(1, 2, 2010), Dated(2, 1, 2012) };
        Assert.True(Services.Lanes.TvdbScrapeFetcher.InferEnded(eps, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TvdbInferEnded_RecentOrUndatedRegular_NotEnded()
    {
        var now = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var recent = new[] { Dated(1, 1, 2010), new RemoteEpisode(1, 2, null, now.AddDays(-10), false, null) };
        Assert.False(Services.Lanes.TvdbScrapeFetcher.InferEnded(recent, now));
        var undated = new[] { Dated(1, 1, 2010), new RemoteEpisode(2, 1, null, null, false, null) };
        Assert.False(Services.Lanes.TvdbScrapeFetcher.InferEnded(undated, now));
        Assert.False(Services.Lanes.TvdbScrapeFetcher.InferEnded(Array.Empty<RemoteEpisode>(), now));
    }
}
