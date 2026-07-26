// Edge-case inventory (M8, synthesized union catalogs):
// - NEVER take the season-ful short-circuit (entry ordinal != local season);
//   a missing (ordinal 2, epno 1) with merged-local anchors must place at S1/abs, not S2E1.
// - merged local, multi-entry: anchors on AbsoluteNumber extrapolate the tail correctly.
// - split local, multi-entry: same-local-season interpolation via AbsoluteNumber anchors.
// - specials (AbsoluteNumber null) are unplaceable in synthesized catalogs -> null.
// - non-synthesized behavior is pinned by the existing frozen PlacerTests.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class PlacerUnionTests
{
    // entry1: a1..a4 (S1 E1-4, abs 1-4); entry2: b1..b3 (S2 E1-3, abs 5-7); one special (no abs)
    private static RemoteCatalog Union()
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 4; i++) eps.Add(new RemoteEpisode(1, i, "a" + i, null, false, null, i));
        for (var i = 1; i <= 3; i++) eps.Add(new RemoteEpisode(2, i, "b" + i, null, false, null, 4 + i));
        eps.Add(new RemoteEpisode(1, 1, "sp1", null, true, "special", null));
        return new RemoteCatalog("AniDB", "AniDB", "18164", true, eps, SynthesizedSeasons: true);
    }

    private static OwnedEpisode OA(int s, int n, string id)
        => new(s, n, null, new Dictionary<string, string> { ["AniDB"] = id }, null);

    private static RemoteEpisode Ep(string id) => Union().Episodes.Single(e => e.SourceEpisodeId == id);

    [Fact]
    public void Synthesized_NeverShortCircuits_MergedLayoutWins()
    {
        // merged local: b1 (abs 5) lives at S1E5. Missing b2 (ordinal 2, epno 2, abs 6)
        // must land at S1E6 — NOT the ordinal tuple (2,2).
        var owned = new[] { OA(1, 4, "a4"), OA(1, 5, "b1") };
        Assert.Equal(new Placement(1, 6), Placer.Infer(Ep("b2"), owned, Union()));
    }

    [Fact]
    public void SplitLayout_AnchorsWithinLocalSeason()
    {
        var owned = new[] { OA(2, 1, "b1"), OA(2, 3, "b3") };
        Assert.Equal(new Placement(2, 2), Placer.Infer(Ep("b2"), owned, Union()));
    }

    [Fact]
    public void SplitLayout_AnchorsStraddlingLocalSeasons_Null()
    {
        var owned = new[] { OA(1, 4, "a4"), OA(2, 2, "b2") }; // missing b1 (abs 5) sits between local seasons
        Assert.Null(Placer.Infer(Ep("b1"), owned, Union()));
    }

    [Fact]
    public void Special_NoAbsoluteNumber_Unplaceable()
    {
        var owned = new[] { OA(1, 1, "a1") };
        Assert.Null(Placer.Infer(Ep("sp1"), owned, Union()));
    }
}
