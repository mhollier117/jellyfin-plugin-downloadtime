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
        IReadOnlyList<OwnedEpisode> owned, IReadOnlyList<ForeignPlaceholder> foreign)
    {
        static bool IsMarker(string key)
            => string.Equals(key, VirtualEpisodeWriter.MarkerProviderKey, StringComparison.OrdinalIgnoreCase);

        var counts = new Dictionary<(string Key, string Value), int>();
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
            }
        }
        var unique = counts.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();

        var deletes = new List<Guid>();
        foreach (var f in foreign)
        {
            if (f.ProviderIds.Any(kv => !IsMarker(kv.Key) && !string.IsNullOrEmpty(kv.Value)
                                        && unique.Contains((kv.Key.ToLowerInvariant(), kv.Value))))
            {
                deletes.Add(f.ItemId);
            }
        }
        return deletes;
    }
}
