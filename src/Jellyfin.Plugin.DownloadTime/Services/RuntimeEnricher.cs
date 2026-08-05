using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Outcome of a runtime-enrichment pass (counts are for reporting).</summary>
public sealed record EnrichmentResult(RemoteCatalog Catalog, int Matched, int Ambiguous, int Unmatched);

/// <summary>
/// Attaches runtimes (and an alternate title) to season-0 items of a catalog
/// whose source does not report duration — the TVDB all-seasons page carries
/// no runtime at all, leaving most of its specials with no classification
/// signal (2026-08-05).
///
/// Strictly a SIDECAR: the routed catalog still drives missing-detection.
/// Nothing is added, removed or renumbered; only RuntimeMinutes/AltTitle are
/// filled in, and only on a CONFIDENT match — unique by exact air date or by
/// normalized title. Anything ambiguous is left untouched, so ambiguity keeps
/// meaning "no signal" and the item stays a Special.
/// </summary>
public static class RuntimeEnricher
{
    public static EnrichmentResult Enrich(RemoteCatalog catalog, IReadOnlyList<RemoteEpisode> enrichmentSource)
    {
        if (enrichmentSource.Count == 0)
        {
            return new EnrichmentResult(catalog, 0, 0, catalog.Episodes.Count(NeedsRuntime));
        }

        var matched = 0;
        var ambiguous = 0;
        var unmatched = 0;
        var episodes = new List<RemoteEpisode>(catalog.Episodes.Count);

        foreach (var e in catalog.Episodes)
        {
            if (!NeedsRuntime(e))
            {
                episodes.Add(e);
                continue;
            }

            var key = TitleKeys.Normalize(e.Title);
            var candidates = new List<RemoteEpisode>();
            foreach (var c in enrichmentSource)
            {
                var dateHit = e.AiredAt.HasValue && c.AiredAt.HasValue
                              && e.AiredAt.Value.Date == c.AiredAt.Value.Date;
                var titleHit = key is not null && TitleKeys.Normalize(c.Title) == key;
                if ((dateHit || titleHit) && !candidates.Contains(c))
                {
                    candidates.Add(c);
                }
            }

            if (candidates.Count == 1)
            {
                var m = candidates[0];
                matched++;
                episodes.Add(e with { RuntimeMinutes = m.RuntimeMinutes, AltTitle = m.Title });
                continue;
            }
            if (candidates.Count > 1)
            {
                ambiguous++;
            }
            else
            {
                unmatched++;
            }
            episodes.Add(e);
        }

        return new EnrichmentResult(catalog with { Episodes = episodes }, matched, ambiguous, unmatched);
    }

    /// <summary>Season-0 content that has no duration yet — the only enrichable rows.</summary>
    private static bool NeedsRuntime(RemoteEpisode e) => e.IsSpecial && !e.RuntimeMinutes.HasValue;
}
