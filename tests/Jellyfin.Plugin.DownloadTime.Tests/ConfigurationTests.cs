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
        Assert.True(c.ShowUserPage);
        Assert.Empty(c.ExcludedItemIds);
        Assert.Equal(2000, c.RequestDelayMs);
        // Deliberately blank: AniDB client strings are per-account registrations;
        // shipping a shared default would funnel all installs through one account.
        Assert.Equal(string.Empty, c.AniDbClientName);
        Assert.Equal(1, c.AniDbClientVersion);
        Assert.Equal(1, c.ContinuingTtlDays);
        Assert.Equal(7, c.EndedTtlDays);
    }

    [Fact]
    public void ShowUserPage_DefaultsOn_AndAssignable()
    {
        Assert.True(new PluginConfiguration().ShowUserPage);
        Assert.False(new PluginConfiguration { ShowUserPage = false }.ShowUserPage);
    }

    [Fact]
    public void GraceHours_Zero_IsAssignable()
    {
        var c = new PluginConfiguration { GraceHours = 0 };
        Assert.Equal(0, c.GraceHours);
    }
}
