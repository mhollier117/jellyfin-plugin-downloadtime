// Edge-case inventory:
// - roundtrip within TTL returns stored value; expired TTL -> null (via FakeClock, no sleeps).
// - missing key -> null.
// - corrupt/truncated cache file -> null, never throws.
// - key sanitization: "tt123/../x" produces a safe filename, still roundtrips.
// - Store overwrites (second Store wins).
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class CatalogCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-cache-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static RemoteCatalog Sample() => new("Tvdb", "Tvdb", "253573", false,
        new[] { new RemoteEpisode(1, 1, "5088686", null, false, "x") });

    [Fact]
    public void Roundtrip_WithinTtl()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("tvdb-253573", Sample());
        var got = cache.TryGet<RemoteCatalog>("tvdb-253573", TimeSpan.FromDays(1));
        Assert.NotNull(got);
        Assert.Equal("253573", got!.SeriesSourceId);
        Assert.Equal("5088686", got.Episodes[0].SourceEpisodeId);
    }

    [Fact]
    public void Expired_ReturnsNull()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("k", Sample());
        clock.UtcNow = Now.AddDays(2);
        Assert.Null(cache.TryGet<RemoteCatalog>("k", TimeSpan.FromDays(1)));
        clock.UtcNow = Now.AddHours(12);
        Assert.NotNull(cache.TryGet<RemoteCatalog>("k", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void MissingKey_Null()
    {
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        Assert.Null(cache.TryGet<RemoteCatalog>("nope", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void CorruptFile_Null_NoThrow()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("bad", Sample());
        var file = Directory.GetFiles(_dir).Single(f => Path.GetFileName(f).StartsWith("bad"));
        File.WriteAllText(file, "{not json");
        Assert.Null(cache.TryGet<RemoteCatalog>("bad", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void UnsafeKey_SanitizedAndRoundtrips()
    {
        var cache = new CatalogCache(_dir, new FakeClock(Now));
        cache.Store("tt123/../x", Sample());
        Assert.NotNull(cache.TryGet<RemoteCatalog>("tt123/../x", TimeSpan.FromDays(1)));
        Assert.All(Directory.GetFiles(_dir), f => Assert.DoesNotContain("..", Path.GetFileName(f)));
    }

    [Fact]
    public void Store_Overwrites()
    {
        var clock = new FakeClock(Now);
        var cache = new CatalogCache(_dir, clock);
        cache.Store("k", Sample());
        cache.Store("k", Sample() with { SeriesSourceId = "999" });
        Assert.Equal("999", cache.TryGet<RemoteCatalog>("k", TimeSpan.FromDays(1))!.SeriesSourceId);
    }
}
