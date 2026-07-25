using Jellyfin.Plugin.DownloadTime.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Daily missing-media scan.</summary>
public class ScanTask : IScheduledTask
{
    private readonly ScanRunner _runner;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(ScanRunner runner, ILogger<ScanTask> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public string Name => "Scan for missing media";
    public string Key => "DownloadTimeScan";
    public string Description => "Compares the library against each item's identifying source and records missing episodes/movies.";
    public string Category => "Download Time";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
#if JELLYFIN_10_10
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(6).Ticks };
#else
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(6).Ticks };
#endif
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var report = await _runner.RunAsync(fullRefresh: false, progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Download Time scan finished: {SeriesWithMissing}/{Series} series with missing episodes, {Collections} collections with missing movies.",
            report.Series.Count(s => s.Missing.Count > 0), report.Series.Count, report.Collections.Count);
        // Task 18 appends virtual-placeholder application here when CreateVirtualEpisodes is on.
    }
}
