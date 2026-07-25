// Edge-case inventory:
// - settings mapping: every config field lands in ScanSettings (incl. GraceHours=0, mute list).
// - GET Report with no scan yet -> 200, empty well-formed report (never 404/null).
// - GET Report after Save -> the saved report.
// - POST Scan while running -> 409; when idle -> 202.
// - auth attributes: GET requires [Authorize]; POST requires policy RequiresElevation.
using System.Reflection;
using Jellyfin.Plugin.DownloadTime.Api;
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ApiControllerTests
{
    [Fact]
    public void ToSettings_MapsAllFields()
    {
        var c = new PluginConfiguration
        {
            EnableTvLane = false, EnableAnimeLane = true, EnableMovieLane = false,
            GraceHours = 0, IncludeSpecials = true, MovieReleaseBufferDays = 30,
            ExcludedItemIds = new[] { "abc" }, ContinuingTtlDays = 2, EndedTtlDays = 14,
        };
        var s = ScanRunner.ToSettings(c);
        Assert.False(s.EnableTvLane);
        Assert.True(s.EnableAnimeLane);
        Assert.False(s.EnableMovieLane);
        Assert.Equal(0, s.GraceHours);
        Assert.True(s.IncludeSpecials);
        Assert.Equal(30, s.MovieReleaseBufferDays);
        Assert.Contains("abc", s.ExcludedItemIds);
        Assert.Equal(TimeSpan.FromDays(2), s.ContinuingTtl);
        Assert.Equal(TimeSpan.FromDays(14), s.EndedTtl);
    }

    [Fact]
    public void AuthAttributes_AsSpecified()
    {
        var get = typeof(DownloadTimeController).GetMethod(nameof(DownloadTimeController.GetReport))!;
        Assert.NotNull(get.GetCustomAttribute<AuthorizeAttribute>());
        var post = typeof(DownloadTimeController).GetMethod(nameof(DownloadTimeController.StartScan))!;
        Assert.Equal("RequiresElevation", post.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void GetReport_NoScanYet_EmptyWellFormed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dt-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var controller = new DownloadTimeController(new ReportStore(dir), null);
            var result = Assert.IsType<OkObjectResult>(controller.GetReport().Result);
            var report = Assert.IsType<ScanReport>(result.Value);
            Assert.Empty(report.Series);
            Assert.Empty(report.Collections);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
