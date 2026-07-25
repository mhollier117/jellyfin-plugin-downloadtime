// Edge-case inventory:
// - date-only air date normalizes to 23:59:00 UTC that same date
// - result is exactly comparable: airedAt+grace==now must be NOT-aired-long-enough (tested in DiffEngine)
// - leap day accepted
// - OwnedEpisode.Covers: span files, singles, unnumbered
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class AirTimeTests
{
    [Fact]
    public void FromDate_Is_2359_Utc()
    {
        var t = AirTime.FromDate(2024, 1, 7);
        Assert.Equal(new DateTimeOffset(2024, 1, 7, 23, 59, 0, TimeSpan.Zero), t);
    }

    [Fact]
    public void FromDate_LeapDay()
    {
        var t = AirTime.FromDate(2024, 2, 29);
        Assert.Equal(29, t.Day);
        Assert.Equal(TimeSpan.Zero, t.Offset);
    }

    [Fact]
    public void OwnedEpisode_Covers_SpansAndSingles()
    {
        var span = new OwnedEpisode(1, 1, 2, new Dictionary<string, string>(), null);
        Assert.True(span.Covers(1));
        Assert.True(span.Covers(2));
        Assert.False(span.Covers(3));
        var single = new OwnedEpisode(1, 5, null, new Dictionary<string, string>(), null);
        Assert.True(single.Covers(5));
        Assert.False(single.Covers(4));
        var unnumbered = new OwnedEpisode(1, null, null, new Dictionary<string, string>(), null);
        Assert.False(unnumbered.Covers(1));
    }
}
