using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

public interface ITvdbSource { Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct); }
public interface ITvmazeSource
{
    Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct);
    Task<FetchOutcome> FetchByImdbIdAsync(string imdbId, CancellationToken ct);

    /// <summary>
    /// Season-0 items with TVmaze's authoritative per-episode significance
    /// (significant/insignificant) and runtime, for classification enrichment.
    /// Empty when the show is unknown there. Default keeps older fakes valid.
    /// </summary>
    Task<IReadOnlyList<RemoteEpisode>> FetchSpecialsByTvdbIdAsync(string tvdbId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RemoteEpisode>>(Array.Empty<RemoteEpisode>());
}
/// <summary>Single AniDB entry plus its outgoing Sequel relation ids.</summary>
public sealed record AniDbEntryOutcome(RemoteCatalog? Catalog, IReadOnlyList<string> SequelIds, string? Error);

/// <summary>Cache envelope for one AniDB entry (catalog + sequel ids).</summary>
public sealed record AniDbEntryCacheItem(RemoteCatalog Catalog, IReadOnlyList<string> SequelIds);

public interface IAniDbSource
{
    Task<FetchOutcome> FetchByAnimeIdAsync(string anidbId, CancellationToken ct);

    /// <summary>Entry fetch for chain walking. Default implementation adapts
    /// FetchByAnimeIdAsync with no sequels (keeps pre-chain fakes valid).</summary>
    async Task<AniDbEntryOutcome> FetchEntryAsync(string anidbId, CancellationToken ct)
    {
        var o = await FetchByAnimeIdAsync(anidbId, ct).ConfigureAwait(false);
        return new AniDbEntryOutcome(o.Catalog, Array.Empty<string>(), o.Error);
    }
}
public sealed record CollectionOutcome(CollectionCatalog? Catalog, string? Error, bool NoCollection);
public interface ITmdbSource
{
    Task<FetchOutcome> FetchSeriesAsync(string tmdbId, CancellationToken ct);
    Task<CollectionOutcome> FetchCollectionForMovieAsync(int movieTmdbId, CancellationToken ct);

    /// <summary>
    /// One season's episodes, used purely as a runtime/title enrichment
    /// sidecar for lanes whose source reports no duration (TVDB scrape).
    /// Returns an empty list when the season does not exist. Default
    /// implementation keeps pre-enrichment fakes valid.
    /// </summary>
    Task<IReadOnlyList<RemoteEpisode>> FetchSeasonEpisodesAsync(string tmdbId, int seasonNumber, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RemoteEpisode>>(Array.Empty<RemoteEpisode>());
}
