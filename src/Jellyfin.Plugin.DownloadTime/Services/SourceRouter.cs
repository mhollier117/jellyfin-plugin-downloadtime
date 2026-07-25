using System.Text.RegularExpressions;
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Routes each item to the source it is identified with (spec §1).</summary>
public static partial class SourceRouter
{
    [GeneratedRegex(@"\[(tvdbid|tmdbid|anidbid|imdbid)-([^\]\s]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex FolderTag();

    public static RouteDecision Route(string path, bool isAnimeLibrary, IReadOnlyDictionary<string, string> providerIds)
    {
        if (isAnimeLibrary && providerIds.TryGetValue("AniDB", out var aid) && !string.IsNullOrEmpty(aid))
        {
            return new RouteDecision(SourceKind.AniDbId, aid);
        }

        var m = FolderTag().Match(path);
        if (m.Success)
        {
            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "tvdbid" => SourceKind.TvdbId,
                "tmdbid" => SourceKind.TmdbId,
                "anidbid" => SourceKind.AniDbId,
                "imdbid" => SourceKind.ImdbId,
                _ => SourceKind.None,
            };
            if (kind != SourceKind.None)
            {
                return new RouteDecision(kind, m.Groups[2].Value);
            }
        }

        if (providerIds.TryGetValue("Tvdb", out var tvdb) && !string.IsNullOrEmpty(tvdb))
        {
            return new RouteDecision(SourceKind.TvdbId, tvdb);
        }
        if (providerIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrEmpty(tmdb))
        {
            return new RouteDecision(SourceKind.TmdbId, tmdb);
        }
        if (providerIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrEmpty(imdb))
        {
            return new RouteDecision(SourceKind.ImdbId, imdb);
        }
        return RouteDecision.None;
    }
}
