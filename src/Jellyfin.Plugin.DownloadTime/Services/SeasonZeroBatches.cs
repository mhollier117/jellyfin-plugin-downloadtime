using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Series-scoped classification of season-0 content. Everything here needs to
/// see a whole series at once, so it is computed per series and then consulted
/// per episode.
///
/// Every rule below was measured against the 430-row hand-labelled fixture
/// (tests/fixtures/specials-ground-truth.json, 313 EXTRA / 95 SPECIAL /
/// 22 UNCERTAIN) and ships only because it produced ZERO false demotions.
/// Rules the same measurement REJECTED, for the record — do not reintroduce
/// without new evidence:
///   runtime &lt; 45                     20.8% false-positive rate
///   prefix repeated, no vocabulary    19.0%
///   prefix equal to the series name   45.0%
///   "Part N" numbering                47.0%
/// </summary>
public sealed class SeasonZeroBatches
{
    private readonly IReadOnlySet<string> _protectedIds;
    private readonly IReadOnlySet<string> _batchIds;
    private readonly IReadOnlySet<string> _vocabularyPrefixKeys;

    private SeasonZeroBatches(
        IReadOnlySet<string> protectedIds, IReadOnlySet<string> batchIds, IReadOnlySet<string> vocabularyPrefixKeys)
    {
        _protectedIds = protectedIds;
        _batchIds = batchIds;
        _vocabularyPrefixKeys = vocabularyPrefixKeys;
    }

    /// <summary>Stable per-episode identity within one series.</summary>
    public static string IdentityOf(RemoteEpisode e)
        => e.SourceEpisodeId ?? $"{e.Season}|{e.Number}|{e.Title}";

    /// <summary>
    /// Builds the series-level view. <paramref name="regularEpisodeTitles"/> is
    /// the series' real episode titles, used by the "same title as a real
    /// episode" protection.
    /// </summary>
    public static SeasonZeroBatches Analyze(
        IReadOnlyList<RemoteEpisode> seasonZero,
        string? seriesName,
        IReadOnlyCollection<string> regularEpisodeTitles,
        ClassifierOptions options)
    {
        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        var batchIds = new HashSet<string>(StringComparer.Ordinal);
        var specials = seasonZero.Where(e => e.IsSpecial).ToList();

        // ---- protections (measured 100% precise; run before any demotion) --
        var realTitles = new HashSet<string>(
            regularEpisodeTitles.Select(TitleKeys.Normalize).Where(t => t is not null)!,
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in specials)
        {
            // A season-0 row that carries the same title as one of the series'
            // real episodes is that episode (an alternate cut or a listing
            // quirk), not bonus material.
            var key = TitleKeys.Normalize(e.Title);
            if (key is not null && realTitles.Contains(key))
            {
                protectedIds.Add(IdentityOf(e));
                continue;
            }
            // Feature films and uncut editions are content in their own right.
            if (ContentClassifier.MatchesExtraPattern(e.Title, ProtectedTitlePatterns))
            {
                protectedIds.Add(IdentityOf(e));
            }
        }

        // ---- rule 5: same-air-date batch (>=5) ----------------------------
        // A studio dumping five or more season-0 rows on one date is publishing
        // a bonus batch. Measured 59 fires, 0 false demotions.
        foreach (var day in specials.Where(e => e.AiredAt.HasValue).GroupBy(e => e.AiredAt!.Value.Date))
        {
            if (day.Count() >= SameDayBatchSize)
            {
                foreach (var e in day)
                {
                    batchIds.Add(IdentityOf(e));
                }
            }
        }

        // ---- rule 6: runtime-gated prefix batch ---------------------------
        // Four conditions together; the naive ">=3 sharing a prefix" form was
        // measured at 35 false demotions (it wrongly hides Workaholics'
        // "5th Year" scripted web series), and raising the count to >=5 removes
        // the false positives but catches nothing new — hence these gates.
        var seriesKey = PrefixGroups.KeyFor(seriesName);
        foreach (var group in specials.GroupBy(e => PrefixGroups.KeyFor(e) ?? string.Empty))
        {
            if (group.Key.Length == 0 || group.Count() < 3)
            {
                continue;
            }
            if (seriesKey is not null && string.Equals(group.Key, seriesKey, StringComparison.OrdinalIgnoreCase))
            {
                continue; // "South Park: ..." style runs are the show itself
            }
            var distinctDates = group.Where(e => e.AiredAt.HasValue).Select(e => e.AiredAt!.Value.Date).Distinct().Count();
            if (distinctDates > MaxBatchAirDates)
            {
                continue; // a run spread over many dates is a real sub-series
            }
            if (group.Any(e => e.RuntimeMinutes >= EpisodeLengthMinutes))
            {
                continue; // an episode-length member vouches for the whole run
            }
            foreach (var e in group)
            {
                batchIds.Add(IdentityOf(e));
            }
        }

        return new SeasonZeroBatches(protectedIds, batchIds, PrefixGroups.DemotableKeys(seasonZero, options));
    }

    /// <summary>Final verdict for one season-0 episode of this series.</summary>
    public ContentKind Classify(RemoteEpisode episode, ClassifierOptions options)
    {
        if (!episode.IsSpecial)
        {
            return ContentClassifier.Classify(episode, options);
        }
        if (_protectedIds.Contains(IdentityOf(episode)))
        {
            return ContentKind.Special;
        }

        var kind = ContentClassifier.Classify(episode, options);
        if (kind != ContentKind.Special)
        {
            return kind;
        }
        if (_batchIds.Contains(IdentityOf(episode)))
        {
            return ContentKind.Extra;
        }
        if (!episode.RuntimeMinutes.HasValue
            && PrefixGroups.KeyFor(episode) is string key && _vocabularyPrefixKeys.Contains(key))
        {
            return ContentKind.Extra;
        }
        return ContentKind.Special;
    }

    /// <summary>Five or more season-0 rows on one air date is a bonus batch.</summary>
    public const int SameDayBatchSize = 5;

    /// <summary>A prefix run spanning more than this many air dates is a real sub-series.</summary>
    public const int MaxBatchAirDates = 2;

    /// <summary>At or above this runtime an item is episode-length, whatever else says.</summary>
    public const int EpisodeLengthMinutes = 20;

    /// <summary>Titles that always denote content (measured 100% precise).</summary>
    public static readonly IReadOnlyList<string> ProtectedTitlePatterns = new[]
    {
        @"re:\bmovie\b", @"re:\buncut\b",
    };
}
