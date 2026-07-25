// Edge-case inventory (ID lanes — IdProviderKey "AniDB"/"Tvdb"):
// - Ronin-merged absolute numbering: local S1E13 carries AniDB id of entry ep 13 -> ID diff exact.
// - Split-season layout: same IDs at different local numbers -> identical result.
// - Local numbering totally scrambled vs remote -> ID match still wins (numbering ignored).
// - Some locals lack the id -> those (only) fall back to tuple/epno matching.
// - Season-less remote (AniDB): id-less local matched by Number regardless of local Season.
// - Remote ep with null SourceEpisodeId in an ID catalog -> tuple path for it.
// - TVDB catalog with episode ids: renumbered local (wrong S/E, right id) still owned.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class DiffEngineIdTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static DiffOptions Opts() => new(Now, 24, false);
    private static DateTimeOffset D(int m, int d) => AirTime.FromDate(2026, m, d);

    private static RemoteEpisode A(int epno, string id, DateTimeOffset aired)
        => new(null, epno, id, aired, false, $"Ep {epno}");

    private static OwnedEpisode OA(int s, int n, string? anidbId) => new(
        s, n, null,
        anidbId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["AniDB"] = anidbId },
        null);

    private static RemoteCatalog AniCat(params RemoteEpisode[] eps) => new("AniDB", "AniDB", "18164", true, eps);

    [Fact]
    public void MergedAbsoluteNumbering_IdDiff_FindsExactGap()
    {
        var remote = AniCat(A(1, "274088", D(1, 7)), A(2, "274089", D(1, 14)), A(3, "274090", D(1, 21)));
        // Ronin merged: locals live at S1E1/E3 with correct AniDB ids; ep2 absent
        var owned = new[] { OA(1, 1, "274088"), OA(1, 3, "274090") };
        var diff = DiffEngine.Diff(owned, remote, Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("274089", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void ScrambledLocalNumbering_IdsStillOwn()
    {
        var remote = AniCat(A(1, "274088", D(1, 7)), A(2, "274089", D(1, 14)));
        // local numbers are nonsense (S5E99 etc.) but ids correct -> nothing missing
        var owned = new[] { OA(5, 99, "274088"), OA(9, 1, "274089") };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void IdlessLocals_FallBackToEpnoMatching_SeasonIgnored()
    {
        var remote = AniCat(A(1, "274088", D(1, 7)), A(2, "274089", D(1, 14)));
        // one local has no AniDB id but sits at Number=2 (any season) -> claims epno 2
        var owned = new[] { OA(1, 1, "274088"), OA(3, 2, null) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void TvdbIdCatalog_RenumberedLocal_RightId_StillOwned()
    {
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "253573", true, new[]
        {
            new RemoteEpisode(1, 1, "5088686", D(1, 1), false, "The Bone Orchard"),
            new RemoteEpisode(1, 2, "5088687", D(1, 8), false, null),
        });
        var owned = new[]
        {
            new OwnedEpisode(4, 44, null, new Dictionary<string, string> { ["Tvdb"] = "5088686" }, null),
            new OwnedEpisode(1, 2, null, new Dictionary<string, string>(), null), // id-less -> tuple
        };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }

    [Fact]
    public void RemoteEpWithoutId_InIdCatalog_UsesTuple()
    {
        var remote = new RemoteCatalog("Tvdb", "Tvdb", "253573", true, new[]
        {
            new RemoteEpisode(1, 1, null, D(1, 1), false, null), // page row lacked a link
        });
        var owned = new[] { new OwnedEpisode(1, 1, null, new Dictionary<string, string>(), null) };
        Assert.Empty(DiffEngine.Diff(owned, remote, Opts()).Missing);
    }
}
