namespace Jellyfin.Plugin.DownloadTime.Model;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>One episode as the remote source knows it.</summary>
public sealed record RemoteEpisode(
    int? Season,              // null for season-less sources (AniDB)
    int? Number,              // aired number within season, or epno within AniDB entry
    string? SourceEpisodeId,  // per-episode id in the catalog's id namespace, null if none
    DateTimeOffset? AiredAt,  // normalized (AirTime rule); null = undated
    bool IsSpecial,
    string? Title);

/// <summary>Full remote catalog for one series.</summary>
public sealed record RemoteCatalog(
    string SourceKey,         // "Tvdb" | "AniDB" | "Tmdb" | "TvmazeFallback"
    string? IdProviderKey,    // local ProviderIds key episode ids join on: "Tvdb", "AniDB", or null (tuple-only)
    string SeriesSourceId,
    bool IsEnded,             // drives cache TTL
    IReadOnlyList<RemoteEpisode> Episodes);

/// <summary>Exactly one of Catalog/Error is non-null.</summary>
public sealed record FetchOutcome(RemoteCatalog? Catalog, string? Error)
{
    public static FetchOutcome Ok(RemoteCatalog c) => new(c, null);

    public static FetchOutcome Fail(string error) => new(null, error);
}

/// <summary>One local (non-virtual) episode.</summary>
public sealed record OwnedEpisode(
    int? Season,              // ParentIndexNumber
    int? Number,              // IndexNumber
    int? NumberEnd,           // IndexNumberEnd (multi-episode files)
    IReadOnlyDictionary<string, string> ProviderIds,
    DateTimeOffset? AiredAt)
{
    public bool Covers(int n) => Number.HasValue && n >= Number.Value && n <= (NumberEnd ?? Number.Value);
}

public enum MissingKind
{
    Gap,
    New,
}

public sealed record MissingEpisode(RemoteEpisode Episode, MissingKind Kind);

public sealed record SeriesDiff(IReadOnlyList<MissingEpisode> Missing, IReadOnlyList<string> Notes);

public sealed record DiffOptions(DateTimeOffset Now, int GraceHours, bool IncludeSpecials);

public sealed record RemoteMovie(int TmdbId, string Title, DateTimeOffset? ReleasedAt);

public sealed record CollectionCatalog(int CollectionId, string Name, IReadOnlyList<RemoteMovie> Movies);

public sealed record Placement(int Season, int Number);

public sealed record ExistingPlaceholder(Guid ItemId, int? Season, int? Number, string Marker);
public sealed record PlaceholderCreate(int Season, int Number, string Marker, string? Title, DateTimeOffset? AiredAt);
public sealed record PlaceholderPlan(IReadOnlyList<PlaceholderCreate> Creates, IReadOnlyList<Guid> Deletes);
