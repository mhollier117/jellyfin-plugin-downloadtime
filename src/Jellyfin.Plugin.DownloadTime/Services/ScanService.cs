using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;

namespace Jellyfin.Plugin.DownloadTime.Services;

public sealed record ScanSettings(
    bool EnableTvLane, bool EnableAnimeLane, bool EnableMovieLane,
    int GraceHours, bool IncludeSpecials, int MovieReleaseBufferDays,
    IReadOnlySet<string> ExcludedItemIds,
    TimeSpan ContinuingTtl, TimeSpan EndedTtl);

/// <summary>Orchestrates a full library scan (spec §2). Per-item failures are
/// isolated; a source outage can never read as "everything missing".</summary>
public class ScanService
{
    private readonly ILibraryReader _library;
    private readonly ITvdbSource _tvdb;
    private readonly ITvmazeSource _tvmaze;
    private readonly IAniDbSource _anidb;
    private readonly ITmdbSource _tmdb;
    private readonly CatalogCache _cache;
    private readonly IClock _clock;
    private int _scanning;

    public ScanService(ILibraryReader library, ITvdbSource tvdb, ITvmazeSource tvmaze,
                       IAniDbSource anidb, ITmdbSource tmdb, CatalogCache cache, IClock clock)
    {
        _library = library;
        _tvdb = tvdb;
        _tvmaze = tvmaze;
        _anidb = anidb;
        _tmdb = tmdb;
        _cache = cache;
        _clock = clock;
    }

    public bool IsScanning => Volatile.Read(ref _scanning) == 1;

    public IReadOnlyDictionary<Guid, (SeriesDiff Diff, RemoteCatalog Catalog)> LastDiffs { get; private set; }
        = new Dictionary<Guid, (SeriesDiff, RemoteCatalog)>();

