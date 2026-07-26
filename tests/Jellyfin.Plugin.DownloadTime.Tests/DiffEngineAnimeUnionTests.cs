// Edge-case inventory (union catalogs — SynthesizedSeasons, matrix cells from
// docs/superpowers/specs/2026-07-26-anime-matching-analysis.md):
// M2: split local, ids present -> id diff exact regardless of local numbering.
// M3: merged local, multi-entry, ids present -> sequel-entry gap detected via union.
// M5: id-less merged local -> AbsoluteNumber coverage matches entry-2+ episodes; fallback note emitted.
// M6: id-less split local -> (ordinal, epno) tuple matches; no false-missing for owned S2 eps.
// M7: owned episodes but ZERO matchable (no ids, no numbers) -> fail-safe: no missing + note
//     (synthesized catalogs only; tuple-lane behavior is frozen elsewhere).
// - fallback note NOT emitted when everything matched by id.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class DiffEngineAnimeUnionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static DiffOptions Opts() => new(Now, 24, false);
    private static DateTimeOffset D(int y, int m, int d) => AirTime.FromDate(y, m, d);

    // Union: entry 1 = ids a1..a4 (S1 E1-4, abs 1-4), entry 2 = ids b1..b3 (S2 E1-3, abs 5-7). All aired.
    private static RemoteCatalog Union()
    {
        var eps = new List<RemoteEpisode>();
        for (var i = 1; i <= 4; i++) eps.Add(new RemoteEpisode(1, i, "a" + i, D(2024, 1, i), false, "E1x" + i, i));
        for (var i = 1; i <= 3; i++) eps.Add(new RemoteEpisode(2, i, "b" + i, D(2025, 1, i), false, "E2x" + i, 4 + i));
        return new RemoteCatalog("AniDB", "AniDB", "18164", true, eps, SynthesizedSeasons: true);
    }

    private static OwnedEpisode O(int s, int n, string? id = null) => new(
        s, n, null,
        id is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["AniDB"] = id },
        null);

    [Fact]
    public void M2_SplitLocal_IdsPresent_IdDiffExact()
    {
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "a3"), O(1, 4, "a4"), O(2, 1, "b1"), O(2, 3, "b3") };
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("b2", m.Episode.SourceEpisodeId);
    }

    [Fact]
    public void M3_MergedLocal_MultiEntry_IdsPresent_SequelGapDetected()
    {
        // Ronin-merged: everything in local S1 at absolute numbers, ids intact, b2 (abs 6) absent
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "a3"), O(1, 4, "a4"), O(1, 5, "b1"), O(1, 7, "b3") };
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("b2", m.Episode.SourceEpisodeId);
        Assert.DoesNotContain(diff.Notes, n => n.Contains("unknown to the source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void M5_IdlessMerged_AbsoluteCoverage_NoFalseMissing()
    {
        // NFO-sourced locals: no ids, merged absolute numbering 1..5 owned (abs 6,7 genuinely absent)
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4), O(1, 5) };
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        Assert.Equal(new[] { "b2", "b3" }, diff.Missing.Select(m => m.Episode.SourceEpisodeId).OrderBy(x => x).ToArray());
        Assert.Contains(diff.Notes, n => n.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void M6_IdlessSplit_TupleMatch_NoFalseMissing()
    {
        // Split locals, no ids: own S1E1-4 and S2E1 + S2E3; only S2E2 (b2) missing
        var owned = new[] { O(1, 1), O(1, 2), O(1, 3), O(1, 4), O(2, 1), O(2, 3) };
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        var m = Assert.Single(diff.Missing);
        Assert.Equal("b2", m.Episode.SourceEpisodeId);
        Assert.Contains(diff.Notes, n => n.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void M7_AllLocalsUnmatchable_FailSafe_NoMissingPlusNote()
    {
        var owned = new[]
        {
            new OwnedEpisode(null, null, null, new Dictionary<string, string>(), null),
            new OwnedEpisode(null, null, null, new Dictionary<string, string>(), null),
        };
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        Assert.Empty(diff.Missing);
        Assert.Contains(diff.Notes, n => n.Contains("fail-safe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllMatchedById_NoFallbackNote()
    {
        var owned = new[] { O(1, 1, "a1"), O(1, 2, "a2"), O(1, 3, "a3"), O(1, 4, "a4"), O(2, 1, "b1"), O(2, 2, "b2"), O(2, 3, "b3") };
        var diff = DiffEngine.Diff(owned, Union(), Opts());
        Assert.Empty(diff.Missing);
        Assert.DoesNotContain(diff.Notes, n => n.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }
}
