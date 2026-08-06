using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>What a missing item actually is, independent of Gap/New.</summary>
public enum ContentKind
{
    Episode,
    Special,
    Extra,
}

/// <summary>User-tunable classifier inputs (plugin settings).</summary>
public sealed record ClassifierOptions(IReadOnlyList<string> ExtraTitlePatterns, int ExtraRuntimeThresholdMinutes);

/// <summary>
/// Splits season-0 content into genuine SPECIALS (things a user wants) and
/// EXTRAS (bonus material). Feature 2026-08-05; user intent: "idgaf about
/// extras but actual specials i care about".
///
/// Priority, highest first:
///  (a) the source's own authoritative type — AniDB epno type 2 = special,
///      3/4/5/6 = credits/trailer/parody/other. It outranks everything
///      because anime has legitimately SHORT specials (Black Clover
///      "Clover Clips" ~7 min) that a runtime rule would wrongly demote.
///  (b) title patterns — verified live TMDB extras carry long runtimes
///      ("Behind the Scenes Q&amp;A with the Cast &amp; Crew" 45 min,
///      "... on Jimmy Kimmel Live!" 22 min), so patterns must beat runtime.
///  (c) runtime below the threshold ("Under the Knife" 11 min).
///  (d) otherwise SPECIAL — deliberately conservative: a false Special is
///      noise, a false Extra hides a real gap.
/// </summary>
public static class ContentClassifier
{
    /// <summary>Generic, source-agnostic extras vocabulary (user-editable in settings).</summary>
    public static readonly IReadOnlyList<string> DefaultExtraPatterns = new[]
    {
        // "behind the <X>" where X is a production noun. Deliberately NOT a
        // bare "behind the": the live corpus is "Behind the Story" (41),
        // "Behind the Character" (3), "Behind the Menu", "Behind The Dead",
        // and an unrestricted match would swallow real titles that merely use
        // the words ("Right Behind the Curve"). Mid-title matches are allowed
        // because Yellowstone writes "Season 2 Behind the Story - Episode 07".
        @"re:\bbehind the (scenes|story|character|characters|camera|curtain|magic|music|menu|action|mask|makeup|lens|dead)\b",
        "making of", "gag reel", "blooper", "interview", "featurette",
        // "Inside <show>" / "Inside Episode N" companion pieces (TWD, Yellowstone).
        // Anchored so a legitimate title like "Inside Man" is unaffected.
        @"re:^inside\b",
        "motion comic", "a sitdown with", "sit down with", "discussion about",
        "promo", "trailer", "teaser", "preview", "recap", "catch up", "catch-up",
        "deleted scene", "commentary", "sneak peek", "inside the episode", "webisode",
        "web series", "special feature", "anatomy of", "post mortem", "postmortem",
        "panel", "comic-con", "comic con", "music video", "clip show", "set tour",
        "q&a", "q & a", "aftershow", "after show", "talk show", "red carpet",
        "first look", "on set", "bonus feature", "extended look", "season in review",
        // talk-show appearances are promo spots, not episodes (live TMDB:
        // "Grey's Anatomy on Jimmy Kimmel Live!" 22 min)
        "jimmy kimmel", "jimmy fallon", "tonight show", "late show", "late night",
        "stephen colbert", "conan", "graham norton", "the view", "good morning america",
        // compilation/promo vocabulary the 2026-08-05 ground truth exposed
        // ("Series 2 Best of", "Sherlock Uncovered", "Advance Screening Stage
        // Greeting", "Funimation Video Commentary").
        // "best of" needs precision: it must catch compilation titles without
        // swallowing real ones like "The Best of Both Worlds", so it is a
        // regex requiring the phrase to END the title or be followed by
        // punctuation/digits rather than continuing into a noun phrase.
        // Matches compilation phrasing — "Best of" at the end, before
        // punctuation/a year, or followed (within a few words) by
        // season/series/episode — while sparing "The Best of Both Worlds".
        @"re:\bbest[- ]?of\b(?:\s*$|\s*[^\w\s]|\s+(?:\w+\s+){0,3}(?:season|series|episode)s?\b)",
        "uncovered", "stage greeting", "screening",
        // "<name> Revealed:" companion series (GoT "The Game Revealed: Season
        // 7 Episode 2"). Scoped to the colon so ordinary titles that merely
        // end in the word ("Secrets Revealed") are untouched; catches 17/17
        // of the live corpus.
        @"re:\brevealed\b\s*:",
        // ---- recap vocabulary (rule 3) and behind-the-scenes vocabulary
        // (rule 4). Every phrase here was scored against the 430-row labelled
        // fixture and kept only at ZERO hits on SPECIAL-labelled titles;
        // "road to" and "countdown to" were dropped for exactly that reason
        // (they hit Street Outlaws episodes).
        "story so far", "prequel", "origins", "look back", "retrospective", "farewell",
        "hall of shame", "discussion", "bringing", "actor", "in the studio",
        "tales from", "hours at", "b-roll", "vfx", "outtake", "table read",
        "after hours", "secrets of", "aftermath", "moments", "celebration", "anniversary",
        // measured single-purpose additions (2026-08-05 corpus)
        // NOTE: a "<subject> 101" pattern shipped in 1.3.9.0 was REMOVED after
        // the hand review labelled Street Outlaws' "Callouts 101" a genuine
        // special — it was the rule stack's only false demotion.
        "access all areas",
        "video commentary", "audio commentary", "compilation",
    };