    public async Task<ScanReport> ScanAsync(ScanSettings settings, bool fullRefresh, IProgress<double>? progress, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            throw new InvalidOperationException("A Download Time scan is already running.");
        }
        try
        {
            var started = _clock.UtcNow;
            var seriesReports = new List<SeriesReportDto>();
            var globalNotes = new List<string>();
            var diffs = new Dictionary<Guid, (SeriesDiff, RemoteCatalog)>();

            var allSeries = _library.GetSeries();
            var movies = _library.GetMovies();
            var totalUnits = Math.Max(1, allSeries.Count + 1);
            var done = 0;

            foreach (var series in allSeries)
            {
                ct.ThrowIfCancellationRequested();
                seriesReports.Add(await ScanSeriesAsync(series, settings, fullRefresh, diffs, ct).ConfigureAwait(false));
                progress?.Report(100.0 * ++done / totalUnits);
            }

            var collections = settings.EnableMovieLane
                ? await ScanMoviesAsync(movies, settings, fullRefresh, globalNotes, ct).ConfigureAwait(false)
                : new List<CollectionReportDto>();

            LastDiffs = diffs;
            progress?.Report(100);
            return new ScanReport(started, _clock.UtcNow, seriesReports, collections, globalNotes);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    private async Task<SeriesReportDto> ScanSeriesAsync(
        SeriesItemInfo series, ScanSettings settings, bool fullRefresh,
        Dictionary<Guid, (SeriesDiff, RemoteCatalog)> diffs, CancellationToken ct)
    {
        var route = SourceRouter.Route(series.Path, series.IsAnimeLibrary, series.ProviderIds);
        var lane = route.Kind switch
        {
            SourceKind.TvdbId => "Tvdb",
            SourceKind.AniDbId => "AniDB",
            SourceKind.TmdbId => "Tmdb",
            SourceKind.ImdbId => "Imdb",
            _ => "None",
        };
        SeriesReportDto Report(string? error, bool usedFallback = false, bool muted = false,
            IReadOnlyList<string>? notes = null, IReadOnlyList<MissingEpisodeDto>? missing = null)
            => new(series.Id, series.Name, lane, usedFallback, muted, error,
                   notes ?? Array.Empty<string>(), missing ?? Array.Empty<MissingEpisodeDto>());

        if (settings.ExcludedItemIds.Contains(series.Id.ToString("N")))
        {
            return Report(null, muted: true);
        }
        if (route.Kind == SourceKind.None)
        {
            return Report("No usable provider id (folder tag or Tvdb/Tmdb/Imdb metadata).");
        }
        var laneEnabled = route.Kind switch
        {
            SourceKind.AniDbId => settings.EnableAnimeLane,
            _ => settings.EnableTvLane,
        };
        if (!laneEnabled)
        {
            return Report(null, notes: new[] { "Lane disabled in settings." });
        }

        var usedFallback = false;
        RemoteCatalog? catalog = null;
        string? error = null;
        var cacheKey = $"{route.Kind}-{route.SourceId}".ToLowerInvariant();
        try
        {
            if (!fullRefresh)
            {
                catalog = _cache.TryGet<RemoteCatalog>(cacheKey, settings.EndedTtl) is { IsEnded: true } ended
                    ? ended
                    : _cache.TryGet<RemoteCatalog>(cacheKey, settings.ContinuingTtl);
            }
            if (catalog is null)
            {
                var outcome = route.Kind switch
                {
                    SourceKind.TvdbId => await _tvdb.FetchByTvdbIdAsync(route.SourceId, ct).ConfigureAwait(false),
                    SourceKind.AniDbId => await _anidb.FetchByAnimeIdAsync(route.SourceId, ct).ConfigureAwait(false),
                    SourceKind.TmdbId => await _tmdb.FetchSeriesAsync(route.SourceId, ct).ConfigureAwait(false),
                    SourceKind.ImdbId => await _tvmaze.FetchByImdbIdAsync(route.SourceId, ct).ConfigureAwait(false),
                    _ => FetchOutcome.Fail("unroutable"),
                };
                if (outcome.Catalog is null && route.Kind == SourceKind.TvdbId)
                {
                    var fb = await _tvmaze.FetchByTvdbIdAsync(route.SourceId, ct).ConfigureAwait(false);
                    if (fb.Catalog is not null)
                    {
                        outcome = fb;
                        usedFallback = true;
                    }
                    else
                    {
                        outcome = FetchOutcome.Fail($"{outcome.Error}; fallback: {fb.Error}");
                    }
                }
                catalog = outcome.Catalog;
                error = outcome.Error;
                if (catalog is not null)
                {
                    _cache.Store(cacheKey, catalog);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Report($"Fetch crashed: {ex.Message}");
        }

        if (catalog is null)
        {
            return Report(error ?? "fetch failed");
        }
        if (catalog.Episodes.Count == 0 && series.Episodes.Count > 0)
        {
            return Report("Source returned zero episodes for a non-empty series (fail-safe).");
        }

        var diff = DiffEngine.Diff(series.Episodes, catalog,
            new DiffOptions(_clock.UtcNow, settings.GraceHours, settings.IncludeSpecials));
        diffs[series.Id] = (diff, catalog);
        var missing = diff.Missing
            .Select(m => new MissingEpisodeDto(m.Episode.Season, m.Episode.Number, m.Episode.Title,
                                               m.Episode.AiredAt, m.Kind.ToString(), m.Episode.SourceEpisodeId))
            .ToList();
        return Report(null, usedFallback, notes: diff.Notes, missing: missing);
    }

    private async Task<List<CollectionReportDto>> ScanMoviesAsync(
        IReadOnlyList<MovieItemInfo> movies, ScanSettings settings, bool fullRefresh,
        List<string> globalNotes, CancellationToken ct)
    {
        var results = new List<CollectionReportDto>();
        var ownedTmdbIds = movies.Where(m => m.TmdbId.HasValue).Select(m => m.TmdbId!.Value).ToHashSet();
        var seenCollections = new HashSet<int>();

        foreach (var movie in movies)
        {
            ct.ThrowIfCancellationRequested();
            if (!movie.TmdbId.HasValue)
            {
                continue;
            }
            if (settings.ExcludedItemIds.Contains(movie.Id.ToString("N")))
            {
                continue;
            }
            try
            {
                var cacheKey = $"tmdb-movie-{movie.TmdbId.Value}";
                var catalog = fullRefresh ? null : _cache.TryGet<CollectionCatalog>(cacheKey, settings.EndedTtl);
                if (catalog is null)
                {
                    var outcome = await _tmdb.FetchCollectionForMovieAsync(movie.TmdbId.Value, ct).ConfigureAwait(false);
                    if (outcome.NoCollection)
                    {
                        continue;
                    }
                    if (outcome.Catalog is null)
                    {
                        globalNotes.Add($"{movie.Name}: {outcome.Error}");
                        continue;
                    }
                    catalog = outcome.Catalog;
                    _cache.Store(cacheKey, catalog);
                }
                if (!seenCollections.Add(catalog.CollectionId))
                {
                    continue;
                }
                var missing = CollectionDiff.MissingMovies(ownedTmdbIds, catalog, _clock.UtcNow, settings.MovieReleaseBufferDays);
                if (missing.Count > 0)
                {
                    results.Add(new CollectionReportDto(
                        catalog.Name, movie.Name,
                        missing.Select(m => new MissingMovieDto(m.TmdbId, m.Title, m.ReleasedAt)).ToList()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalNotes.Add($"{movie.Name}: movie scan crashed: {ex.Message}");
            }
        }
        return results;
    }
}
