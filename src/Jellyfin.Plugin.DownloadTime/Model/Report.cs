namespace Jellyfin.Plugin.DownloadTime.Model;

public sealed record MissingEpisodeDto(int? Season, int? Number, string? Title, DateTimeOffset? AiredAt, string Kind, string? SourceEpisodeId);
public sealed record SeriesReportDto(
    Guid ItemId, string Name, string Lane, bool UsedFallback, bool Muted,
    string? Error, IReadOnlyList<string> Notes, IReadOnlyList<MissingEpisodeDto> Missing);
public sealed record MissingMovieDto(int TmdbId, string Title, DateTimeOffset? ReleasedAt);
public sealed record CollectionReportDto(string Name, string ViaMovie, IReadOnlyList<MissingMovieDto> Missing);
public sealed record ScanReport(
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    IReadOnlyList<SeriesReportDto> Series,
    IReadOnlyList<CollectionReportDto> Collections,
    IReadOnlyList<string> GlobalNotes);
