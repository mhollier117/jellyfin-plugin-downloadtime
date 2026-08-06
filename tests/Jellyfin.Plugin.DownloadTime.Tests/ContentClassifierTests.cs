// Specials-vs-extras classification (feature 2026-08-05). User intent:
// "idgaf about extras but actual specials i care about".
//
// Priority (coordinator's verified evidence):
//  (a) AniDB epno type is AUTHORITATIVE where present - 2 => Special,
//      3/4/5/6 => Extra. It outranks runtime because anime has legitimately
//      short specials (Black Clover "Clover Clips" ~7 min) that a runtime
//      threshold would wrongly demote.
//  (b) Title patterns => Extra. These outrank runtime because live TMDB data
//      shows long extras: "Behind the Scenes Q&A with the Cast & Crew" (45m),
//      "Grey's Anatomy on Jimmy Kimmel Live!" (22m).
//  (c) Runtime known AND < threshold => Extra ("Under the Knife" 11m,
//      "Anatomy of a Pilot" 12m).
//  (d) Otherwise Special - CONSERVATIVE: a false Special is noise, a false
//      Extra hides a real gap.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ContentClassifierTests
{
    private static ClassifierOptions Opts(int threshold = 15, IReadOnlyList<string>? patterns = null)
        => new(patterns ?? ContentClassifier.DefaultExtraPatterns, threshold);

    private static RemoteEpisode Sp(string? title, int? runtime = null, string? typeCode = null)
        => new(0, 1, "x", null, true, title, null, null, runtime, typeCode);

    [Fact]
    public void RegularEpisode_IsEpisode()
    {
        var e = new RemoteEpisode(1, 1, "x", null, false, "Pilot", null, null, 42, "1");
        Assert.Equal(ContentKind.Episode, ContentClassifier.Classify(e, Opts()));
    }

    // ----------------------------------------------------------------- a --

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    public void AniDbNonContentTypes_AreExtra(string typeCode)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Opening 1", 90, typeCode), Opts()));

    [Theory] // live shapes: Shangri-La "Mini Anime" 2-3 min, Clover Clips 2-10 min
    [InlineData("Mini Anime 7", 2)]
    [InlineData("Clover Clips: Supersized! 2", 2)]
    [InlineData("Magic Episode 4: Magic to Make a Good Scent Come from the Body", 1)]
    [InlineData("Episode S1", 14)]
    public void AniDbType2_ShortKnownRuntime_IsDemoted(string title, int runtime)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, runtime, "2"), Opts()));

    [Theory] // the long ones must survive: Slime movie 110m, Abominable Bride 90m
    [InlineData("Complete Movie", 110)]
    [InlineData("The Abominable Bride", 90)]
    [InlineData("The Journey So Far", 86)]
    [InlineData("Episode S1", 15)]
    public void AniDbType2_LongRuntime_StaysSpecial(string title, int runtime)
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(title, runtime, "2"), Opts()));

    [Theory] // no runtime = no signal = conservative Special, type 2 or not
    [InlineData("Episode S1")]
    [InlineData("The Day Naruto Became Hokage")]
    [InlineData("Jump Festa 2003 - Find the Crimson Four-leaf Clover!")]
    public void AniDbType2_UnknownRuntime_StaysSpecial(string title)
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(title, null, "2"), Opts()));

    [Theory] // the eight live anime items with unambiguous extras titles
    [InlineData("Behind the Scenes of Dr. Stone")]
    [InlineData("Dr. Stone Special Feature")]
    [InlineData("Behind the Scenes of Food Wars")]
    [InlineData("Completed Screening Theater Greeting Event")]
    [InlineData("Advance Screening Stage Greeting")]
    [InlineData("Episode 7 Funimation Video Commentary")]
    [InlineData("Episode 14 Funimation Video Commentary")]
    [InlineData("Special Bonus Episode: Get Yourselves Caught Up with This Light-Novel Recap")]
    public void AniDbType2_WithExtrasTitle_IsDemotedToExtra(string title)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, null, "2"), Opts()));

    [Fact]
    public void AniDbNonContentTypes_StayTerminalExtra_EvenWithInnocentTitle()
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Opening 1", 90, "3"), Opts()));

    // ----------------------------------------------------------------- b --

    [Theory]
    [InlineData("Behind the Scenes Q&A with the Cast & Crew", 45)]
    [InlineData("Grey's Anatomy on Jimmy Kimmel Live!", 22)]
    [InlineData("The Making of The Walking Dead", 60)]
    [InlineData("Comic-Con 2015 Writers Panel", 55)]
    [InlineData("Gag Reel", 30)]
    [InlineData("Deleted Scenes", 40)]
    [InlineData("Season 2 Recap", 25)]
    [InlineData("Inside the Episode", 21)]
    [InlineData("Audio Commentary", 44)]
    [InlineData("Sneak Peek", 30)]
    public void ExtraTitlePattern_BeatsLongRuntime(string title, int runtime)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, runtime), Opts()));

    [Fact]
    public void TitleMatching_IsCaseInsensitive()
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("BEHIND THE SCENES", 45), Opts()));

    [Fact]
    public void CustomPatternList_Replaces_Defaults()
    {
        var custom = new[] { "zzz-marker" };
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Behind the Scenes", 45), Opts(patterns: custom)));
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("A zzz-marker thing", 45), Opts(patterns: custom)));
    }

    // ----------------------------------------------------------------- c --

    [Fact]
    public void ShortRuntime_NoPattern_IsExtra()
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Under the Knife", 11), Opts()));

    [Fact]
    public void RuntimeThreshold_IsConfigurable()
    {
        var ep = Sp("A Plain Untagged Title", 20);
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(ep, Opts(threshold: 15)));
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(ep, Opts(threshold: 25)));
    }

    [Fact]
    public void RuntimeExactlyAtThreshold_IsSpecial()
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Mystery Item", 15), Opts()));

    // ----------------------------------------------------------------- d --

    [Fact]
    public void UnknownRuntime_NoPattern_IsSpecial_Conservative()
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("The Christmas Invasion", null), Opts()));

    [Fact]
    public void LongRuntime_NoPattern_IsSpecial()
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("The Christmas Invasion", 60), Opts()));

    [Fact]
    public void NullTitle_UnknownRuntime_IsSpecial_Conservative()
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(null), Opts()));

    // ------------------------------------------------- new vocabulary (F3) --

    [Theory] // live Top Gear strings that leaked past the 1.3.7.0 regex
    [InlineData("Best of Season 15 (1)")]
    [InlineData("Best of Season 16 (2)")]
    [InlineData("Best of Season 17 and 18 (3)")]
    [InlineData("Best of Season 19 (1)")]
    [InlineData("Best of Season 20 and Season 21 (4)")]
    [InlineData("Best of Top Gear Series 29 & 30 (1)")]
    public void BestOfSeasonCompilations_AreExtra(string title)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, 60), Opts()));

    [Theory]
    [InlineData("Series 2 Best of")]
    [InlineData("Best of '14-'15 (2)")]
    [InlineData("Best Of - Episode 4")]
    [InlineData("Sherlock Uncovered: The Women")]
    [InlineData("Advance Screening Stage Greeting")]
    [InlineData("Completed Screening Theater Greeting Event")]
    [InlineData("Episode 7 Funimation Video Commentary")]
    [InlineData("A Webisode")]
    public void CompilationAndPromoVocabulary_IsExtra(string title)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, 60), Opts()));

    [Theory] // "best of" must not swallow legitimate special titles
    [InlineData("The Best of Both Worlds")]
    [InlineData("The Christmas Invasion")]
    [InlineData("The Abominable Bride")]
    [InlineData("The Big Send Off Special")]
    [InlineData("An Evening with Top Gear")]
    public void LegitimateSpecialTitles_AreNotDemoted(string title)
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(title, 72), Opts()));

    // ------------------------------- unknown-runtime vocabulary (v1.3.8) --

    [Theory] // Yellowstone / TWD / Dexter / Shameless live shapes
    [InlineData("Season 2 Behind the Story - Episode 07")]
    [InlineData("Behind the Story")]
    [InlineData("Inside Yellowstone")]
    [InlineData("Inside The Walking Dead: The Last Episodes")]
    [InlineData("Inside Episode 402")]
    [InlineData("Early Cuts: Alex Timmons Motion Comic")]
    [InlineData("A Sitdown with Michael C. Hall")]
    [InlineData("A Shameless Discussion about Family")]
    public void UnknownRuntimeVocabulary_IsExtra(string title)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, null), Opts()));

    [Theory] // "inside" is start-anchored and "behind the" needs a production
             // noun, so ordinary uses of both words survive
    [InlineData("The Man Inside")]
    [InlineData("Right Behind the Curve")]
    [InlineData("Behind the Wheel of a Dream")]
    [InlineData("Step Up")]
    public void InsideAndBehind_DoNotSwallowLegitimateTitles(string title)
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(title, null), Opts()));

    [Theory] // "<name> Revealed:" companion series (GoT "The Game Revealed")
    [InlineData("The Game Revealed: Season 6 Episode 1 & 2")]
    [InlineData("The Game Revealed: Season 7 Episode 2")]
    [InlineData("The Game Revealed: Season 8 Episode 3")]
    public void RevealedCompanionSeries_IsExtra(string title)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, 60), Opts()));

    [Theory] // scoped to the "Revealed:" head, so ordinary uses survive
    [InlineData("Secrets Revealed")]
    [InlineData("The Truth Revealed")]
    [InlineData("All Will Be Revealed")]
    public void RevealedInOrdinaryTitles_StaysSpecial(string title)
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(title, 60), Opts()));

    [Theory] // narrow, measured additions (v1.3.9)
    [InlineData("Access All Areas")]
    [InlineData("Blood Spatter 101")]
    [InlineData("Callouts 101")]
    public void MeasuredVocabularyAdditions_AreExtra(string title)
        => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(title, null), Opts()));

    [Theory] // "101" is END-anchored so ordinary numbering survives
    [InlineData("Episode 101")]
    [InlineData("Room 101 Revisited")]
    [InlineData("Unaired Pilot")]      // genuine unreleased content, never hidden
    public void MeasuredAdditions_DoNotOverReach(string title)
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(title, null), Opts()));

    [Fact]
    public void InsideAnchor_DemotesAnyTitleStartingWithInside_DocumentedRisk()
    {
        // All 38 live "Inside ..." season-0 items are companion pieces
        // (Inside The Walking Dead / Inside Episode 402 / Inside Yellowstone /
        // Inside Chester's Mill), so the anchor is worth its one risk: a real
        // special literally titled "Inside Man" would also be demoted.
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Inside Man", null), Opts()));
    }

    [Fact]
    public void StreetOutlawsStyleEpisodeTitles_StaySpecial()
    {
        // Genuine-looking episode titles with no extras signal: nothing was
        // invented for these; they must remain Special.
        foreach (var t in new[] { "Step Up", "Wild Horses", "Paint it Black", "No Prep Kings: Season 5" })
        {
            Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp(t, null), Opts()));
        }
    }

    [Fact]
    public void RegexPatterns_AreSupported_AndBadOnesAreIgnored()
    {
        var good = new[] { @"re:^promo\b" };
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Promo reel", 60), Opts(patterns: good)));
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("A promo reel", 60), Opts(patterns: good)));

        var broken = new[] { "re:[unclosed(", "behind the scenes" };
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Anything", 60), Opts(patterns: broken)));
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Behind the Scenes", 60), Opts(patterns: broken)));
    }

    [Fact]
    public void AltTitle_FromEnrichment_CanDemote()
    {
        var e = new RemoteEpisode(0, 1, "x", null, true, "Episode 104", null, null, 40, null, "Behind the Scenes: The Set");
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(e, Opts()));
    }

    [Fact]
    public void DefaultPatterns_CoverTheBriefedVocabulary()
    {
        string[] titles =
        {
            "Behind the Scenes", "The Making of Something", "Blooper Reel", "Cast Interview",
            "A Featurette", "Promo Reel", "Official Trailer", "Teaser", "Series Preview",
            "Catch Up on Season 1", "Deleted Scene", "Commentary Track", "Sneak Peek",
            "Inside the Episode", "A Webisode", "Web Series Part 1", "Special Feature",
            "Anatomy of a Pilot", "Post Mortem", "Writers Panel", "Comic-Con Special",
            "Music Video", "Clip Show", "Set Tour", "Q&A Session", "Recap of Season 3",
        };
        Assert.All(titles, t => Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp(t, 45), Opts())));
    }
}
