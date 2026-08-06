using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Repeated-prefix detection for one series' season-0 content (2026-08-05).
///
/// A run of >=3 items sharing a title prefix is bonus material only when the
/// PREFIX ITSELF carries production vocabulary — "Creating Westworld's
/// Reality", "Early Cuts", "Season 2 Stories From the Bunkhouse", "Diaries".
/// Spinoff and crossover runs ("No Prep Kings", "Street Outlaws vs. Fast N'
/// Loud", "South Park", "Countdown to 1997") share a prefix too but say
/// nothing about production, so they stay Special however long the run is.
///
/// Two further guards keep this from becoming the blunt structural rule that
/// was rejected earlier: a single feature-length member spares the whole
/// group, and only members with UNKNOWN runtime are ever hidden (anything
/// with a known runtime is already the runtime rule's business).
/// </summary>
public static class PrefixGroups
{
    /// <summary>Production vocabulary that may appear in a run's prefix.</summary>
    public static readonly IReadOnlyList<string> PrefixVocabulary = new[]
    {
        "stories", "story", "creating", "revealed", "moment", "moments", "cuts",
        "diaries", "diary", "making", "behind", "inside", "anatomy", "profile",
        "featurette", "webisode", "minisode", "sketch", "tour", "recap",
    };

    /// <summary>Text before the first ':' , else the first three words. Null when unusable.</summary>
    public static string? KeyFor(RemoteEpisode episode) => KeyFor(episode.Title);

    public static string? KeyFor(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        var t = title.Trim();
        var colon = t.IndexOf(':', StringComparison.Ordinal);
        var head = colon > 0
            ? t[..colon]
            : string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3));
        head = head.Trim().ToLowerInvariant();
        return head.Length == 0 ? null : head;
    }

    /// <summary>
    /// Prefix keys whose run qualifies as bonus material. Callers demote only
    /// the members of these groups that have no runtime of their own.
    /// </summary>
    public static IReadOnlySet<string> DemotableKeys(IReadOnlyList<RemoteEpisode> seasonZero, ClassifierOptions options)
    {
        var groups = new Dictionary<string, List<RemoteEpisode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in seasonZero)
        {
            if (!e.IsSpecial)
            {
                continue;
            }
            var key = KeyFor(e);
            if (key is null)
            {
                continue;
            }
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<RemoteEpisode>();
            }
            list.Add(e);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, members) in groups)
        {
            if (members.Count < 3)
            {
                continue;
            }
            if (!PrefixVocabulary.Any(v => key.Contains(v, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (members.Any(m => m.RuntimeMinutes >= options.ExtraRuntimeThresholdMinutes))
            {
                continue; // a feature-length member vouches for the whole run
            }
            if (!members.Any(m => !m.RuntimeMinutes.HasValue))
            {
                continue; // nothing left for this rule to decide
            }
            result.Add(key);
        }
        return result;
    }
}
