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
        // measured single-purpose additions (2026-08-05 corpus)
        // "<subject> 101" primer shorts ("Blood Spatter 101", "Callouts 101").
        // End-anchored and guarded so ordinary numbering ("Episode 101") is
        // untouched.
        "access all areas", @"re:(?<!\bepisode )\b101$",
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

        // (a2) TVmaze marks each special significant or insignificant. Where
        //      present this is authoritative: GoT's "You Win or You Die" and
        //      "Greatest Moments" are insignificant despite running 60/120 min.
        if (string.Equals(episode.SourceSignificance, InsignificantSpecial, StringComparison.OrdinalIgnoreCase))
        {
            return ContentKind.Extra;
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
