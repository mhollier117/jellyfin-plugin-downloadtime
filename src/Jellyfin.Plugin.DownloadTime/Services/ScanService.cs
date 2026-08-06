using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;

namespace Jellyfin.Plugin.DownloadTime.Services;

public sealed record ScanSettings(
    bool EnableTvLane, bool EnableAnimeLane, bool EnableMovieLane,
    int GraceHours, bool IncludeSpecials, int MovieReleaseBufferDays,
    IReadOnlySet<string> ExcludedItemIds,
    TimeSpan ContinuingTtl, TimeSpan EndedTtl,
    bool ReportExtras = false,
    IReadOnlyList<string>? ExtraTitlePatterns = null,
    int ExtraRuntimeThresholdMinutes = 15)
{
    public ClassifierOptions Classifier => new(
        ExtraTitlePatterns ?? ContentClassifier.DefaultExtraPatterns, ExtraRuntimeThresholdMinutes);
}

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

            // AniDB roots across the library: chain expansion must stop at
            // another series' root, or one report swallows the franchise
            // (audit D1, 2026-08-03).
            var routes = new Dictionary<Guid, RouteDecision>();
            var anidbRoots = new Dictionary<Guid, string>();
            foreach (var series in allSeries)
            {
                var route = SourceRouter.Route(series.Path, series.IsAnimeLibrary, series.ProviderIds);
                routes[series.Id] = route;
                if (route.Kind == SourceKind.AniDbId)
                {
                    anidbRoots[series.Id] = route.SourceId;
                }
            }
            var rootSet = new HashSet<string>(anidbRoots.Values, StringComparer.OrdinalIgnoreCase);

            // Phase 1: resolve every eligible series' entry chain (fetch+cache).
            var chains = new Dictionary<Guid, ChainResult>();
            if (settings.EnableAnimeLane)
            {
                foreach (var series in allSeries)
                {
                    if (!anidbRoots.TryGetValue(series.Id, out var root)
                        || settings.ExcludedItemIds.Contains(series.Id.ToString("N")))
                    {
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    chains[series.Id] = await FetchChainAsync(root, rootSet, settings, fullRefresh, ct).ConfigureAwait(false);
                }
            }

            // Phase 2: attribute each chain entry to the series whose root is
            // nearest (ties: lower root aid) — shared movie/special entries
            // are reported at most once (audit D3i).
            var entryOwner = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var bestClaim = new Dictionary<string, (int Dist, string Root)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (seriesId, chain) in chains)
            {
                if (chain.Error is not null)
                {
                    continue;
                }
                var root = anidbRoots[seriesId];
                foreach (var e in chain.Entries)
                {
                    if (!bestClaim.TryGetValue(e.Aid, out var cur)
                        || e.Distance < cur.Dist
                        || (e.Distance == cur.Dist && string.CompareOrdinal(root, cur.Root) < 0))
                    {
                        bestClaim[e.Aid] = (e.Distance, root);
                        entryOwner[e.Aid] = seriesId;
                    }
                }
            }

            foreach (var series in allSeries)
            {
                ct.ThrowIfCancellationRequested();
                seriesReports.Add(await ScanSeriesAsync(series, routes[series.Id], chains, entryOwner, settings, fullRefresh, diffs, ct).ConfigureAwait(false));
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
        SeriesItemInfo series, RouteDecision route,
        IReadOnlyDictionary<Guid, ChainResult> chains, IReadOnlyDictionary<string, Guid> entryOwner,
        ScanSettings settings, bool fullRefresh,
        Dictionary<Guid, (SeriesDiff, RemoteCatalog)> diffs, CancellationToken ct)
    {
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
        var chainNotes = new List<string>();
        var cacheKey = $"{route.Kind}-{route.SourceId}".ToLowerInvariant();
        try
        {
            if (route.Kind == SourceKind.AniDbId)
            {
                // Anime: diff against the union of this series' resolved entry
                // chain (analysis 2026-07-26 — merged AND split local layouts).
                // Chains were fetched up front; entries stop at other series'
                // roots (audit D1) and specials-only entries reachable from
                // several series belong to the nearest root only (audit D3i).
                if (!chains.TryGetValue(series.Id, out var chain))
                {
                    return Report("AniDB chain was not resolved for this series.");
                }
                if (chain.Error is not null)
                {
                    return Report(chain.Error);
                }
                chainNotes.AddRange(chain.Notes);
                var included = chain.Entries
                    .Where(e => e.Distance == 0
                                || e.Item.Catalog.Episodes.Any(ep => !ep.IsSpecial)
                                || (entryOwner.TryGetValue(e.Aid, out var owner) && owner == series.Id))
                    .Select(e => e.Item.Catalog)
                    .ToList();
                catalog = AniDbChain.BuildUnion(included);
                if (included.Count > 1)
                {
                    lane = $"AniDB ({included.Count} entries)";
                }
            }
            else
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
                    // Runtime enrichment sidecar (2026-08-05): the TVDB scrape
                    // reports no duration, leaving most of its specials with
                    // no classification signal. When the series also carries a
                    // Tmdb id, one extra season-0 call supplies runtimes and
                    // alternate titles. Detection still runs on THIS catalog.
                    catalog = await EnrichRuntimesAsync(catalog, series, route, chainNotes, ct).ConfigureAwait(false);
                    catalog = await EnrichSignificanceAsync(catalog, series, chainNotes, ct).ConfigureAwait(false);
                    _cache.Store(cacheKey, catalog);
                }
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

        // Series-scoped season-0 analysis: protections first, then the batch
        // and vocabulary-prefix rules. Every rule is validated against the
        // hand-labelled fixture by GroundTruthRegressionTests.
        var batches = SeasonZeroBatches.Analyze(
            catalog.Episodes,
            series.Name,
            catalog.Episodes.Where(e => !e.IsSpecial && e.Title is not null).Select(e => e.Title!).ToList(),
            settings.Classifier);
        ContentKind Classify(RemoteEpisode e) => batches.Classify(e, settings.Classifier);

        // Extras are bonus material: when they are hidden from the report they
        // must not become virtual placeholders either (placeholder planning
        // reads LastDiffs).
        if (!settings.ReportExtras)
        {
            var kept = diff.Missing
                .Where(m => Classify(m.Episode) != ContentKind.Extra)
                .ToList();
            if (kept.Count != diff.Missing.Count)
            {
                diff = new SeriesDiff(kept, diff.Notes);
            }
        }
        diffs[series.Id] = (diff, catalog);
        // Classification (Episode/Special/Extra) is a separate axis from Kind
        // (Gap/New). Extras are bonus material: hidden unless opted in.
        var missing = diff.Missing
            .Select(m => new
            {
                m.Kind,
                m.Episode,
                Kindness = Classify(m.Episode),
            })
            .Where(x => settings.ReportExtras || x.Kindness != ContentKind.Extra)
            .Select(x => new MissingEpisodeDto(x.Episode.Season, x.Episode.Number, x.Episode.Title,
                                               x.Episode.AiredAt, x.Kind.ToString(), x.Episode.SourceEpisodeId,
                                               x.Episode.IsSpecial, x.Episode.EntryName, x.Episode.AbsoluteNumber,
                                               x.Kindness.ToString(), x.Episode.RuntimeMinutes))
            .ToList();
        var allNotes = chainNotes.Count == 0 ? diff.Notes : chainNotes.Concat(diff.Notes).ToList();
        return Report(null, usedFallback, notes: allNotes, missing: missing);
    }

    /// <summary>
    /// Fills missing season-0 runtimes from TMDB when the routed source does
    /// not report duration. Purely additive: never changes which catalog
    /// drives detection, never adds/removes/renumbers claims. Conservative
    /// matching lives in RuntimeEnricher; failures degrade to a note.
    /// </summary>
    private async Task<RemoteCatalog> EnrichRuntimesAsync(
        RemoteCatalog catalog, SeriesItemInfo series, RouteDecision route, List<string> notes, CancellationToken ct)
    {
        if (route.Kind == SourceKind.TmdbId)
        {
            return catalog; // the TMDB lane already carries runtimes
        }
        var enrichable = catalog.Episodes.Count(e => e.IsSpecial && !e.RuntimeMinutes.HasValue);
        if (enrichable == 0)
        {
            return catalog;
        }
        if (!series.ProviderIds.TryGetValue("Tmdb", out var tmdbId) || string.IsNullOrEmpty(tmdbId))
        {
            notes.Add($"{enrichable} special(s) have no runtime and this series has no TMDB id - they cannot be told apart from extras.");
            return catalog;
        }

        IReadOnlyList<RemoteEpisode> seasonZero;
        try
        {
            seasonZero = await _tmdb.FetchSeasonEpisodesAsync(tmdbId, 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"TMDB runtime enrichment failed ({ex.Message}) - specials keep their existing classification.");
            return catalog;
        }
        if (seasonZero.Count == 0)
        {
            notes.Add($"{enrichable} special(s) have no runtime and TMDB has no season 0 for this series - they cannot be told apart from extras.");
            return catalog;
        }

        var result = RuntimeEnricher.Enrich(catalog, seasonZero);
        if (result.Matched > 0)
        {
            notes.Add($"Runtime for {result.Matched} special(s) supplied by TMDB"
                      + (result.Ambiguous > 0 ? $"; {result.Ambiguous} ambiguous match(es) skipped" : string.Empty)
                      + (result.Unmatched > 0 ? $"; {result.Unmatched} not found there" : string.Empty)
                      + ".");
        }
        return result.Catalog;
    }

    /// <summary>
    /// Second enrichment pass: TVmaze publishes an authoritative per-episode
    /// significance (significant/insignificant) plus runtime, keyless and
    /// sparse. Same conservative unique-match rule; failures are silent.
    /// </summary>
    private async Task<RemoteCatalog> EnrichSignificanceAsync(
        RemoteCatalog catalog, SeriesItemInfo series, List<string> notes, CancellationToken ct)
    {
        if (!catalog.Episodes.Any(e => e.IsSpecial && (e.SourceSignificance is null || !e.RuntimeMinutes.HasValue)))
        {
            return catalog;
        }
        if (!series.ProviderIds.TryGetValue("Tvdb", out var tvdbId) || string.IsNullOrEmpty(tvdbId))
        {
            return catalog;
        }

        IReadOnlyList<RemoteEpisode> specials;
        try
        {
            specials = await _tvmaze.FetchSpecialsByTvdbIdAsync(tvdbId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return catalog;
        }
        if (specials.Count == 0)
        {
            return catalog;
        }

        var result = RuntimeEnricher.Enrich(catalog, specials);
        if (result.Matched > 0)
        {
            notes.Add($"TVmaze classified {result.Matched} special(s) by significance.");
        }
        return result.Catalog;
    }

    private sealed record ChainEntry(string Aid, AniDbEntryCacheItem Item, int Distance);

    private sealed record ChainResult(IReadOnlyList<ChainEntry> Entries, IReadOnlyList<string> Notes, string? Error)
    {
        public static ChainResult Fail(string error) => new(Array.Empty<ChainEntry>(), Array.Empty<string>(), error);
    }

    /// <summary>
    /// Walks the AniDB Sequel chain from the identified entry (BFS, cycle
    /// guard, capped at AniDbChain.MaxEntries). Expansion STOPS at any aid
    /// that is another library series' root (audit D1) — that content is
    /// tracked under its own series. Each entry is cached individually;
    /// sequel fetch failures degrade to a partial chain with a note — only a
    /// failed ROOT entry errors the series.
    /// </summary>
    private async Task<ChainResult> FetchChainAsync(
        string rootAid, IReadOnlySet<string> allRoots, ScanSettings settings, bool fullRefresh, CancellationToken ct)
    {
        var notes = new List<string>();
        var entries = new List<ChainEntry>();
        var queue = new Queue<(string Aid, int Distance)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((rootAid, 0));

        while (queue.Count > 0 && entries.Count < AniDbChain.MaxEntries)
        {
            var (aid, distance) = queue.Dequeue();
            if (!seen.Add(aid))
            {
                continue;
            }
            if (distance > 0 && allRoots.Contains(aid))
            {
                continue; // another series' root: its content is its own report
            }

            var key = $"anidb-entry-{aid}";
            AniDbEntryCacheItem? item = null;
            if (!fullRefresh)
            {
                item = _cache.TryGet<AniDbEntryCacheItem>(key, settings.EndedTtl) is { Catalog.IsEnded: true } ended
                    ? ended
                    : _cache.TryGet<AniDbEntryCacheItem>(key, settings.ContinuingTtl);
            }
            if (item is null)
            {
                var outcome = await _anidb.FetchEntryAsync(aid, ct).ConfigureAwait(false);
                if (outcome.Catalog is null)
                {
                    if (entries.Count == 0)
                    {
                        return ChainResult.Fail(outcome.Error ?? "AniDB fetch failed");
                    }
                    notes.Add($"AniDB entry {aid} fetch failed ({outcome.Error}) - missing detection for its episodes is partial this scan.");
                    continue;
                }
                item = new AniDbEntryCacheItem(outcome.Catalog, outcome.SequelIds);
                _cache.Store(key, item);
            }

            entries.Add(new ChainEntry(aid, item, distance));
            foreach (var sequel in item.SequelIds)
            {
                queue.Enqueue((sequel, distance + 1));
            }
        }

        return new ChainResult(entries, notes, null);
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
