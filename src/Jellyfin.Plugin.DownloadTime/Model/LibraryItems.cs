namespace Jellyfin.Plugin.DownloadTime.Model;

public enum SourceKind { TvdbId, AniDbId, TmdbId, ImdbId, None }
public sealed record RouteDecision(SourceKind Kind, string SourceId)
{
    public static readonly RouteDecision None = new(SourceKind.None, string.Empty);
}

/// <summary>A series as read from the Jellyfin library (adapter output).</summary>
public sealed record SeriesItemInfo(
    Guid Id, string Name, string Path, bool IsAnimeLibrary,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyList<OwnedEpisode> Episodes);

/// <summary>A movie as read from the Jellyfin library.</summary>
public sealed record MovieItemInfo(Guid Id, string Name, int? TmdbId);
