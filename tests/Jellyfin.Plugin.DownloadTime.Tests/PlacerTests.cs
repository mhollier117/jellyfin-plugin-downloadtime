// Edge-case inventory:
// - tuple catalog: placement == remote (S,E); null when remote S or E null.
// - ID catalog, merged local (S1 absolute): between-anchors interpolation.
// - ID catalog, split local: anchors in same local season -> interpolate within it.
// - anchors disagree with remote spacing (inconsistent) -> null.
// - anchors straddle local seasons -> null (no confident scheme).
// - tail beyond last anchor -> extrapolate same season.
// - head before first anchor -> extrapolate down; below 1 -> null.
// - no anchors at all -> null.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class PlacerTests
{
    private static RemoteEpisode A(int epno, string id) => new(null, epno, id, null, false, null);
    private static OwnedEpisode OA(int s, int n, string id)
        => new(s, n, null, new Dictionary<string, string> { ["AniDB"] = id }, null);
    private static RemoteCatalog AniCat(params RemoteEpisode[] eps) => new("AniDB", "AniDB", "1", true, eps);

    [Fact]
    public void TupleCatalog_PlacesAtRemoteNumbers()
    {
        var cat = new RemoteCatalog("Tvdb", null, "1", true, Array.Empty<RemoteEpisode>());
        var p = Placer.Infer(new RemoteEpisode(2, 5, null, null, false, null), Array.Empty<OwnedEpisode>(), cat);
        Assert.Equal(new Placement(2, 5), p);
        Assert.Null(Placer.Infer(new RemoteEpisode(null, 5, null, null, false, null), Array.Empty<OwnedEpisode>(), cat));
    }

    [Fact]
    public void MergedLocal_BetweenAnchors_Interpolates()
    {
        var cat = AniCat(A(12, "a12"), A(13, "a13"), A(14, "a14"));
        var owned = new[] { OA(1, 12, "a12"), OA(1, 14, "a14") };
        Assert.Equal(new Placement(1, 13), Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void SplitLocal_SameSeasonAnchors_Interpolates()
    {
        var cat = AniCat(A(1, "b1"), A(2, "b2"), A(3, "b3"));
        var owned = new[] { OA(2, 1, "b1"), OA(2, 3, "b3") }; // entry mapped to local season 2
        Assert.Equal(new Placement(2, 2), Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void InconsistentAnchorSpacing_ReturnsNull()
    {
        var cat = AniCat(A(1, "c1"), A(2, "c2"), A(3, "c3"));
        var owned = new[] { OA(1, 1, "c1"), OA(1, 9, "c3") }; // spacing 8 vs remote spacing 2
        Assert.Null(Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void AnchorsStraddleSeasons_ReturnsNull()
    {
        var cat = AniCat(A(1, "d1"), A(2, "d2"), A(3, "d3"));
        var owned = new[] { OA(1, 12, "d1"), OA(2, 1, "d3") };
        Assert.Null(Placer.Infer(cat.Episodes[1], owned, cat));
    }

    [Fact]
    public void TailBeyondLastAnchor_ExtrapolatesSameSeason()
    {
        var cat = AniCat(A(10, "e10"), A(11, "e11"), A(12, "e12"));
        var owned = new[] { OA(1, 22, "e10"), OA(1, 23, "e11") }; // merged offset +12
        Assert.Equal(new Placement(1, 24), Placer.Infer(cat.Episodes[2], owned, cat));
    }

    [Fact]
    public void HeadBeforeFirstAnchor_ExtrapolatesDown_NullBelowOne()
    {
        var cat = AniCat(A(1, "f1"), A(2, "f2"), A(3, "f3"));
        var owned = new[] { OA(1, 2, "f2"), OA(1, 3, "f3") };
        Assert.Equal(new Placement(1, 1), Placer.Infer(cat.Episodes[0], owned, cat));
        var owned2 = new[] { OA(1, 1, "f2"), OA(1, 2, "f3") }; // extrapolating f1 -> local 0 -> null
        Assert.Null(Placer.Infer(cat.Episodes[0], owned2, cat));
    }

    [Fact]
    public void SeasonfulIdCatalog_PlacesAtRemoteSeasonEpisode_NotAnchorMath()
    {
        // Live bug 2026-07-25: TVDB catalogs have episode IDs AND per-season
        // numbers; anchor math built for season-less AniDB entries matched a
        // Season-1 anchor ("number 4") for missing S2E5 and placed it at S1E5.
        // Season-ful catalogs must place at the remote (Season, Number) directly.
        var cat = new RemoteCatalog("Tvdb", "Tvdb", "296762", true, new[]
        {
            new RemoteEpisode(1, 4, "t14", null, false, null),
            new RemoteEpisode(1, 5, "t15", null, false, null),
            new RemoteEpisode(2, 5, "t25", null, false, "Akane No Mai"),
        });
        var owned = new[]
        {
            new OwnedEpisode(1, 4, null, new Dictionary<string, string> { ["Tvdb"] = "t14" }, null),
            new OwnedEpisode(1, 5, null, new Dictionary<string, string> { ["Tvdb"] = "t15" }, null),
        };
        Assert.Equal(new Placement(2, 5), Placer.Infer(cat.Episodes[2], owned, cat));
    }

    [Fact]
    public void AnchorAtSameRemoteNumber_ReturnsNull_NotThrow()
    {
        // Live crash 2026-07-25: owned anchor whose remote number EQUALS the
        // missing episode's number (e.g. special "S1" parsed as 1 vs regular
        // ep 1) left below==above==null -> "Nullable object must have a value".
        var cat = AniCat(A(1, "h1"), new RemoteEpisode(null, 1, "h1s", null, true, "special one"));
        var owned = new[] { OA(1, 7, "h1s") }; // anchors at RemoteN=1, target is also 1
        Assert.Null(Placer.Infer(cat.Episodes[0], owned, cat));
    }

    [Fact]
    public void NoAnchors_ReturnsNull()
    {
        var cat = AniCat(A(1, "g1"));
        Assert.Null(Placer.Infer(cat.Episodes[0], Array.Empty<OwnedEpisode>(), cat));
    }
}
