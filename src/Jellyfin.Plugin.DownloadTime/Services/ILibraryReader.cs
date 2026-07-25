using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

public interface ILibraryReader
{
    IReadOnlyList<SeriesItemInfo> GetSeries();
    IReadOnlyList<MovieItemInfo> GetMovies();
}
