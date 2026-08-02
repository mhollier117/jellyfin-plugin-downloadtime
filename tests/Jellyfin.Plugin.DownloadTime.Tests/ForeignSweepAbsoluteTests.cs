// Second, id-independent duplicate proof for merged-absolute libraries
// (post-recovery live shape 2026-08-02): NFO-recreated physicals can carry
// stale/duplicated per-episode ids, so the unique-id proof goes blind while
// aired-season phantoms (TVDB plugin) still shadow owned files.
// Proof A (catalog axis): for NON-synthesized season-ful catalogs, aired
//   (S,E) maps through the cumulative absolute index (same axis DiffEngine
//   uses); if exactly ONE owned S1 episode covers that absolute -> duplicate.
//   Synthesized unions NEVER qualify: their Season is an AniDB entry ordinal,
//   not an aired season (Bleach counterexample: aired S2E1 = abs 21, but the
//   union maps (2,1) to abs 367 - which IS owned - and would delete a
//   genuinely-missing placeholder).
// Proof B (anchor axis): foreign virtuals that the unique-id proof already
//   pins to merged S1 physicals define a per-aired-season offset
//   (physNumber - airedNumber). With >= 2 agreeing anchors in a season, the
//   remaining virtuals of that season map to absolute numbers; exactly one
//   owning physical -> duplicate.
// Conservatism: specials (S0) never swept by these proofs; ambiguous physical
// coverage (two files claiming the absolute) = keep; disagreeing or lone
// anchors = keep; split layouts never qualify.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ForeignSweepAbsoluteTests
{
    private static OwnedEpisode Phys(int s, int n, int? end = null, params (string Key, string Value)[] ids) => new(
        s, n, end,
        ids.ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase),
        null);

    private static ForeignPlaceholder Foreign(Guid id, int? s, int? n, params (string Key, string Value)[] ids) => new(
        id, s, n,
        ids.ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase));

    // ------------------------------------------------------------ proof B --

    /// <summary>HxH shape: merged S1E1..8; E5/E6 carry unique Tvdb ids, E7/E8
    /// only a duplicated AniDB id (id-blind). Aired-S2 phantoms E1/E2 pin to
    /// phys 5/6 (offset 4); E3/E4 then map to abs 7/8 (owned) and E5 to abs 9
    /// (beyond coverage - genuinely missing tail).</summary>
    private static (List<OwnedEpisode> Owned, List<ForeignPlaceholder> Foreign, Guid[] Ids) AnchorShape()
    {
        var owned = new List<OwnedEpisode>
        {
            Phys(1, 1, null, ("AniDB", "dup")), Phys(1, 2, null, ("AniDB", "dup")),
            Phys(1, 3, null, ("AniDB", "dup")), Phys(1, 4, null, ("AniDB", "dup")),
            Phys(1, 5, null, ("Tvdb", "t5")), Phys(1, 6, null, ("Tvdb", "t6")),
            Phys(1, 7, null, ("AniDB", "dup")), Phys(1, 8, null, ("AniDB", "dup")),
        };
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var foreign = new List<ForeignPlaceholder>
        {
            Foreign(ids[0], 2, 1, ("Tvdb", "t5")),   // anchor -> phys 5
            Foreign(ids[1], 2, 2, ("Tvdb", "t6")),   // anchor -> phys 6
            Foreign(ids[2], 2, 3, ("Tvdb", "t7x")),  // stale id, abs 7 owned -> sweep
            Foreign(ids[3], 2, 4),                    // no ids, abs 8 owned -> sweep
            Foreign(ids[4], 2, 5),                    // abs 9 beyond coverage -> keep
        };
        return (owned, foreign, ids);
    }

    [Fact]
    public void AnchoredSeasonOffset_SweepsIdBlindDuplicates_KeepsMissingTail()
    {
        var (owned, foreign, ids) = AnchorShape();
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, foreign, remote: null);
        Assert.Equal(
            new[] { ids[0], ids[1], ids[2], ids[3] }.OrderBy(g => g),
            deletes.OrderBy(g => g));
    }

    [Fact]
    public void LoneAnchor_InsufficientProof_IdBlindKept()
    {
        var (owned, foreign, ids) = AnchorShape();
        foreign.RemoveAt(1); // drop the t6 anchor -> only one anchor left
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, foreign, remote: null);
        Assert.Equal(ids[0], Assert.Single(deletes)); // id proof only
    }

    [Fact]
    public void DisagreeingAnchors_NoProofForThatSeason()
    {
        var (owned, foreign, ids) = AnchorShape();
        // second anchor pins to phys 8 instead of 6 -> offsets 4 vs 6 disagree
        foreign[1] = Foreign(ids[1], 2, 2, ("Tvdb", "t8x"));
        owned[7] = Phys(1, 8, null, ("Tvdb", "t8x"));
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, foreign, remote: null);
        Assert.Equal(
            new[] { ids[0], ids[1] }.OrderBy(g => g), // id-proof deletions only
            deletes.OrderBy(g => g));
    }

    [Fact]
    public void AmbiguousPhysicalCoverage_Kept()
    {
        var (owned, foreign, ids) = AnchorShape();
        // a second file also covers abs 7 (multi-episode span 6-7)
        owned[5] = Phys(1, 6, 7, ("Tvdb", "t6"));
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, foreign, remote: null);
        Assert.DoesNotContain(ids[2], deletes); // abs 7 claimed by two files -> keep
        Assert.Contains(ids[3], deletes);       // abs 8 still unambiguous
    }

    [Fact]
    public void Specials_NeverSweptByAbsoluteProof()
    {
        var (owned, foreign, _) = AnchorShape();
        var sp = Guid.NewGuid();
        foreign.Add(Foreign(sp, 0, 4)); // S0E4: numbering would map inside coverage
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, foreign, remote: null);
        Assert.DoesNotContain(sp, deletes);
    }

    [Fact]
    public void SplitLayout_AbsoluteProofNeverApplies()
    {
        var (owned, foreign, ids) = AnchorShape();
        owned.Add(Phys(2, 1, null, ("Tvdb", "t9"))); // a real episode outside S1 -> not merged
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, foreign, remote: null);
        Assert.Equal(
            new[] { ids[0], ids[1] }.OrderBy(g => g), // id proof only
            deletes.OrderBy(g => g));
    }

    // ------------------------------------------------------------ proof A --

    private static RemoteCatalog SeasonfulCat() => new("Tvdb", null, "1", true, new[]
    {
        new RemoteEpisode(1, 1, null, null, false, null), new RemoteEpisode(1, 2, null, null, false, null),
        new RemoteEpisode(1, 3, null, null, false, null),
        new RemoteEpisode(2, 1, null, null, false, null), new RemoteEpisode(2, 2, null, null, false, null),
        new RemoteEpisode(2, 3, null, null, false, null),
    });

    [Fact]
    public void SeasonfulCatalog_MapsAiredToOwnedAbsolute()
    {
        // merged-absolute: owns abs 1..5 of a 3+3 catalog; aired S2E1 = abs 4
        // (owned -> sweep), S2E3 = abs 6 (missing -> keep).
        var owned = new List<OwnedEpisode> { Phys(1, 1), Phys(1, 2), Phys(1, 3), Phys(1, 4), Phys(1, 5) };
        var dup = Guid.NewGuid();
        var tail = Guid.NewGuid();
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, new List<ForeignPlaceholder>
        {
            Foreign(dup, 2, 1),
            Foreign(tail, 2, 3),
        }, SeasonfulCat());
        Assert.Equal(dup, Assert.Single(deletes));
    }

    [Fact]
    public void SynthesizedUnion_NeverUsedForAbsoluteProof()
    {
        // Bleach counterexample: union entry ordinals are not aired seasons.
        // Owner covers 1..8 (past entry 1 of 6); aired S2E1 (= abs 7 on the
        // union axis, owned!) must be KEPT - the union axis is unusable.
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 6; i++) eps.Add(new RemoteEpisode(1, i, "a" + i, null, false, null, i));
        for (var i = 1; i <= 6; i++) eps.Add(new RemoteEpisode(2, i, "b" + i, null, false, null, 6 + i));
        var union = new RemoteCatalog("AniDB", "AniDB", "2369", true, eps, SynthesizedSeasons: true);
        var owned = Enumerable.Range(1, 8).Select(n => Phys(1, n)).ToList();
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(
            owned, new List<ForeignPlaceholder> { Foreign(Guid.NewGuid(), 2, 1) }, union);
        Assert.Empty(deletes);
    }
}
