using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Movie-franchise gap detection (spec §2.3/§3).</summary>
public static class CollectionDiff
{
    public static IReadOnlyList<RemoteMovie> MissingMovies(
        ISet<int> ownedTmdbIds, CollectionCatalog catalog, DateTimeOffset now, int bufferDays)
        => catalog.Movies
            .Where(m => !ownedTmdbIds.Contains(m.TmdbId)
                        && m.ReleasedAt.HasValue
                        && m.ReleasedAt.Value.AddDays(bufferDays) < now)
            .ToList();
}
