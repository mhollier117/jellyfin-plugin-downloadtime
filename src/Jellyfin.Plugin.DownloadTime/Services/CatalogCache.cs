using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Disk cache for remote catalogs (spec §2.4). Clock-injected TTLs.</summary>
public partial class CatalogCache
{
    private sealed record Envelope<T>(DateTimeOffset FetchedAt, T Payload);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly IClock _clock;

    public CatalogCache(string cacheDir, IClock clock)
    {
        _dir = cacheDir;
        _clock = clock;
        Directory.CreateDirectory(_dir);
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();

    private string PathFor(string key) => System.IO.Path.Combine(_dir, Unsafe().Replace(key, "_") + ".json");

    public T? TryGet<T>(string key, TimeSpan ttl) where T : class
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var env = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(path), JsonOpts);
            if (env is null || _clock.UtcNow - env.FetchedAt > ttl)
            {
                return null;
            }
            return env.Payload;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void Store<T>(string key, T value)
    {
        var env = new Envelope<T>(_clock.UtcNow, value);
        File.WriteAllText(PathFor(key), JsonSerializer.Serialize(env, JsonOpts));
    }
}
