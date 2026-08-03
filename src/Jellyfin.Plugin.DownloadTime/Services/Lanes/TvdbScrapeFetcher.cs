using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>
/// Reads TheTVDB episode lists from the public all-seasons page (Ronin-style
/// scraping, spec §2.1): one throttled request per series per scan.
/// </summary>
public partial class TvdbScrapeFetcher : ITvdbSource
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    private readonly HttpClient _http;
    private readonly Func<int> _requestDelayMs;

    public TvdbScrapeFetcher(HttpClient http, Func<int> requestDelayMs)
    {
        _http = http;
        _requestDelayMs = requestDelayMs;
    }

    [GeneratedRegex(@"S(\d+)E(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeLabel();

    [GeneratedRegex(@"/episodes/(\d+)")]
    private static partial Regex EpisodeHref();

    public async Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct)
    {
        try
        {
            // numeric id -> slug via dereferrer (301 to /series/{slug})
            var slugPath = $"/series/{tvdbId}";
            if (tvdbId.All(char.IsDigit))
            {
                using var deref = await GetAsync($"https://www.thetvdb.com/dereferrer/series/{tvdbId}", ct).ConfigureAwait(false);
                if (!deref.IsSuccessStatusCode)
                {
                    return FetchOutcome.Fail($"TVDB dereferrer HTTP {(int)deref.StatusCode}");
                }
                var finalUri = deref.RequestMessage?.RequestUri;
                if (finalUri is null || !finalUri.AbsolutePath.StartsWith("/series/", StringComparison.Ordinal))
                {
                    return FetchOutcome.Fail("TVDB dereferrer did not resolve to a series page");
                }
                slugPath = finalUri.AbsolutePath;
            }

            using var resp = await GetAsync($"https://www.thetvdb.com{slugPath}/allseasons/official", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"TVDB all-seasons HTTP {(int)resp.StatusCode}");
            }
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (episodes, error) = ParseAllSeasons(html);
            if (error is not null)
            {
                return FetchOutcome.Fail(error);
            }
            return FetchOutcome.Ok(new RemoteCatalog(
                "Tvdb", "Tvdb", tvdbId, IsEnded: InferEnded(episodes!, DateTimeOffset.UtcNow), episodes!));
        }
        catch (HttpRequestException ex)
        {
            return FetchOutcome.Fail($"TVDB request failed: {ex.Message}");
        }
        finally
        {
            var delay = _requestDelayMs();
            if (delay > 0)
            {
                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ended inference (audit D7): the page carries no status, but a show
    /// whose regular episodes are all dated and last aired long ago is ended
    /// for caching purposes (7-day TTL instead of daily refetch).
    /// </summary>
    public static bool InferEnded(IReadOnlyList<RemoteEpisode> episodes, DateTimeOffset now)
    {
        var regulars = episodes.Where(e => e.Season is not 0 && !e.IsSpecial).ToList();
        if (regulars.Count == 0 || regulars.Any(e => !e.AiredAt.HasValue))
        {
            return false; // undated rows may be scheduled future episodes
        }
        return regulars.Max(e => e.AiredAt!.Value) < now.AddDays(-120);
    }

    /// <summary>Pure parser. Returns (episodes, null) or (null, error). An empty
    /// episode list is an ERROR (mutated-markup fail-safe), never a success.</summary>
    public static (IReadOnlyList<RemoteEpisode>? Episodes, string? Error) ParseAllSeasons(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var items = doc.DocumentNode.SelectNodes("//li[contains(@class,'list-group-item')]");
        var episodes = new List<RemoteEpisode>();
        if (items is not null)
        {
            foreach (var li in items)
            {
                var label = li.SelectSingleNode(".//span[contains(@class,'episode-label')]");
                if (label is null)
                {
                    continue;
                }
                var m = EpisodeLabel().Match(label.InnerText);
                if (!m.Success)
                {
                    continue;
                }
                var season = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var number = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                string? id = null;
                string? title = null;
                var link = li.SelectSingleNode(".//h4//a[contains(@href,'/episodes/')]");
                if (link is not null)
                {
                    var hm = EpisodeHref().Match(link.GetAttributeValue("href", string.Empty));
                    if (hm.Success)
                    {
                        id = hm.Groups[1].Value;
                    }
                    title = HtmlEntity.DeEntitize(link.InnerText).Trim();
                }

                DateTimeOffset? aired = null;
                var dateNode = li.SelectSingleNode(".//ul[contains(@class,'list-inline')]/li");
                if (dateNode is not null && DateTime.TryParse(
                        HtmlEntity.DeEntitize(dateNode.InnerText).Trim(),
                        CultureInfo.GetCultureInfo("en-US"),
                        DateTimeStyles.None,
                        out var d))
                {
                    aired = AirTime.FromDate(d.Year, d.Month, d.Day);
                }

                episodes.Add(new RemoteEpisode(season, number, id, aired, season == 0, title));
            }
        }

        if (episodes.Count == 0)
        {
            return (null, "TVDB all-seasons page yielded zero episodes (markup change or wrong page)");
        }
        return (episodes, null);
    }
}