    public static ContentKind Classify(RemoteEpisode episode, ClassifierOptions options)
    {
        if (!episode.IsSpecial)
        {
            return ContentKind.Episode;
        }

        // (a) non-content source types are terminal extras (AniDB credits,
        //     trailers, parodies, other) whatever they are called.
        switch (episode.SourceTypeCode)
        {
            case "3":
            case "4":
            case "5":
            case "6":
                return ContentKind.Extra;
        }

        // (a1) TheTVDB's per-episode "Special Category" tag, scraped from the
        //      page we already fetch. Where a curator has tagged the item this
        //      is the best signal available: measured 30 correct demotions and
        //      8 correct protections with ZERO false demotions once the
        //      episode-length gate below is applied.
        var categories = SplitCategories(episode.SourceCategory);
        if (categories.Count > 0)
        {
            if (categories.Any(c => ContentCategories.Contains(c)))
            {
                return ContentKind.Special;   // authoritative content
            }
            if (categories.Any(c => BonusCategories.Contains(c))
                && !(episode.RuntimeMinutes is int tagRuntime
                     && tagRuntime >= SeasonZeroBatches.EpisodeLengthMinutes))
            {
                return ContentKind.Extra;
            }
        }

        // (a2) TVmaze marks each special significant or insignificant, but that
        //      axis is significance to SERIES CONTINUITY, not bonus-vs-content:
        //      it files 60-minute crossover episodes ("Street Outlaws vs. Fast
        //      N' Loud", Grimm's "Bad Hair Day" parts) as insignificant too.
        //      So it only implies "extra" for items that are not
        //      episode-length; an episode-length item is still an episode.
        //      MEASURED: ungated, this signal fires 49 times with 17 false
        //      demotions — a 34.7% false-positive rate — and also kills Rick
        //      and Morty's "Portal People". The runtime gate below is what
        //      makes it safe; never let significance demote on its own.
        if (string.Equals(episode.SourceSignificance, InsignificantSpecial, StringComparison.OrdinalIgnoreCase))
        {
            var episodeLength = episode.RuntimeMinutes is int insigRuntime
                                && insigRuntime >= options.ExtraRuntimeThresholdMinutes;
            if (!episodeLength)
            {
                return ContentKind.Extra;
            }
        }

        // (b) title patterns — checked against the item's own title AND any
        //     alternate title supplied by runtime enrichment. These now
        //     outrank AniDB type 2 as well: a type-2 row literally called
        //     "Behind the Scenes of Dr. Stone" is an extra (2026-08-05).
        if (MatchesExtraPattern(episode.Title, options.ExtraTitlePatterns)
            || MatchesExtraPattern(episode.AltTitle, options.ExtraTitlePatterns))
        {
            return ContentKind.Extra;
        }

        // (b2) an explicitly SIGNIFICANT special is real content, however
        //      short — terminal above the runtime rule.
        if (string.Equals(episode.SourceSignificance, SignificantSpecial, StringComparison.OrdinalIgnoreCase))
        {
            return ContentKind.Special;
        }

        // (c) runtime threshold — applies to AniDB type-2 rows too (2026-08-05
        //     live evidence: Shangri-La's 44 "Mini Anime" run 2-3 min, Frieren's
        //     "Magic Episode" shorts 1-2 min, Clover Clips 2-10 min). Genuine
        //     long specials keep their runtime and survive (Slime "Complete
        //     Movie" 110 min, "The Abominable Bride" 90 min).
        if (episode.RuntimeMinutes is int runtime && runtime < options.ExtraRuntimeThresholdMinutes)
        {
            return ContentKind.Extra;
        }

        // (d) known-good special typing, and the conservative default: an item
        //     with no runtime signal is never hidden.
        return ContentKind.Special;
    }

    /// <summary>Prefix marking a pattern as a regular expression rather than a plain substring.</summary>
    public const string RegexPrefix = "re:";

    /// <summary>TVmaze significance values carried on <see cref="RemoteEpisode.SourceSignificance"/>.</summary>
    public const string SignificantSpecial = "significant";
    public const string InsignificantSpecial = "insignificant";

    /// <summary>TheTVDB special categories that denote bonus material.</summary>
    public static readonly IReadOnlySet<string> BonusCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "behind the scenes/ makings of", "behind the scenes/makings of", "behind the scenes",
        "bloopers", "cast interviews", "deleted scenes", "extended scenes",
        "season recaps", "webisodes and shorts",
    };

    /// <summary>TheTVDB special categories that denote real content.</summary>
    public static readonly IReadOnlySet<string> ContentCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "episodic special", "movies", "ovas", "pilots",
    };

    /// <summary>Splits the '; '-joined tag list and normalizes whitespace/case.</summary>
    public static IReadOnlyList<string> SplitCategories(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return Array.Empty<string>();
        }
        return category.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => string.Join(' ', c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant())
            .Where(c => c.Length > 0)
            .ToList();
    }

    public static bool MatchesExtraPattern(string? title, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                continue;
            }
            if (p.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // A user-supplied bad pattern must never break a scan.
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            title, p[RegexPrefix.Length..],
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException) { }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
                continue;
            }
            if (title.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
