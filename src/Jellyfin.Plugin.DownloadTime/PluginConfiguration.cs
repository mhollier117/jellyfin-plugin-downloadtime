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

    /// <summary>
    /// Include EXTRAS (behind-the-scenes, panels, promos, recaps...) in the
    /// report. Off by default: extras are bonus material, not missing content.
    /// </summary>
    public bool ReportExtras { get; set; }

    /// <summary>Season-0 items shorter than this (when the source reports a runtime) are extras.</summary>
    public int ExtraRuntimeThresholdMinutes { get; set; } = 15;

    /// <summary>Title substrings marking an item as an extra (case-insensitive, user-editable).</summary>
    public string[] ExtraTitlePatterns { get; set; }
        = Services.ContentClassifier.DefaultExtraPatterns.ToArray();

    public bool CreateVirtualEpisodes { get; set; }

    public bool ShowPosterBadges { get; set; } = true;

    public bool ShowDetailBadges { get; set; } = true;

    /// <summary>Inject the user-facing Missing Media page (drawer entry + overlay) for all logged-in users.</summary>
    public bool ShowUserPage { get; set; } = true;

    /// <summary>Muted item ids (series or movie ids as N-format GUID strings).</summary>
    public string[] ExcludedItemIds { get; set; } = System.Array.Empty<string>();

    /// <summary>Min delay between outbound requests to scraped/rate-limited sources.</summary>
    public int RequestDelayMs { get; set; } = 2000;

    /// <summary>
    /// AniDB HTTP client name. Blank by default: client strings are registered
    /// under a specific AniDB account, so each installer must register and enter
    /// their own (anidb.net/software/add). The anime lane is inert when blank.
    /// </summary>
    public string AniDbClientName { get; set; } = string.Empty;

    public int AniDbClientVersion { get; set; } = 1;

    /// <summary>Catalog cache TTL for continuing series, days (spec §2.4).</summary>
    public int ContinuingTtlDays { get; set; } = 1;

    /// <summary>Catalog cache TTL for ended series, days (spec §2.4).</summary>
    public int EndedTtlDays { get; set; } = 7;
}
