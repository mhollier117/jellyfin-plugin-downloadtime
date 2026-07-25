using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Infers where a missing remote episode belongs in the LOCAL numbering
/// scheme by anchoring on owned episodes (spec §4.3). Returns null when no
/// confident placement exists — callers must then skip placeholder creation.
/// </summary>
public static class Placer
{
    public static Placement? Infer(RemoteEpisode missing, IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote)
    {
        // Season-ful episodes (tuple catalogs AND season-ful ID catalogs like
        // TVDB) place at the remote (Season, Number) directly — anchor math is
        // only meaningful for season-less absolute numbering (AniDB entries),
        // where the local scheme may be merged/split.
        if (missing.Season.HasValue && missing.Number.HasValue)
        {
            return new Placement(missing.Season.Value, missing.Number.Value);
        }

        if (remote.IdProviderKey is null || !missing.Number.HasValue)
        {
            return null;
        }

        // anchors: remote epno -> local (season, number), joined on episode IDs
        var idToRemoteNumber = remote.Episodes
            .Where(e => e.SourceEpisodeId is not null && e.Number.HasValue)
            .ToDictionary(e => e.SourceEpisodeId!, e => e.Number!.Value, StringComparer.OrdinalIgnoreCase);
        var anchors = new List<(int RemoteN, int LocalS, int LocalN)>();
        foreach (var o in owned)
        {
            if (o.Season.HasValue && o.Number.HasValue
                && o.ProviderIds.TryGetValue(remote.IdProviderKey, out var id)
                && idToRemoteNumber.TryGetValue(id, out var rn))
            {
                anchors.Add((rn, o.Season.Value, o.Number.Value));
            }
        }
        if (anchors.Count == 0)
        {
            return null;
        }

        var target = missing.Number.Value;
        (int RemoteN, int LocalS, int LocalN)? below = null, above = null;
        foreach (var a in anchors)
        {
            if (a.RemoteN < target && (below is null || a.RemoteN > below.Value.RemoteN)) below = a;
            if (a.RemoteN > target && (above is null || a.RemoteN < above.Value.RemoteN)) above = a;
        }

        if (below.HasValue && above.HasValue)
        {
            var b = below.Value; var a = above.Value;
            if (b.LocalS != a.LocalS || a.LocalN - b.LocalN != a.RemoteN - b.RemoteN)
            {
                return null; // straddles seasons or spacing disagrees - no confident scheme
            }
            return new Placement(b.LocalS, b.LocalN + (target - b.RemoteN));
        }
        if (below.HasValue)
        {
            var b = below.Value;
            return new Placement(b.LocalS, b.LocalN + (target - b.RemoteN));
        }
        if (!above.HasValue)
        {
            // Every anchor sits AT the target's remote number (e.g. a special
            // sharing the number) — no direction to extrapolate from.
            return null;
        }
        var up = above.Value;
        var n = up.LocalN - (up.RemoteN - target);
        return n >= 1 ? new Placement(up.LocalS, n) : null;
    }
}
