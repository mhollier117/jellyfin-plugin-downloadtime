// Repeated-prefix + vocabulary gate, and TVmaze significance (2026-08-05).
//
// The safe form of the structural heuristic: a run of >=3 season-0 items that
// share a title prefix is only demoted when the PREFIX ITSELF carries
// production vocabulary, no member is feature-length, and only the members
// with unknown runtime are hidden. Spinoff/crossover runs with no vocabulary
// signal ("No Prep Kings", "Street Outlaws vs. Fast N' Loud", "South Park")
// therefore stay Special no matter how long the run is.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class PrefixGroupTests
{
    private static ClassifierOptions Opts(int threshold = 15)
        => new(ContentClassifier.DefaultExtraPatterns, threshold);

    private static RemoteEpisode Sp(string title, int? runtime = null)
        => new(0, null, title, null, true, title, RuntimeMinutes: runtime);

    private static IReadOnlySet<string> Keys(IEnumerable<RemoteEpisode> eps)
        => PrefixGroups.DemotableKeys(eps.ToList(), Opts());

    private static bool Demoted(IReadOnlyList<RemoteEpisode> all, RemoteEpisode e)
        => PrefixGroups.DemotableKeys(all, Opts()).Contains(PrefixGroups.KeyFor(e)!)
           && !e.RuntimeMinutes.HasValue;

    // ------------------------------------------------------- prefix rule --

    [Fact]
    public void KeyFor_UsesTextBeforeFirstColon()
        => Assert.Equal("early cuts", PrefixGroups.KeyFor(Sp("Early Cuts: Gene Marshall (Chapter 1)")));

    [Fact]
    public void KeyFor_FallsBackToFirstThreeWords()
        => Assert.Equal("season 2 stories", PrefixGroups.KeyFor(Sp("Season 2 Stories From the Bunkhouse - Episode 01")));

    // ---------------------------------------------------- groups to catch --

    public static IEnumerable<object[]> CatchGroups() => new[]
    {
        new object[] { "Season 2 Stories From the Bunkhouse - Episode {0}", 8 },
        new object[] { "Creating Westworld's Reality: {0}", 10 },
        new object[] { "The Big Moment: Episode {0}", 7 },
        new object[] { "Early Cuts: Chapter {0}", 6 },
        new object[] { "Diaries: Part {0}", 3 },
    };

    [Theory]
    [MemberData(nameof(CatchGroups))]
    public void VocabularyPrefixRuns_AreDemoted(string template, int count)
    {
        var eps = Enumerable.Range(1, count)
            .Select(i => Sp(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, i)))
            .ToList();
        Assert.All(eps, e => Assert.True(Demoted(eps, e), e.Title));
    }

    // ------------------------------------------------- groups to preserve --

    public static IEnumerable<object[]> SpareGroups() => new[]
    {
        new object[] { "No Prep Kings: Episode {0}", 8 },
        new object[] { "Street Outlaws vs. Fast N' Loud {0}", 11 },
        new object[] { "South Park: Episode {0}", 7 },
        new object[] { "Countdown to 199{0}", 3 },
        new object[] { "Fastest in America {0}", 4 },
        new object[] { "Bad Hair Day {0}", 5 },
    };

    [Theory]
    [MemberData(nameof(SpareGroups))]
    public void RunsWithoutVocabulary_StaySpecial(string template, int count)
    {
        var eps = Enumerable.Range(1, count)
            .Select(i => Sp(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, i)))
            .ToList();
        Assert.Empty(Keys(eps));
        Assert.All(eps, e => Assert.False(Demoted(eps, e), e.Title));
    }

    // ------------------------------------------------------------ guards --

    [Fact]
    public void RunSmallerThanThree_IsIgnored()
    {
        var eps = new[] { Sp("Early Cuts: One"), Sp("Early Cuts: Two") };
        Assert.Empty(Keys(eps));
    }

    [Fact]
    public void AnyFeatureLengthMember_SparesTheWholeGroup()
    {
        // GoT "The Game Revealed" mixes unknown-runtime and 60 min members.
        var eps = new[]
        {
            Sp("The Game Revealed: Season 6 Episode 1"),
            Sp("The Game Revealed: Season 6 Episode 3"),
            Sp("The Game Revealed: Season 7 Episode 2", 60),
        };
        Assert.Empty(Keys(eps));
    }

    [Fact]
    public void MembersWithKnownShortRuntime_AreNotTouchedByThisRule()
    {
        // The runtime rule already owns those; the prefix rule only ever
        // hides UNKNOWN-runtime members.
        var eps = new[] { Sp("Diaries: A", 4), Sp("Diaries: B", 5), Sp("Diaries: C") };
        var keys = Keys(eps);
        Assert.Single(keys);
        Assert.False(Demoted(eps, eps[0]));
        Assert.True(Demoted(eps, eps[2]));
    }

    [Fact]
    public void GroupsAreScopedToOneSeries_ByConstruction()
    {
        // Callers pass a single series' season-0 set; two unrelated singletons
        // must never combine into a run.
        var eps = new[] { Sp("Diaries: A"), Sp("Something Else") };
        Assert.Empty(Keys(eps));
    }
}

/// <summary>TVmaze per-episode significance (authoritative where present).</summary>
public class TvmazeSignificanceTests
{
    private static ClassifierOptions Opts() => new(ContentClassifier.DefaultExtraPatterns, 15);

    private static RemoteEpisode Sp(string title, int? runtime, string? significance)
        => new(0, 1, "x", null, true, title, RuntimeMinutes: runtime, SourceSignificance: significance);

    [Fact]
    public void InsignificantSpecial_IsExtra_EvenWhenFeatureLength()
    {
        // GoT "You Win or You Die" is a 60 min BTS piece TVmaze marks
        // insignificant; "Greatest Moments" runs 120 min.
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("You Win or You Die", 60, "insignificant"), Opts()));
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Greatest Moments", 120, "insignificant"), Opts()));
    }

    [Fact]
    public void SignificantSpecial_StaysSpecial_EvenWhenShort()
        => Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("A Christmas Special", 8, "significant"), Opts()));

    [Fact]
    public void NoSignificance_FallsThroughToExistingRules()
    {
        Assert.Equal(ContentKind.Extra, ContentClassifier.Classify(Sp("Some Short Thing", 4, null), Opts()));
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Some Long Thing", 90, null), Opts()));
        Assert.Equal(ContentKind.Special, ContentClassifier.Classify(Sp("Unknown Thing", null, null), Opts()));
    }

    [Fact]
    public void Enricher_CarriesSignificanceAndRuntime()
    {
        var cat = new RemoteCatalog("Tvdb", "Tvdb", "121361", true, new[]
        {
            new RemoteEpisode(0, 1, "t1", null, true, "You Win or You Die"),
            new RemoteEpisode(0, 2, "t2", null, true, "Unmatched Item"),
        });
        var source = new[]
        {
            new RemoteEpisode(0, 3, null, null, true, "You Win or You Die", RuntimeMinutes: 60, SourceSignificance: "insignificant"),
        };
        var result = RuntimeEnricher.Enrich(cat, source);
        var enriched = result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t1");
        Assert.Equal(60, enriched.RuntimeMinutes);
        Assert.Equal("insignificant", enriched.SourceSignificance);
        Assert.Null(result.Catalog.Episodes.Single(e => e.SourceEpisodeId == "t2").SourceSignificance);
    }
}
