// Edge-case inventory:
// - every spec §6 default exactly as documented
// - GraceHours=0 is a legal value (off) — property is int, not uint with floor
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var c = new PluginConfiguration();
        Assert.True(c.EnableTvLane);
        Assert.True(c.EnableAnimeLane);
        Assert.True(c.EnableMovieLane);
        Assert.Equal(string.Empty, c.TmdbApiKey);
        Assert.Equal(24, c.GraceHours);
        Assert.Equal(90, c.MovieReleaseBufferDays);
        Assert.False(c.IncludeSpecials);
        Assert.False(c.CreateVirtualEpisodes);
        Assert.True(c.ShowPosterBadges);
        Assert.True(c.ShowDetailBadges);
        Assert.Empty(c.ExcludedItemIds);
        Assert.Equal(2000, c.RequestDelayMs);
        Assert.Equal("downloadtime", c.AniDbClientName);
        Assert.Equal(1, c.AniDbClientVersion);
        Assert.Equal(1, c.ContinuingTtlDays);
        Assert.Equal(7, c.EndedTtlDays);
    }

    [Fact]
    public void GraceHours_Zero_IsAssignable()
    {
        var c = new PluginConfiguration { GraceHours = 0 };
        Assert.Equal(0, c.GraceHours);
    }
}
