using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>
/// AniDB HTTP API client (spec §2.2). One request per series per scan; HARD
/// pacing between requests — AniDB bans aggressive clients. Episode-ID
/// catalogs make anime detection immune to Ronin merge/split renumbering.
/// </summary>
public class AniDbFetcher : IAniDbSource
{
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, Task> _delayFn;
    private readonly Func<int> _requestDelayMs;
    private readonly Func<(string Name, int Version)> _clientId;
    private DateTimeOffset? _lastRequestAt;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AniDbFetcher(HttpClient http, IClock clock, Func<TimeSpan, Task> delayFn, Func<int> requestDelayMs, Func<(string Name, int Version)> clientId)
    {
        _http = http;
        _clock = clock;
        _delayFn = delayFn;
        _requestDelayMs = requestDelayMs;
        _clientId = clientId;
    }

    public async Task<FetchOutcome> FetchByAnimeIdAsync(string anidbId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var minGap = TimeSpan.FromMilliseconds(_requestDelayMs());
            if (_lastRequestAt.HasValue)
            {
                var elapsed = _clock.UtcNow - _lastRequestAt.Value;
                if (elapsed < minGap)
                {
                    await _delayFn(minGap - elapsed).ConfigureAwait(false);
                }
            }
            _lastRequestAt = _clock.UtcNow;

            var (name, version) = _clientId();
            var url = $"http://api.anidb.net:9001/httpapi?request=anime&client={Uri.EscapeDataString(name)}&clientver={version}&protover=1&aid={Uri.EscapeDataString(anidbId)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"AniDB HTTP {(int)resp.StatusCode}");
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            // AniDB serves gzip-compressed XML regardless of Accept-Encoding.
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                await gz.CopyToAsync(outMs, ct).ConfigureAwait(false);
                bytes = outMs.ToArray();
            }
            var xml = System.Text.Encoding.UTF8.GetString(bytes);
            var (catalog, error) = ParseAnime(xml, _clock);
            return error is null ? FetchOutcome.Ok(catalog!) : FetchOutcome.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return FetchOutcome.Fail($"AniDB request failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pure parser for the httpapi anime response.</summary>
    public static (RemoteCatalog? Catalog, string? Error) ParseAnime(string xml, IClock clock)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            return (null, $"AniDB XML parse error: {ex.Message}");
        }

        if (doc.Root is null)
        {
            return (null, "AniDB response empty");
        }
        if (doc.Root.Name.LocalName == "error")
        {
            return (null, $"AniDB error: {doc.Root.Value}");
        }
        if (doc.Root.Name.LocalName != "anime")
        {
            return (null, $"AniDB unexpected root <{doc.Root.Name.LocalName}>");
        }

        var seriesId = doc.Root.Attribute("id")?.Value ?? string.Empty;
        var isEnded = false;
        if (DateOnly.TryParse(doc.Root.Element("enddate")?.Value, out var end))
        {
            isEnded = AirTime.FromDate(end.Year, end.Month, end.Day) < clock.UtcNow;
        }

        var episodes = new List<RemoteEpisode>();
        foreach (var ep in doc.Root.Element("episodes")?.Elements("episode") ?? Enumerable.Empty<XElement>())
        {
            var id = ep.Attribute("id")?.Value;
            var epno = ep.Element("epno");
            if (id is null || epno is null)
            {
                continue;
            }
            var typeAttr = epno.Attribute("type")?.Value;
            var isSpecial = typeAttr != "1";
            var digits = new string(epno.Value.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            DateTimeOffset? aired = null;
            if (DateOnly.TryParse(ep.Element("airdate")?.Value, out var d))
            {
                aired = AirTime.FromDate(d.Year, d.Month, d.Day);
            }

            var title = ep.Elements("title").FirstOrDefault(t => (string?)t.Attribute(XNamespace.Xml + "lang") == "en")?.Value
                        ?? ep.Elements("title").FirstOrDefault()?.Value;

            episodes.Add(new RemoteEpisode(null, number, id, aired, isSpecial, title));
        }

        if (episodes.Count == 0)
        {
            return (null, "AniDB anime entry contained zero parsable episodes");
        }
        return (new RemoteCatalog("AniDB", "AniDB", seriesId, isEnded, episodes), null);
    }
}
