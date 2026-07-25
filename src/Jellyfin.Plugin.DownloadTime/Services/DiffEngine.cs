using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Pure missing-episode detection. ID-first matching when the catalog carries
/// an IdProviderKey; (season,episode) tuple matching otherwise / as fallback
/// for local items lacking the ID. See spec §3.
/// </summary>
public static class DiffEngine
{
    public static SeriesDiff Diff(IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote, DiffOptions opts)
    {
        var notes = new List<string>();

        // --- owned partitions -------------------------------------------------
        var idKey = remote.IdProviderKey;
        var ownedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tupleOwned = new List<OwnedEpisode>();  // participate in tuple matching
        var unnumbered = 0;
        var seasonless = remote.Episodes.Count > 0 && remote.Episodes.All(e => e.Season is null);
        foreach (var o in owned)
        {
            var hasId = idKey is not null && o.ProviderIds.TryGetValue(idKey, out var id) && !string.IsNullOrEmpty(id);
            if (hasId)
            {
                ownedIds.Add(o.ProviderIds[idKey!]);
                continue; // ID-bearing locals match by ID only (spec: tuple fallback is for id-less locals)
            }
            if (o.Number.HasValue && (o.Season.HasValue || seasonless))
            {
                tupleOwned.Add(o);
            }
            else
            {
                unnumbered++;
            }
        }
        if (unnumbered > 0)
        {
            notes.Add($"{unnumbered} local episode(s) unnumbered and unidentifiable - excluded from matching.");
        }

        // --- relevant remote set ----------------------------------------------
        bool IsSpecialEp(RemoteEpisode e) => e.IsSpecial || e.Season == 0;
        var considered = remote.Episodes.Where(e => opts.IncludeSpecials || !IsSpecialEp(e)).ToList();

        // --- matching -----------------------------------------------------------
        bool TupleMatch(RemoteEpisode e) => e.Number.HasValue && tupleOwned.Any(o =>
            (e.Season is null || !o.Season.HasValue || o.Season == e.Season) && o.Covers(e.Number.Value));
        bool IdMatch(RemoteEpisode e) => e.SourceEpisodeId is not null && ownedIds.Contains(e.SourceEpisodeId);
        bool IsOwned(RemoteEpisode e) => IdMatch(e) || TupleMatch(e);

        // --- aired rule ---------------------------------------------------------
        bool AiredLongEnough(RemoteEpisode e)
            => e.AiredAt.HasValue && e.AiredAt.Value.AddHours(opts.GraceHours) < opts.Now;

        // --- classification -----------------------------------------------------
        var newestOwnedAir = owned.Where(o => o.AiredAt.HasValue).Select(o => o.AiredAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var hasOwnedAir = owned.Any(o => o.AiredAt.HasValue);

        var missing = new List<MissingEpisode>();
        foreach (var e in considered)
        {
            if (IsOwned(e) || !AiredLongEnough(e))
            {
                continue;
            }
            var kind = !hasOwnedAir || e.AiredAt!.Value <= newestOwnedAir ? MissingKind.Gap : MissingKind.New;
            missing.Add(new MissingEpisode(e, kind));
        }

        // --- "library exceeds source" note --------------------------------------
        var strayIds = ownedIds.Count(id => !remote.Episodes.Any(e =>
            e.SourceEpisodeId is not null && string.Equals(e.SourceEpisodeId, id, StringComparison.OrdinalIgnoreCase)));
        var strayTuples = tupleOwned.Count(o => !remote.Episodes.Any(e =>
            e.Number.HasValue && (e.Season is null || !o.Season.HasValue || o.Season == e.Season) && o.Covers(e.Number.Value)));
        var stray = strayIds + strayTuples;
        if (stray > 0)
        {
            notes.Add($"{stray} local episode(s) unknown to the source.");
        }

        return new SeriesDiff(missing, notes);
    }
}
