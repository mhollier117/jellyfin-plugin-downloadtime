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
        // TVDB) place at the remote (Season, Number) directly. Synthesized
        // unions are the exception: their Season is an AniDB entry ordinal,
        // NOT a local season — those always use anchor math on the
        // chain-monotonic AbsoluteNumber axis (M8, analysis 2026-07-26).
        if (!remote.SynthesizedSeasons && missing.Season.HasValue && missing.Number.HasValue)
        {
            // Merged-absolute local layout (live Bleach shape 2026-08-02): the
            // aired (S,E) tuple is NOT a local position — leftover empty
            // aired-season rows would swallow the placeholder. Place at local
            // S1 on the cumulative absolute axis instead.
            if (!missing.IsSpecial && missing.Season.Value >= 1 && Layout.MergedAbsolute(owned, remote))
            {
                return Layout.AbsoluteIndex(remote).TryGetValue((missing.Season.Value, missing.Number.Value), out var abs)
                    ? new Placement(1, abs)
                    : null;
            }
            return new Placement(missing.Season.Value, missing.Number.Value);
        }

        var targetAxis = remote.SynthesizedSeasons ? missing.AbsoluteNumber : missing.Number;
        if (remote.IdProviderKey is null || !targetAxis.HasValue)
        {
            return null;
        }

        // anchors: remote axis position -> local (season, number), joined on
        // episode IDs. Axis = AbsoluteNumber for synthesized unions (monotonic
        // across the entry chain), epno otherwise.
        var idToRemoteNumber = remote.Episodes
            .Where(e => e.SourceEpisodeId is not null
                        && (remote.SynthesizedSeasons ? e.AbsoluteNumber : e.Number).HasValue)
            .ToDictionary(
                e => e.SourceEpisodeId!,
                e => (remote.SynthesizedSeasons ? e.AbsoluteNumber : e.Number)!.Value,
                StringComparer.OrdinalIgnoreCase);
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

        var target = targetAxis.Value;
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
