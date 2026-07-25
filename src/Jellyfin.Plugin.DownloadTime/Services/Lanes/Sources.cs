using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

public interface ITvdbSource { Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct); }
public interface ITvmazeSource
{
    Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct);
    Task<FetchOutcome> FetchByImdbIdAsync(string imdbId, CancellationToken ct);
}
public interface IAniDbSource { Task<FetchOutcome> FetchByAnimeIdAsync(string anidbId, CancellationToken ct); }
public sealed record CollectionOutcome(CollectionCatalog? Catalog, string? Error, bool NoCollection);
public interface ITmdbSource
{
    Task<FetchOutcome> FetchSeriesAsync(string tmdbId, CancellationToken ct);
    Task<CollectionOutcome> FetchCollectionForMovieAsync(int movieTmdbId, CancellationToken ct);
}
