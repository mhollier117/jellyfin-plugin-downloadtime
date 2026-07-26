// Edge-case inventory (entry-chain support, analysis doc 2026-07-26):
// - ParseEntry extracts ONLY type="Sequel" relation ids (prequel/side-story ignored);
//   entry without <relatedanime> -> empty sequel list; episode parsing identical to ParseAnime.
// - FetchEntryAsync returns catalog + sequel ids over HTTP (gzip path shared with FetchByAnimeIdAsync).
// - BuildUnion: Season = 1-based entry ordinal in given order; Number = epno within entry;
//   AbsoluteNumber cumulative over REGULAR episodes only (specials -> null AbsoluteNumber);
//   SynthesizedSeasons flag set; IdProviderKey "AniDB"; SeriesSourceId = first entry id;
//   IsEnded only when every entry ended; single-entry union still synthesized (Season=1).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AniDbChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void ParseEntry_ExtractsSequelIdsOnly()
    {
        var (catalog, sequels, error) = AniDbFetcher.ParseEntry(Fix("anidb-anime-18164.xml"), new FakeClock(Now));
        Assert.Null(error);
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "18800" }, sequels); // prequel 18000 and side story 18500 ignored
        Assert.Equal(5, catalog!.Episodes.Count);  // episode parsing unchanged by relatedanime block
    }

    [Fact]
    public void ParseEntry_NoRelatedAnime_EmptySequels()
    {
        var (_, sequels, error) = AniDbFetcher.ParseEntry(Fix("anidb-anime-18900.xml"), new FakeClock(Now));
        Assert.Null(error);
        Assert.Empty(sequels);
    }

    [Fact]
    public async Task FetchEntryAsync_ReturnsCatalogAndSequels()
    {
        var handler = new FakeHttpHandler(uri =>
            uri.Query.Contains("aid=18164") ? FakeHttpHandler.Xml(Fix("anidb-anime-18164.xml"))
                                            : FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound));
        var clock = new FakeClock(Now);
        var f = new AniDbFetcher(new HttpClient(handler), clock, _ => Task.CompletedTask, () => 0, () => ("testclient", 1));
        var outcome = await f.FetchEntryAsync("18164", CancellationToken.None);
        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal(new[] { "18800" }, outcome.SequelIds);
    }

    [Fact]
    public void BuildUnion_OrdinalsEpnosAbsolutes()
    {
        var clock = new FakeClock(Now);
        var e1 = AniDbFetcher.ParseEntry(Fix("anidb-anime-18164.xml"), clock).Catalog!; // 4 regular (one undated) + 1 special
        var e2 = AniDbFetcher.ParseEntry(Fix("anidb-anime-18800.xml"), clock).Catalog!; // 3 regular
        var e3 = AniDbFetcher.ParseEntry(Fix("anidb-anime-18900.xml"), clock).Catalog!; // 2 regular
        var union = AniDbChain.BuildUnion(new[] { e1, e2, e3 });

        Assert.True(union.SynthesizedSeasons);
        Assert.Equal("AniDB", union.IdProviderKey);
        Assert.Equal("18164", union.SeriesSourceId);
        Assert.Equal(10, union.Episodes.Count); // 5 + 3 + 2

        var s2e1 = union.Episodes.Single(e => e.SourceEpisodeId == "300001");
        Assert.Equal(2, s2e1.Season);            // entry ordinal
        Assert.Equal(1, s2e1.Number);            // epno within entry
        Assert.Equal(5, s2e1.AbsoluteNumber);    // 4 regulars in entry 1, then this

        var s3e2 = union.Episodes.Single(e => e.SourceEpisodeId == "310002");
        Assert.Equal(3, s3e2.Season);
        Assert.Equal(9, s3e2.AbsoluteNumber);    // 4 + 3 + 2

        var special = union.Episodes.Single(e => e.SourceEpisodeId == "290001");
        Assert.True(special.IsSpecial);
        Assert.Null(special.AbsoluteNumber);     // specials never consume absolute slots

        var e1ep1 = union.Episodes.Single(e => e.SourceEpisodeId == "274088");
        Assert.Equal(1, e1ep1.Season);
        Assert.Equal(1, e1ep1.AbsoluteNumber);
    }

    [Fact]
    public void BuildUnion_SingleEntry_StillSynthesizedSeasonOne()
    {
        var e1 = AniDbFetcher.ParseEntry(Fix("anidb-anime-18900.xml"), new FakeClock(Now)).Catalog!;
        var union = AniDbChain.BuildUnion(new[] { e1 });
        Assert.True(union.SynthesizedSeasons);
        Assert.All(union.Episodes, e => Assert.Equal(1, e.Season));
    }

    [Fact]
    public void BuildUnion_IsEnded_OnlyWhenAllEnded()
    {
        var clock = new FakeClock(Now);
        var ended = AniDbFetcher.ParseEntry(Fix("anidb-anime-18900.xml"), clock).Catalog!;   // ended 2026-01-11
        var airing = ended with { IsEnded = false };
        Assert.True(AniDbChain.BuildUnion(new[] { ended }).IsEnded);
        Assert.False(AniDbChain.BuildUnion(new[] { ended, airing }).IsEnded);
    }
}
