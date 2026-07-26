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

        // --- M7 fail-safe (synthesized/anime catalogs only, analysis 2026-07-26):
        // owned episodes exist but NONE are matchable -> never report the whole
        // show missing; note it instead. Tuple-lane behavior is pinned elsewhere.
        if (remote.SynthesizedSeasons && owned.Count > 0 && ownedIds.Count == 0 && tupleOwned.Count == 0)
        {
            notes.Add("Local episodes are unidentifiable (no AniDB ids, no usable numbering) - skipping missing detection for this series (fail-safe).");
            return new SeriesDiff(Array.Empty<MissingEpisode>(), notes);
        }

        // --- relevant remote set ----------------------------------------------
        bool IsSpecialEp(RemoteEpisode e) => e.IsSpecial || (e.Season == 0 && !remote.SynthesizedSeasons);
        var considered = remote.Episodes.Where(e => opts.IncludeSpecials || !IsSpecialEp(e)).ToList();

        // --- matching -----------------------------------------------------------
        // Synthesized unions (AniDB entry chains) let id-less locals match by
        // EITHER the (entry ordinal, epno) tuple (split layouts) OR absolute-
        // number coverage (merged layouts). Conservative OR: false-missing is
        // worse than false-owned.
        bool TupleMatch(RemoteEpisode e) => e.Number.HasValue && tupleOwned.Any(o =>
            (e.Season is null || !o.Season.HasValue || o.Season == e.Season) && o.Covers(e.Number.Value));
        bool AbsMatch(RemoteEpisode e) => remote.SynthesizedSeasons && e.AbsoluteNumber.HasValue
            && tupleOwned.Any(o => o.Covers(e.AbsoluteNumber.Value));
        bool IdMatch(RemoteEpisode e) => e.SourceEpisodeId is not null && ownedIds.Contains(e.SourceEpisodeId);
        var fallbackMatched = 0;
        bool IsOwned(RemoteEpisode e)
        {
            if (IdMatch(e))
            {
                return true;
            }
            if (TupleMatch(e) || AbsMatch(e))
            {
                fallbackMatched++;
                return true;
            }
            return false;
        }

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

        if (remote.SynthesizedSeasons && fallbackMatched > 0)
        {
            notes.Add($"{fallbackMatched} episode(s) matched via numbering fallback (no AniDB episode ids on those local files).");
        }

        // --- "library exceeds source" note --------------------------------------
        var strayIds = ownedIds.Count(id => !remote.Episodes.Any(e =>
            e.SourceEpisodeId is not null && string.Equals(e.SourceEpisodeId, id, StringComparison.OrdinalIgnoreCase)));
        var strayTuples = tupleOwned.Count(o => !remote.Episodes.Any(e =>
            (e.Number.HasValue && (e.Season is null || !o.Season.HasValue || o.Season == e.Season) && o.Covers(e.Number.Value))
            || (remote.SynthesizedSeasons && e.AbsoluteNumber.HasValue && o.Covers(e.AbsoluteNumber.Value))));
        var stray = strayIds + strayTuples;
        if (stray > 0)
        {
            notes.Add($"{stray} local episode(s) unknown to the source.");
        }

        return new SeriesDiff(missing, notes);
    }
}
