using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DownloadTime.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Reads series/movies from the Jellyfin library into plain DTOs.
/// Virtual items are excluded from OWNED episodes (our own placeholders must
/// never count as owned). IsAnimeLibrary == item carries an AniDB id.</summary>
public class JellyfinLibraryReader : ILibraryReader
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinLibraryReader(ILibraryManager libraryManager) => _libraryManager = libraryManager;

    public IReadOnlyList<SeriesItemInfo> GetSeries()
    {
        var result = new List<SeriesItemInfo>();
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
        }).OfType<Series>();

        foreach (var s in series)
        {
            var owned = new List<OwnedEpisode>();
            foreach (var e in s.GetRecursiveChildren().OfType<Episode>())
            {
                if (e.IsVirtualItem || e.LocationType == LocationType.Virtual)
                {
                    continue;
                }
                owned.Add(new OwnedEpisode(
                    e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd,
                    new Dictionary<string, string>(e.ProviderIds, StringComparer.OrdinalIgnoreCase),
                    e.PremiereDate.HasValue ? new DateTimeOffset(e.PremiereDate.Value, TimeSpan.Zero) : null,
                    e.Name));
            }
            var providerIds = new Dictionary<string, string>(s.ProviderIds, StringComparer.OrdinalIgnoreCase);
            result.Add(new SeriesItemInfo(s.Id, s.Name, s.Path ?? string.Empty,
                providerIds.ContainsKey("AniDB"), providerIds, owned));
        }
        return result;
    }

    public IReadOnlyList<MovieItemInfo> GetMovies()
    {
        var result = new List<MovieItemInfo>();
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
        }).OfType<Movie>();
        foreach (var m in movies)
        {
            int? tmdb = null;
            if (m.ProviderIds.TryGetValue("Tmdb", out var t) && int.TryParse(t, out var parsed))
            {
                tmdb = parsed;
            }
            result.Add(new MovieItemInfo(m.Id, m.Name, tmdb));
        }
        return result;
    }
}
