using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>Keyless TVmaze fallback for TVDB/IMDb-identified shows (spec §2.1).</summary>
public class TvmazeFetcher : ITvmazeSource
{
    private readonly HttpClient _http;

    public TvmazeFetcher(HttpClient http) => _http = http;

    public Task<FetchOutcome> FetchByTvdbIdAsync(string tvdbId, CancellationToken ct)
        => FetchAsync($"https://api.tvmaze.com/lookup/shows?thetvdb={Uri.EscapeDataString(tvdbId)}", ct);

    public Task<FetchOutcome> FetchByImdbIdAsync(string imdbId, CancellationToken ct)
        => FetchAsync($"https://api.tvmaze.com/lookup/shows?imdb={Uri.EscapeDataString(imdbId)}", ct);

    private async Task<FetchOutcome> FetchAsync(string lookupUrl, CancellationToken ct)
    {
        try
        {
            using var showResp = await _http.GetAsync(lookupUrl, ct).ConfigureAwait(false);
            if (!showResp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"TVmaze lookup HTTP {(int)showResp.StatusCode}");
            }
            using var showDoc = JsonDocument.Parse(await showResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = showDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return FetchOutcome.Fail("TVmaze lookup returned no show");
            }
            var showId = root.GetProperty("id").GetInt32();
            var isEnded = root.TryGetProperty("status", out var st) && st.GetString() == "Ended";

            using var epResp = await _http.GetAsync($"https://api.tvmaze.com/shows/{showId}/episodes?specials=1", ct).ConfigureAwait(false);
            if (!epResp.IsSuccessStatusCode)
            {
                return FetchOutcome.Fail($"TVmaze episodes HTTP {(int)epResp.StatusCode}");
            }
            using var epDoc = JsonDocument.Parse(await epResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            var episodes = new List<RemoteEpisode>();
            foreach (var e in epDoc.RootElement.EnumerateArray())
            {
                int? season = e.TryGetProperty("season", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
                int? number = e.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
                var isSpecial = e.TryGetProperty("type", out var t) && t.GetString() != "regular";

                DateTimeOffset? aired = null;
                if (e.TryGetProperty("airstamp", out var stamp) && stamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(stamp.GetString(), out var dto))
                {
                    aired = dto;
                }
                else if (e.TryGetProperty("airdate", out var ad) && ad.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(ad.GetString(), out var d))
                {
                    aired = AirTime.FromDate(d.Year, d.Month, d.Day);
                }

                var title = e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                episodes.Add(new RemoteEpisode(season, number, null, aired, isSpecial, title));
            }

            return FetchOutcome.Ok(new RemoteCatalog("TvmazeFallback", null, showId.ToString(System.Globalization.CultureInfo.InvariantCulture), isEnded, episodes));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return FetchOutcome.Fail($"TVmaze request failed: {ex.Message}");
        }
    }
}
