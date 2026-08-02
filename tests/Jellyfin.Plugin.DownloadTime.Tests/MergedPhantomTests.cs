// Live-shape regression suite (Bleach phantom analysis, 2026-08-02):
// A merged (Ronin-rehomed) library keeps every real episode in local S1 at
// absolute numbers while leftover aired-season rows (S2..S17) survive in the
// DB. Two failure modes must never produce phantom "missing" results:
//  (1) synthesized union + local per-episode ids that are DUPLICATED across
//      files (split-era AniDB plugin stamped each Season NN folder's E<n>
//      with the eid of union episode n) -> ids are untrustworthy; numbering
//      fallback must kick in instead of reporting owned episodes missing.
//  (2) season-ful tuple catalogs (TVDB shape): aired SxxEyy has no local
//      (S,E) twin in a merged library; presence must be checked on the
//      cumulative absolute axis, and placements must land in local S1 at the
//      absolute number - never in a leftover aired-season row.
// Layout matrix: merged, split, merged-with-leftover-season-rows (leftover
// rows are invisible to Diff/Placer inputs; the S1-absolute placement IS the
// protection - the writer then never targets S2+).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class MergedPhantomTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static DiffOptions Opts() => new(Now, 24, false);
    private static DateTimeOffset D(int y, int m, int d) => AirTime.FromDate(y, m, d);

    private static OwnedEpisode O(int s, int n, string? id = null, string key = "AniDB") => new(
        s, n, null,
        id is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [key] = id },
        null);

    // ---------------------------------------------------------------- (1) --
    // Union: entry1 = a1..a6 (S1 E1-6, abs 1-6), entry2 = b1..b6 (S2 E1-6, abs 7-12).
    private static RemoteCatalog Union()
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 6; i++) eps.Add(new RemoteEpisode(1, i, "a" + i, D(2024, 1, i), false, "E1x" + i, i));
        for (var i = 1; i <= 6; i++) eps.Add(new RemoteEpisode(2, i, "b" + i, D(2025, 1, i), false, "E2x" + i, 6 + i));
        return new RemoteCatalog("AniDB", "AniDB", "2369", true, eps, SynthesizedSeasons: true);
    }

    /// <summary>Bleach shape: merged S1 absolute locals; files re-homed from the old
    /// Season 2 folder were stamped with entry-1 eids at their WITHIN-SEASON number,
    /// so eids are duplicated across files. abs 8 (b2) is genuinely absent.</summary>
    private static List<OwnedEpisode> MergedCorruptIdLocals()
    {
        var owned = new List<OwnedEpisode>();
        for (var i = 1; i <= 6; i++) owned.Add(O(1, i, "a" + i));         // correct ids
        owned.Add(O(1, 7, "a1"));                                          // was S2E01 -> eid of ep 1
        // abs 8 (was S2E02) genuinely absent
        owned.Add(O(1, 9, "a3"));
        owned.Add(O(1, 10, "a4"));
        owned.Add(O(1, 11, "a5"));
        owned.Add(O(1, 12, "a6"));
        return owned;
    }

    [Fact]
    public void Synthesized_Merged_DuplicatedLocalIds_OnlyGenuineGapMissing()
    {
        var diff = DiffEngine.Diff(MergedCorruptIdLocals(), Union(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("b2", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void Synthesized_Merged_DuplicatedLocalIds_EmitsUnreliableIdsNote()
    {
        var diff = DiffEngine.Diff(MergedCorruptIdLocals(), Union(), Opts());
        Assert.Contains(diff.Notes, n => n.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Synthesized_Split_DuplicatedLocalIds_TupleFallbackStillSeasonAware()
    {
        // Same corruption in a SPLIT layout: local (2, n) files stamped with a<n>.
        // Tuple fallback matches (ordinal 2, epno n) locals; b2 (S2E02) absent.
        var owned = new List<OwnedEpisode>();
        for (var i = 1; i <= 6; i++) owned.Add(O(1, i, "a" + i));
        owned.Add(O(2, 1, "a1"));
        owned.Add(O(2, 3, "a3"));
        owned.Add(O(2, 4, "a4"));
        owned.Add(O(2, 5, "a5"));
        owned.Add(O(2, 6, "a6"));
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("b2", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void Synthesized_Merged_UniqueCorrectIds_IdDetectionStaysExact()
    {
        // No duplication -> ids trusted, numbering NOT consulted (pins the
        // AdversarialAnimeTests (a) behavior: offset local numbering with
        // correct unique ids must not fallback-mask a real gap).
        var owned = new List<OwnedEpisode>();
        for (var i = 1; i <= 6; i++) owned.Add(O(1, i, "a" + i));
        owned.Add(O(1, 7, "b1"));
        owned.Add(O(1, 9, "b3")); // b2 absent; local numbering has a hole at 8 anyway
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        Assert.Equal(
            new[] { "b2", "b4", "b5", "b6" },
            diff.Missing.Select(m => m.Episode.SourceEpisodeId).OrderBy(x => x).ToArray());
    }

    // ---------------------------------------------------------------- (2) --
    // TVDB-shaped season-ful tuple catalog: S1E1..3 + S2E1..3, all aired.
    private static RemoteCatalog TupleCat()
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 3; i++) eps.Add(new RemoteEpisode(1, i, null, D(2024, 1, i), false, $"S1E{i}"));
        for (var i = 1; i <= 3; i++) eps.Add(new RemoteEpisode(2, i, null, D(2024, 2, i), false, $"S2E{i}"));
        return new RemoteCatalog("Tvdb", null, "74796", true, eps);
    }

    [Fact]
    public void Tuple_MergedAbsoluteLocals_AiredSeasonEpisodes_NotMissing()
    {
        // Merged absolute locals own abs 1..5 (S1E1..S2E2); only abs 6 (S2E3) missing.
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4), O(1, 5) };
        var diff = DiffEngine.Diff(owned, TupleCat(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal((2, 3), (m.Episode.Season, m.Episode.Number));
    }

    [Fact]
    public void Tuple_SplitLocals_SeasonMatchingUnchanged()
    {
        // Split layout: whole S2 owned except S2E3 -> exactly S2E3 missing.
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(2, 1), O(2, 2) };
        var diff = DiffEngine.Diff(owned, TupleCat(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal((2, 3), (m.Episode.Season, m.Episode.Number));
    }

    [Fact]
    public void Tuple_MergedAbsoluteLocals_PlacerPlacesAtSeasonOneAbsolute()
    {
        // The missing aired S2E3 is absolute 6 -> placeholder belongs at S1E6,
        // never in the (leftover, empty) aired Season 2 row.
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4), O(1, 5) };
        var missing = TupleCat().Episodes.Single(e => e.Season == 2 && e.Number == 3);
        Assert.Equal(new Placement(1, 6), Placer.Infer(missing, owned, TupleCat()));
    }

    [Fact]
    public void Tuple_SplitLocals_PlacerDirectPlacementUnchanged()
    {
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(2, 1), O(2, 2) };
        var missing = TupleCat().Episodes.Single(e => e.Season == 2 && e.Number == 3);
        Assert.Equal(new Placement(2, 3), Placer.Infer(missing, owned, TupleCat()));
    }

    [Fact]
    public void Tuple_SingleSeasonOwned_NotAbsolute_PlacerDirectPlacementPreserved()
    {
        // Owns exactly remote S1 (numbering does NOT exceed S1) -> this is a
        // split library that simply lacks S2; direct placement stands.
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3) };
        var missing = TupleCat().Episodes.Single(e => e.Season == 2 && e.Number == 1);
        Assert.Equal(new Placement(2, 1), Placer.Infer(missing, owned, TupleCat()));
    }

    [Fact]
    public void Tuple_MergedAbsoluteLocals_PlannerCreatesOnlyInSeasonOne()
    {
        // End-to-end plan for the merged shape: creates land in S1 at absolute
        // numbers - leftover aired-season rows are never targeted.
        var cat = TupleCat();
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4), O(1, 5) };
        var diff = DiffEngine.Diff(owned, cat, Opts());
        var plan = VirtualEpisodePlanner.Plan(diff, cat, owned, Array.Empty<ExistingPlaceholder>(), featureEnabled: true);
        var c = Assert.Single(plan.Creates);
        Assert.Equal((1, 6), (c.Season, c.Number));
    }
}
