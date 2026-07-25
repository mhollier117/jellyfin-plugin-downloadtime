using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Tests.Support;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }
}
