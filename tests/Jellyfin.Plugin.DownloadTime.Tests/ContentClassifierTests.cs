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

    [Fact]
    public void AniDbType2_ShortRuntime_StaysSpecial()
    {
        // Black Clover "Clover Clips" ~7 min: authoritative type beats the
        // runtime threshold.
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Clover Clips", 7, "2"), Opts()));
    }

    [Fact]
    public void AniDbType2_ExtraLookingTitle_StaysSpecial()
    {
        // Authoritative type also outranks title heuristics.
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Behind the Scenes", 30, "2"), Opts()));
    }

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
