// Edge-case inventory (spec §1 precedence):
// 1. anime library + AniDB provider id -> AniDbId, regardless of folder tag.
// 2. else folder tag [tvdbid-N]/[tmdbid-N]/[anidbid-N]/[imdbid-ttN] (case-insensitive) wins,
//    using the TAG value (files were matched under that identity).
// 3. else ProviderIds precedence Tvdb > Tmdb > Imdb.
// 4. nothing usable -> None.
// - tag with unknown source name ignored -> falls through to precedence.
// - anime library WITHOUT AniDB id falls through to tag/precedence.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class SourceRouterTests
{
    private static Dictionary<string, string> Ids(params (string K, string V)[] kv)
        => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void AnimeLibrary_WithAniDbId_WinsOverFolderTag()
    {
        var r = SourceRouter.Route(@"D:\Anime\7th Time Loop (2024) [tvdbid-435005]", true,
            Ids(("AniDB", "18164"), ("Tvdb", "435005")));
        Assert.Equal(new RouteDecision(SourceKind.AniDbId, "18164"), r);
    }

    [Fact]
    public void FolderTag_Tvdbid_Wins()
    {
        var r = SourceRouter.Route(@"D:\TV\American Gods (2017) [tvdbid-253573]", false,
            Ids(("Tvdb", "253573"), ("Tmdb", "46639")));
        Assert.Equal(new RouteDecision(SourceKind.TvdbId, "253573"), r);
    }

    [Fact]
    public void FolderTag_Tmdbid_Wins_EvenWhenTvdbIdPresent()
    {
        var r = SourceRouter.Route(@"D:\TV\Alice in Borderland (2020) [tmdbid-110316]", false,
            Ids(("Tvdb", "289181"), ("Tmdb", "110316")));
        Assert.Equal(new RouteDecision(SourceKind.TmdbId, "110316"), r);
    }

    [Fact]
    public void FolderTag_CaseInsensitive_AndImdb()
    {
        var r = SourceRouter.Route(@"D:\TV\Some Show [IMDBID-tt1898069]", false, Ids());
        Assert.Equal(new RouteDecision(SourceKind.ImdbId, "tt1898069"), r);
    }

    [Fact]
    public void NoTag_ProviderPrecedence_TvdbFirst_ThenTmdb_ThenImdb()
    {
        Assert.Equal(new RouteDecision(SourceKind.TvdbId, "1"),
            SourceRouter.Route(@"D:\TV\X", false, Ids(("Tvdb", "1"), ("Tmdb", "2"), ("Imdb", "tt3"))));
        Assert.Equal(new RouteDecision(SourceKind.TmdbId, "2"),
            SourceRouter.Route(@"D:\TV\X", false, Ids(("Tmdb", "2"), ("Imdb", "tt3"))));
        Assert.Equal(new RouteDecision(SourceKind.ImdbId, "tt3"),
            SourceRouter.Route(@"D:\TV\X", false, Ids(("Imdb", "tt3"))));
    }

    [Fact]
    public void UnknownTag_IgnoredAndFallsThrough()
    {
        var r = SourceRouter.Route(@"D:\TV\X [weirdid-9]", false, Ids(("Tmdb", "2")));
        Assert.Equal(new RouteDecision(SourceKind.TmdbId, "2"), r);
    }

    [Fact]
    public void AnimeLibrary_NoAniDbId_FallsThrough()
    {
        var r = SourceRouter.Route(@"D:\Anime\X [tvdbid-5]", true, Ids(("Tvdb", "5")));
        Assert.Equal(new RouteDecision(SourceKind.TvdbId, "5"), r);
    }

    [Fact]
    public void NothingUsable_None()
    {
        Assert.Equal(RouteDecision.None, SourceRouter.Route(@"D:\TV\X", false, Ids()));
    }
}
