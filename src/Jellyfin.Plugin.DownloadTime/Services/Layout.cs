using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Local-layout detection for merged (Ronin-style) libraries: every real
/// episode lives in local Season 1 at an absolute number while aired-season
/// rows may survive empty. Season-ful remote catalogs must then be matched
/// and placed on the cumulative absolute axis (analysis 2026-08-02).
/// </summary>
public static class Layout
{
    /// <summary>All numbered locals (specials in S0 aside) live in season 1.</summary>
    public static bool MergedSeasonOne(IReadOnlyList<OwnedEpisode> owned)
    {
        var any = false;
        foreach (var o in owned)
        {
            if (!o.Number.HasValue || !o.Season.HasValue || o.Season.Value == 0)
            {
                continue;
            }
            if (o.Season.Value != 1)
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Merged AND locally absolute-numbered: the single local season's
    /// numbering exceeds the remote first season, so aired (S,E) tuples
    /// cannot be local positions. Distinguishes a merged library from one
    /// that simply owns only season 1.
    /// </summary>
    public static bool MergedAbsolute(IReadOnlyList<OwnedEpisode> owned, RemoteCatalog remote)
    {
        if (!MergedSeasonOne(owned))
        {
            return false;
        }
        var remoteS1Max = remote.Episodes
            .Where(e => !e.IsSpecial && e.Season == 1 && e.Number.HasValue)
            .Select(e => e.Number!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (remoteS1Max == 0)
        {
            return false;
        }
        var localMax = owned
            .Where(o => o.Season == 1 && o.Number.HasValue)
            .Select(o => o.NumberEnd ?? o.Number!.Value)
            .Max();
        return localMax > remoteS1Max;
    }

    /// <summary>
    /// Cumulative aired-order position of every regular (season >= 1) remote
    /// episode, keyed by (Season, Number). This is the local numbering axis
    /// of a merged-absolute library.
    /// </summary>
    public static IReadOnlyDictionary<(int Season, int Number), int> AbsoluteIndex(RemoteCatalog remote)
    {
        var map = new Dictionary<(int, int), int>();
        var abs = 0;
        foreach (var e in remote.Episodes
                     .Where(e => !e.IsSpecial && e.Season is >= 1 && e.Number.HasValue)
                     .OrderBy(e => e.Season)
                     .ThenBy(e => e.Number))
        {
            abs++;
            map.TryAdd((e.Season!.Value, e.Number!.Value), abs);
        }
        return map;
    }
}
