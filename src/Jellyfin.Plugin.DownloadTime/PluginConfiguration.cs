using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DownloadTime;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableTvLane { get; set; } = true;

    public bool EnableAnimeLane { get; set; } = true;

    public bool EnableMovieLane { get; set; } = true;

    /// <summary>TMDB API key; when blank all tmdbid-routed items are skipped (reported).</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Hours after airing before an episode counts as missing. 0 = off.</summary>
    public int GraceHours { get; set; } = 24;

    /// <summary>Days after theatrical release before a franchise movie counts as missing.</summary>
    public int MovieReleaseBufferDays { get; set; } = 90;

    public bool IncludeSpecials { get; set; }

    public bool CreateVirtualEpisodes { get; set; }

    public bool ShowPosterBadges { get; set; } = true;

    public bool ShowDetailBadges { get; set; } = true;

    /// <summary>Muted item ids (series or movie ids as N-format GUID strings).</summary>
    public string[] ExcludedItemIds { get; set; } = System.Array.Empty<string>();

    /// <summary>Min delay between outbound requests to scraped/rate-limited sources.</summary>
    public int RequestDelayMs { get; set; } = 2000;

    public string AniDbClientName { get; set; } = "downloadtime";

    public int AniDbClientVersion { get; set; } = 1;

    /// <summary>Catalog cache TTL for continuing series, days (spec §2.4).</summary>
    public int ContinuingTtlDays { get; set; } = 1;

    /// <summary>Catalog cache TTL for ended series, days (spec §2.4).</summary>
    public int EndedTtlDays { get; set; } = 7;
}
