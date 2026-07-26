using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>
/// Builds the union catalog over an AniDB entry chain (analysis doc
/// 2026-07-26): Season = entry ordinal (1-based, chain order), Number = epno
/// within the entry, AbsoluteNumber = cumulative regular-episode position
/// across the chain (specials get none). SynthesizedSeasons marks that the
/// ordinal is NOT a trustworthy local season number.
/// </summary>
public static class AniDbChain
{
    /// <summary>Maximum entries walked per series (cycle/runaway backstop).</summary>
    public const int MaxEntries = 16;

    public static RemoteCatalog BuildUnion(IReadOnlyList<RemoteCatalog> entries)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("union requires at least one entry", nameof(entries));
        }

        var episodes = new List<RemoteEpisode>();
        var absolute = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var ordinal = i + 1;
            // regular episodes in epno order define the absolute sequence
            foreach (var ep in entries[i].Episodes.OrderBy(e => e.IsSpecial ? 1 : 0).ThenBy(e => e.Number ?? int.MaxValue))
            {
                if (ep.IsSpecial)
                {
                    episodes.Add(ep with { Season = ordinal, AbsoluteNumber = null });
                }
                else
                {
                    absolute++;
                    episodes.Add(ep with { Season = ordinal, AbsoluteNumber = absolute });
                }
            }
        }

        return new RemoteCatalog(
            "AniDB",
            "AniDB",
            entries[0].SeriesSourceId,
            entries.All(e => e.IsEnded),
            episodes,
            SynthesizedSeasons: true);
    }
}
