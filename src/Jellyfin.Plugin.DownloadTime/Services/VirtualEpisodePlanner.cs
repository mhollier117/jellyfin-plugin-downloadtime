using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Decides placeholder creations/deletions (spec §4.3). Pure — the writer
/// (VirtualEpisodeWriter) applies the plan against ILibraryManager.
/// Deletes only ever reference the marker-filtered `existing` list.
/// </summary>
public static class VirtualEpisodePlanner
{
    public static string MarkerFor(RemoteCatalog catalog, RemoteEpisode e)
        => e.SourceEpisodeId is not null
            ? $"{catalog.SourceKey}:{e.SourceEpisodeId}"
            : $"{catalog.SourceKey}:S{e.Season ?? 0}E{e.Number ?? 0}";

    public static PlaceholderPlan Plan(
        SeriesDiff diff, RemoteCatalog catalog, IReadOnlyList<OwnedEpisode> owned,
        IReadOnlyList<ExistingPlaceholder> existing, bool featureEnabled)
    {
        if (!featureEnabled)
        {
            return new PlaceholderPlan(Array.Empty<PlaceholderCreate>(), existing.Select(e => e.ItemId).ToList());
        }

        // 10.4 MissingEpisodeProvider HasInvalidContent guard: unreliable local
        // numbering in a tuple-matched series risks duplicate placeholders.
        var hasInvalid = catalog.IdProviderKey is null
            && owned.Any(o => !o.Number.HasValue || !o.Season.HasValue);

        var desired = new Dictionary<string, PlaceholderCreate>(StringComparer.OrdinalIgnoreCase);
        if (!hasInvalid)
        {
            foreach (var m in diff.Missing)
            {
                var placement = Placer.Infer(m.Episode, owned, catalog);
                if (placement is null)
                {
                    continue;
                }
                var marker = MarkerFor(catalog, m.Episode);
                desired[marker] = new PlaceholderCreate(placement.Season, placement.Number, marker, m.Episode.Title, m.Episode.AiredAt);
            }
        }

        var deletes = new List<Guid>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ex in existing)
        {
            if (desired.TryGetValue(ex.Marker, out var want) && ex.Season == want.Season && ex.Number == want.Number)
            {
                keep.Add(ex.Marker);
            }
            else
            {
                deletes.Add(ex.ItemId);
            }
        }
        var creates = desired.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        return new PlaceholderPlan(creates, deletes);
    }

    /// <summary>
    /// Foreign virtual episodes (created by other writers, no DownloadTime
    /// marker) that PROVABLY duplicate an owned file: they carry a per-episode
    /// provider id that identifies exactly one physical episode. Ids shared by
    /// several physicals prove nothing (split-era misidentification) and the
    /// marker key is never an episode identity (live phantom analysis
    /// 2026-08-02: TVDB-plugin aired-season virtuals shadowing merged
    /// absolute-numbered files).
    /// </summary>
    public static IReadOnlyList<Guid> ForeignDuplicates(
        IReadOnlyList<OwnedEpisode> owned, IReadOnlyList<ForeignPlaceholder> foreign, RemoteCatalog? remote = null)
    {
        static bool IsMarker(string key)
            => string.Equals(key, VirtualEpisodeWriter.MarkerProviderKey, StringComparison.OrdinalIgnoreCase);

        var counts = new Dictionary<(string Key, string Value), int>();
        var owner = new Dictionary<(string Key, string Value), OwnedEpisode>();
        foreach (var o in owned)
        {
            foreach (var (k, v) in o.ProviderIds)
            {
                if (IsMarker(k) || string.IsNullOrEmpty(v))
                {
                    continue;
                }
                var key = (k.ToLowerInvariant(), v);
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                owner[key] = o;
            }
        }
        var unique = counts.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();

        // ---- proof 1: a per-episode id identifying exactly one physical ----
        var deletes = new List<Guid>();
        var deleted = new HashSet<Guid>();
        var pinned = new Dictionary<Guid, OwnedEpisode>(); // id-proven foreign -> its physical twin
        foreach (var f in foreign)
        {
            var key = f.ProviderIds
                .Where(kv => !IsMarker(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                .Select(kv => (kv.Key.ToLowerInvariant(), kv.Value))
                .FirstOrDefault(k => unique.Contains(k));
            if (key != default)
            {
                deletes.Add(f.ItemId);
                deleted.Add(f.ItemId);
                pinned[f.ItemId] = owner[key];
            }
        }

        // ---- proof 2: aired (S,E) -> owned absolute coverage --------------
        // Merged layouts only; specials (S0) are never swept by this proof.
        if (!Layout.MergedSeasonOne(owned))
        {
            return deletes;
        }
        int OwnersCovering(int abs) => owned.Count(o => o.Season == 1 && o.Covers(abs));

        // 2a: catalog axis — usable ONLY for non-synthesized season-ful
        // catalogs. Synthesized unions carry AniDB entry ordinals, not aired
        // seasons; mapping aired (S,E) through them mislabels episodes
        // (Bleach: aired S2E1 = abs 21, union (2,1) = abs 367).
        IReadOnlyDictionary<(int Season, int Number), int>? absIdx = null;
        if (remote is { SynthesizedSeasons: false }
            && remote.Episodes.Any(e => e.Season is > 1)
            && Layout.MergedAbsolute(owned, remote))
        {
            absIdx = Layout.AbsoluteIndex(remote);
        }

        // 2b: anchor axis — id-proven foreigns pin aired positions to merged
        // S1 physicals; >= 2 agreeing anchors fix a per-season offset.
        var anchors = new Dictionary<int, List<int>>();
        foreach (var f in foreign)
        {
            if (f.Season is not (> 0) || !f.Number.HasValue
                || !pinned.TryGetValue(f.ItemId, out var p)
                || p.Season != 1 || !p.Number.HasValue)
            {
                continue;
            }
            if (!anchors.TryGetValue(f.Season.Value, out var list))
            {
                anchors[f.Season.Value] = list = new List<int>();
            }
            list.Add(p.Number.Value - f.Number.Value);
        }
        var seasonOffset = new Dictionary<int, int>();
        foreach (var (season, offs) in anchors)
        {
            if (offs.Count >= 2 && offs.All(o => o == offs[0]))
            {
                seasonOffset[season] = offs[0];
            }
        }

        foreach (var f in foreign)
        {
            if (deleted.Contains(f.ItemId) || f.Season is not (>= 1) || !f.Number.HasValue)
            {
                continue;
            }
            int? abs = null;
            if (absIdx is not null && absIdx.TryGetValue((f.Season.Value, f.Number.Value), out var a))
            {
                abs = a;
            }
            else if (seasonOffset.TryGetValue(f.Season.Value, out var off))
            {
                abs = f.Number.Value + off;
            }
            if (abs.HasValue && OwnersCovering(abs.Value) == 1)
            {
                deletes.Add(f.ItemId);
                deleted.Add(f.ItemId);
            }
        }
        return deletes;
    }
}
