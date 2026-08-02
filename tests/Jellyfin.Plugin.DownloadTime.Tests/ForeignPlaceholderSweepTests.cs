// Phantom-placeholder cleanup (live analysis 2026-08-02): virtual episodes
// created by OTHER writers (TVDB plugin missing-episode provider) sit in
// leftover aired-season rows and duplicate episodes the user owns under
// merged absolute numbering. The scan lifecycle must delete a foreign
// virtual episode when it PROVABLY duplicates an owned file: it carries a
// per-episode provider id that identifies exactly one physical episode.
// Guards: ids duplicated among physicals prove nothing (split-era AniDB
// mis-stamping); the DownloadTime marker key is not an episode identity;
// virtuals for genuinely-missing episodes (no id twin) must be kept.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ForeignPlaceholderSweepTests
{
    private static OwnedEpisode Owned(int n, params (string Key, string Value)[] ids) => new(
        1, n, null,
        ids.ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase),
        null);

    private static ForeignPlaceholder Foreign(Guid id, params (string Key, string Value)[] ids) => new(
        id,
        ids.ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void VirtualShadowingOwnedFile_UniqueIdMatch_Deleted()
    {
        // Bleach shape: physical S1E21 carries Tvdb id t21; the TVDB plugin's
        // virtual "S2E01" carries the same episode id -> provable duplicate.
        var owned = new[] { Owned(21, ("Tvdb", "t21")) };
        var phantom = Guid.NewGuid();
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(
            owned, new[] { Foreign(phantom, ("Tvdb", "t21")) });
        Assert.Equal(phantom, Assert.Single(deletes));
    }

    [Fact]
    public void VirtualForGenuinelyMissingEpisode_Kept()
    {
        // Frieren shape: virtual for the next airing episode shares no id
        // with any physical file -> keep it.
        var owned = new[] { Owned(38, ("Tvdb", "t38")) };
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(
            owned, new[] { Foreign(Guid.NewGuid(), ("Tvdb", "t39"), ("Imdb", "tt39")) });
        Assert.Empty(deletes);
    }

    [Fact]
    public void IdDuplicatedAmongPhysicals_ProvesNothing_Kept()
    {
        // Split-era AniDB mis-stamping: several files share eid e11; a virtual
        // carrying e11 is NOT proof of duplication -> keep.
        var owned = new[] { Owned(11, ("AniDB", "e11")), Owned(52, ("AniDB", "e11")) };
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(
            owned, new[] { Foreign(Guid.NewGuid(), ("AniDB", "e11")) });
        Assert.Empty(deletes);
    }

    [Fact]
    public void MarkerKeyNeverCountsAsEpisodeIdentity()
    {
        var owned = new[] { Owned(1, ("DownloadTime", "Tvdb:x")) };
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(
            owned, new[] { Foreign(Guid.NewGuid(), ("DownloadTime", "Tvdb:x")) });
        Assert.Empty(deletes);
    }

    [Fact]
    public void MixedSeries_DeletesOnlyProvenDuplicates()
    {
        var owned = new[]
        {
            Owned(21, ("Tvdb", "t21"), ("AniDB", "e1")),
            Owned(22, ("Tvdb", "t22"), ("AniDB", "e1")), // AniDB dup -> untrusted
        };
        var dupA = Guid.NewGuid();
        var keepB = Guid.NewGuid();
        var keepC = Guid.NewGuid();
        var deletes = VirtualEpisodePlanner.ForeignDuplicates(owned, new[]
        {
            Foreign(dupA, ("Tvdb", "t22")),            // unique physical twin -> delete
            Foreign(keepB, ("AniDB", "e1")),           // only a duplicated id -> keep
            Foreign(keepC, ("Tvdb", "t99")),           // unknown episode -> keep
        });
        Assert.Equal(dupA, Assert.Single(deletes));
    }
}
