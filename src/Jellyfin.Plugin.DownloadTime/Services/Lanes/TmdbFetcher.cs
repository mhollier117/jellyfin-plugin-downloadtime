using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;

namespace Jellyfin.Plugin.DownloadTime.Services.Lanes;

/// <summary>TMDB API client for tmdbid-identified shows and movie collections (spec §2.3).</summary>
public class TmdbFetcher : ITmdbSource
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private readonly HttpClient _http;
    private readonly Func<string> _apiKey;
    private readonly Func<TimeSpan, Task> _delayFn;

    public TmdbFetcher(HttpClient http, Func<string> apiKey, Func<TimeSpan, Task> delayFn)
    {
        _http = http;
        _apiKey = apiKey;
        _delayFn = delayFn;
    }

    public async Task<FetchOutcome> FetchSeriesAsync(string tmdbId, CancellationToken ct)
    {
        var (tvDoc, error) = await GetJsonAsync($"/tv/{tmdbId}", ct).ConfigureAwait(false);
        if (error is not null)
        {
            return FetchOutcome.Fail(error);
        }
        using var tv = tvDoc!;
        var status = tv.RootElement.TryGetProperty("status", out var st) ? st.GetString() : null;
        var isEnded = status is "Ended" or "Canceled";

        var seasonNumbers = new List<int>();
        if (tv.RootElement.TryGetProperty("seasons", out var seasons))
        {
            foreach (var s in seasons.EnumerateArray())
            {
                if (s.TryGetProperty("season_number", out var sn) && sn.ValueKind == JsonValueKind.Number)
                {
                    seasonNumbers.Add(sn.GetInt32());
                }
            }
        }

        var episodes = new List<RemoteEpisode>();
        foreach (var sn in seasonNumbers)
        {
            var (seasonDoc, sErr) = await GetJsonAsync($"/tv/{tmdbId}/season/{sn}", ct).ConfigureAwait(false);
            if (sErr is not null)
            {
                return FetchOutcome.Fail(sErr);
            }
            using var season = seasonDoc!;
            if (!season.RootElement.TryGetProperty("episodes", out var eps))
            {
                continue;
            }
            foreach (var e in eps.EnumerateArray())
            {
                int? number = e.TryGetProperty("episode_number", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : null;
                DateTimeOffset? aired = null;
                if (e.TryGetProperty("air_date", out var ad) && ad.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(ad.GetString(), out var d))
                {
                    aired = AirTime.FromDate(d.Year, d.Month, d.Day);
                }
                var title = e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                // TMDB exposes per-episode runtime on the season endpoint we
                // already call (often null on older/bonus rows).
                int? runtime = e.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number
                    ? rt.GetInt32()
                    : null;
                episodes.Add(new RemoteEpisode(sn, number, null, aired, sn == 0, title, RuntimeMinutes: runtime));
            }
        }

        if (episodes.Count == 0)
        {
            return FetchOutcome.Fail("TMDB returned zero episodes");
        }
        return FetchOutcome.Ok(new RemoteCatalog("Tmdb", null, tmdbId, isEnded, episodes));
    }

    public async Task<CollectionOutcome> FetchCollectionForMovieAsync(int movieTmdbId, CancellationToken ct)
    {
        var (movieDoc, error) = await GetJsonAsync($"/movie/{movieTmdbId.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (error is not null)
        {
            return new CollectionOutcome(null, error, false);
        }
        using var movie = movieDoc!;
        if (!movie.RootElement.TryGetProperty("belongs_to_collection", out var btc) || btc.ValueKind != JsonValueKind.Object)
        {
            return new CollectionOutcome(null, null, NoCollection: true);
        }
        var collectionId = btc.GetProperty("id").GetInt32();

        var (colDoc, cErr) = await GetJsonAsync($"/collection/{collectionId.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (cErr is not null)
        {
            return new CollectionOutcome(null, cErr, false);
        }
        using var col = colDoc!;
        var name = col.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
        var movies = new List<RemoteMovie>();
        if (col.RootElement.TryGetProperty("parts", out var parts))
        {
            foreach (var p in parts.EnumerateArray())
            {
                var id = p.GetProperty("id").GetInt32();
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                DateTimeOffset? released = null;
                if (p.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(rd.GetString(), out var d))
                {
                    released = AirTime.FromDate(d.Year, d.Month, d.Day);
                }
                movies.Add(new RemoteMovie(id, title, released));
            }
        }
        return new CollectionOutcome(new CollectionCatalog(collectionId, name, movies), null, false);
    }

    private async Task<(JsonDocument? Doc, string? Error)> GetJsonAsync(string path, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, "TMDB API key not configured");
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync($"{BaseUrl}{path}?api_key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return (null, $"TMDB request failed: {ex.Message}");
            }
            using (resp)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return (null, "TMDB API key rejected (401)");
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await _delayFn(wait).ConfigureAwait(false);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    return (null, $"TMDB HTTP {(int)resp.StatusCode} for {path}");
                }
                try
                {
                    return (JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), null);
                }
                catch (JsonException ex)
                {
                    return (null, $"TMDB JSON parse error: {ex.Message}");
                }
            }
        }
        return (null, "TMDB rate limited (429) after retry");
    }
}
