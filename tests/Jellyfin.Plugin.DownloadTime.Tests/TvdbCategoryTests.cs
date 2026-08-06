// TheTVDB "Special Category" tags (2026-08-05). The tags are rendered inline
// on the all-seasons page we already scrape - no API key, no extra request:
//
//   <li class="list-group-item list-group-item-special">
//     <span class="label label-sunglow pull-right">Behind the Scenes/ Makings Of</span>
//     ...<a href="/series/<slug>/episodes/11268339">
//
// Vocabulary (taxonomy/episode/3787): Behind the Scenes/ Makings Of,
// Bloopers, Cast Interviews, Deleted Scenes, Extended Scenes, Season Recaps,
// Webisodes and Shorts | Episodic Special, Movies, OVAs, Pilots.
//
// Measured against the 430-row fixture over 26 TVDB-lane series (104 tagged
// episodes scraped, 52 joined to labelled rows):
//   ungated       40 correct extras, 8 correct protections, 1 FALSE DEMOTION
//                 (Hannibal "Ouf (NBC Web Version)", 43 min, tagged
//                  "Webisodes and Shorts" but a real alternate cut)
//   >=20 min gate 30 correct extras, 8 correct protections, ZERO false
//                 demotions  <-- shipped
// An episode-length item is an episode whatever the crowd-sourced tag says,
// exactly as for TVmaze significance.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TvdbCategoryTests
{
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
    private static ClassifierOptions Opts() => new(ContentClassifier.DefaultExtraPatterns, 15);

    private static RemoteEpisode Sp(string title, int? runtime, string? category)
        => new(0, 1, "x", null, true, title, RuntimeMinutes: runtime, SourceCategory: category);

    // ------------------------------------------------------------ parsing --

    [Fact]
    public void AllSeasonsPage_CapturesSpecialCategoryTags()
    {
        var (episodes, error) = TvdbScrapeFetcher.ParseAllSeasons(Fix("tvdb-allseasons-tagged.html"));
        Assert.Null(error);
        var byId = episodes!.ToDictionary(e => e.SourceEpisodeId!);
        // multiple labels on one row are all captured
        Assert.Equal("Behind the Scenes/ Makings Of; Cast Interviews", byId["11268339"].SourceCategory);
        Assert.Equal("Episodic Special", byId["11268340"].SourceCategory);
        Assert.Null(byId["11268341"].SourceCategory);   // untagged row
        Assert.Null(byId["3286391"].SourceCategory);    // regular episode
        Assert.Equal(0, byId["11268339"].Season);
    }

    // -------------------------------------------------------- classifying --

    [Theory]
    [InlineData("Behind the Scenes/ Makings Of")]
    [InlineData("Bloopers")]
    [InlineData("Cast Interviews")]
    [InlineData("Deleted Scenes")]
    [InlineData("Extended Scenes")]
    [InlineData("Season Recaps")]
    [InlineData("Webisodes and Shorts")]
    public void ExtraCategories_ShortOrUnknownRuntime_AreExtra(string category)
    {
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Neutral Title", null, category), Opts()));
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Neutral Title", 6, category), Opts()));
    }

    [Fact]
    public void ExtraCategory_EpisodeLength_StaysSpecial()
    {
        // the measured false demotion this gate exists to prevent
        Assert.Equal(ContentKind.Special,
            ContentClassifier.Classify(Sp("Ouf (NBC Web Version)", 43, "Webisodes and Shorts"), Opts()));
        Assert.Equal(ContentKind.Special,
            ContentClassifier.Classify(Sp("Borderline", 20, "Bloopers"), Opts()));
    }

    [Theory]
    [InlineData("Episodic Special")]
    [InlineData("Movies")]
    [InlineData("OVAs")]
    [InlineData("Pilots")]
    public void ContentCategories_AreProtected_EvenWhenShortOrPatterned(string category)
    {
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Neutral Title", 4, category), Opts()));
        // an authoritative content tag outranks a title that merely looks like an extra
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Behind the Scenes", null, category), Opts()));
    }

    [Fact]
    public void MultipleTags_ExtraWins_WhenNoContentTagPresent()
        => Assert.Equal(ContentKind.Extra,
            ContentClassifier.Classify(Sp("X", null, "Behind the Scenes/ Makings Of; Cast Interviews"), Opts()));

    [Fact]
    public void MultipleTags_ContentTagWins()
        => Assert.Equal(ContentKind.Special,
            ContentClassifier.Classify(Sp("X", null, "Behind the Scenes/ Makings Of; Movies"), Opts()));

    [Fact]
    public void UnknownOrAbsentCategory_FallsThroughToExistingRules()
    {
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Mystery", null, null), Opts()));
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Mystery", null, "Some Future Tag"), Opts()));
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Behind the Scenes", null, "Some Future Tag"), Opts()));
    }

    [Fact]
    public void CategoryMatching_ToleratesWhitespaceAndCase()
        => Assert.Equal(ContentKind.Extra,
            ContentClassifier.Classify(Sp("X", null, "  behind the scenes/  makings of  "), Opts()));
}
