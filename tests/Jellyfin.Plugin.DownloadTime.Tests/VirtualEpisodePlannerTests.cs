// Edge-case inventory:
// - feature off -> delete ALL existing, create none.
// - fresh missing episode -> one create at Placer placement, marker stamped.
// - idempotency: existing placeholder matches desired marker+position -> no create, no delete.
// - resolved (no longer missing) -> its placeholder deleted.
// - placement changed (e.g. remote renumbered) -> delete old + create new.
// - unplaceable missing (Placer returns null) -> skipped, no create.
// - HasInvalidContent-style guard: any owned episode with null Number in an
//   id-less catalog -> plan creates NOTHING for that series (dup prevention),
//   but still deletes obsolete placeholders.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class VirtualEpisodePlannerTests
{
    private static readonly DateTimeOffset Aired = new(2026, 1, 1, 23, 59, 0, TimeSpan.Zero);

    private static RemoteCatalog TupleCat(params RemoteEpisode[] eps) => new("Tvdb", null, "1", true, eps);
    private static RemoteEpisode R(int s, int n) => new(s, n, null, Aired, false, $"S{s}E{n}");
    private static SeriesDiff DiffOf(params MissingEpisode[] m) => new(m, Array.Empty<string>());
    private static MissingEpisode Gap(RemoteEpisode e) => new(e, MissingKind.Gap);
    private static OwnedEpisode O(int s, int n) => new(s, n, null, new Dictionary<string, string>(), null);

    [Fact]
    public void FeatureOff_DeletesAll_CreatesNone()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "Tvdb:S1E2") };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), new[] { O(1, 1) }, existing, featureEnabled: false);
        Assert.Empty(plan.Creates);
        Assert.Equal(existing[0].ItemId, Assert.Single(plan.Deletes));
    }

    [Fact]
    public void FreshMissing_CreatesWithMarker()
    {
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), new[] { O(1, 1) },
            Array.Empty<ExistingPlaceholder>(), true);
        var c = Assert.Single(plan.Creates);
        Assert.Equal((1, 2), (c.Season, c.Number));
        Assert.Equal("Tvdb:S1E2", c.Marker);
        Assert.Equal(Aired, c.AiredAt);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Idempotent_ExistingMatches_NoOps()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "Tvdb:S1E2") };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), new[] { O(1, 1) }, existing, true);
        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Resolved_PlaceholderDeleted()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "Tvdb:S1E2") };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(), TupleCat(R(1, 2)), new[] { O(1, 1), O(1, 2) }, existing, true);
        Assert.Empty(plan.Creates);
        Assert.Single(plan.Deletes);
    }

    [Fact]
    public void PlacementChanged_DeleteAndRecreate()
    {
        var idCat = new RemoteCatalog("AniDB", "AniDB", "9", true, new[]
        {
            new RemoteEpisode(null, 1, "x1", Aired, false, null),
            new RemoteEpisode(null, 2, "x2", Aired, false, null),
        });
        var owned = new[] { new OwnedEpisode(1, 13, null, new Dictionary<string, string> { ["AniDB"] = "x1" }, null) };
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 1, 2, "AniDB:x2") }; // stale position
        var plan = VirtualEpisodePlanner.Plan(DiffOf(new MissingEpisode(idCat.Episodes[1], MissingKind.New)), idCat, owned, existing, true);
        Assert.Single(plan.Deletes);
        var c = Assert.Single(plan.Creates);
        Assert.Equal((1, 14), (c.Season, c.Number)); // anchor x1 at S1E13 -> epno2 at E14
        Assert.Equal("AniDB:x2", c.Marker);
    }

    [Fact]
    public void Unplaceable_Skipped()
    {
        var idCat = new RemoteCatalog("AniDB", "AniDB", "9", true, new[] { new RemoteEpisode(null, 1, "y1", Aired, false, null) });
        var plan = VirtualEpisodePlanner.Plan(DiffOf(new MissingEpisode(idCat.Episodes[0], MissingKind.Gap)), idCat,
            Array.Empty<OwnedEpisode>(), Array.Empty<ExistingPlaceholder>(), true); // no anchors -> Placer null
        Assert.Empty(plan.Creates);
    }

    [Fact]
    public void InvalidLocalNumbering_BlocksCreates_StillDeletesObsolete()
    {
        var existing = new[] { new ExistingPlaceholder(Guid.NewGuid(), 3, 9, "Tvdb:S3E9") }; // obsolete
        var owned = new[] { O(1, 1), new OwnedEpisode(1, null, null, new Dictionary<string, string>(), null) };
        var plan = VirtualEpisodePlanner.Plan(DiffOf(Gap(R(1, 2))), TupleCat(R(1, 2)), owned, existing, true);
        Assert.Empty(plan.Creates);
        Assert.Single(plan.Deletes);
    }
}
