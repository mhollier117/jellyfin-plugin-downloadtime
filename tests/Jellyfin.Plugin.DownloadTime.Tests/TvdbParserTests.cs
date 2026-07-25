// Edge-case inventory:
// - REAL captured page (American Gods): 26 regular episodes parsed with correct first/last
//   (S01E01 "The Bone Orchard" id 5088686 aired 2017-04-30; S03E10 last), specials (S00) flagged IsSpecial,
//   per-episode TVDB ids extracted from hrefs, dates normalized via AirTime (23:59Z).
// - Rows with missing/unparseable date -> episode kept, AiredAt null.
// - MUTATED page (episode-label class renamed) -> ParseFailure error, NOT an empty success.
// - 404 page fixture -> fetcher returns FetchOutcome.Fail.
// - Fetcher resolves numeric id via /dereferrer/series/{id} redirect, then requests
//   /series/{slug}/allseasons/official.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class TvdbParserTests
{
    private static string Fix(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void RealPage_ParsesEpisodesWithIdsAndDates()
    {
        var (eps, error) = TvdbScrapeFetcher.ParseAllSeasons(Fix("tvdb-allseasons-american-gods.html"));
        Assert.Null(error);
        Assert.NotNull(eps);
        var regular = eps!.Where(e => !e.IsSpecial).ToList();
        Assert.Equal(26, regular.Count);
        var first = regular.First(e => e.Season == 1 && e.Number == 1);
        Assert.Equal("5088686", first.SourceEpisodeId);
        Assert.Equal("The Bone Orchard", first.Title);
        Assert.Equal(new DateTimeOffset(2017, 4, 30, 23, 59, 0, TimeSpan.Zero), first.AiredAt);
        Assert.Contains(regular, e => e.Season == 3 && e.Number == 10);
        // ground truth: this capture has NO S00 rows (26 episodes, S1=8 S2=8 S3=10)
        Assert.DoesNotContain(eps!, e => e.IsSpecial);
        Assert.Equal(26, eps!.Count);
    }

    [Fact]
    public void SpecialsRow_S00_FlaggedSpecial_MissingDateTolerated()
    {
        var html = """
            <html><body><ul>
            <li class="list-group-item">
              <h4 class="list-group-item-heading">
                <span class="text-muted episode-label">S00E01</span>
                <a href="/series/x/episodes/111">A Special</a>
              </h4>
              <ul class="list-inline text-muted"><li>January 2, 2020</li></ul>
            </li>
            <li class="list-group-item">
              <h4 class="list-group-item-heading">
                <span class="text-muted episode-label">S01E01</span>
                <a href="/series/x/episodes/222">Pilot</a>
              </h4>
              <ul class="list-inline text-muted"><li></li></ul>
            </li>
            </ul></body></html>
            """;
        var (eps, error) = TvdbScrapeFetcher.ParseAllSeasons(html);
        Assert.Null(error);
        var special = eps!.Single(e => e.SourceEpisodeId == "111");
        Assert.True(special.IsSpecial);
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 23, 59, 0, TimeSpan.Zero), special.AiredAt);
        var pilot = eps!.Single(e => e.SourceEpisodeId == "222");
        Assert.False(pilot.IsSpecial);
        Assert.Null(pilot.AiredAt); // empty date cell -> undated, episode still kept
    }

    [Fact]
    public void MutatedPage_ReturnsError_NotEmptySuccess()
    {
        var (eps, error) = TvdbScrapeFetcher.ParseAllSeasons(Fix("tvdb-allseasons-mutated.html"));
        Assert.Null(eps);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Fetcher_DereferrerRedirect_ThenAllSeasons()
    {
        var handler = new FakeHttpHandler(uri => uri.AbsolutePath switch
        {
            "/dereferrer/series/253573" => FakeHttpHandler.Redirect("https://www.thetvdb.com/series/american-gods"),
            "/series/american-gods" => FakeHttpHandler.Html("<html>series page</html>"),
            "/series/american-gods/allseasons/official" => FakeHttpHandler.Html(Fix("tvdb-allseasons-american-gods.html")),
            _ => FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound),
        });
        var fetcher = new TvdbScrapeFetcher(new HttpClient(handler), () => 0);
        var outcome = await fetcher.FetchByTvdbIdAsync("253573", CancellationToken.None);
        Assert.NotNull(outcome.Catalog);
        Assert.Equal("Tvdb", outcome.Catalog!.SourceKey);
        Assert.Equal("Tvdb", outcome.Catalog.IdProviderKey);
        Assert.Equal(26, outcome.Catalog.Episodes.Count(e => !e.IsSpecial));
    }

    [Fact]
    public async Task Fetcher_404_ReturnsFail()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(System.Net.HttpStatusCode.NotFound));
        var fetcher = new TvdbScrapeFetcher(new HttpClient(handler), () => 0);
        var outcome = await fetcher.FetchByTvdbIdAsync("999999999", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task Fetcher_MutatedMarkup_ReturnsFail()
    {
        var handler = new FakeHttpHandler(uri => uri.AbsolutePath switch
        {
            "/dereferrer/series/1" => FakeHttpHandler.Redirect("https://www.thetvdb.com/series/x"),
            "/series/x/allseasons/official" => FakeHttpHandler.Html(Fix("tvdb-allseasons-mutated.html")),
            _ => FakeHttpHandler.Html("<html></html>"),
        });
        var fetcher = new TvdbScrapeFetcher(new HttpClient(handler), () => 0);
        var outcome = await fetcher.FetchByTvdbIdAsync("1", CancellationToken.None);
        Assert.Null(outcome.Catalog);
        Assert.NotNull(outcome.Error);
    }
}
