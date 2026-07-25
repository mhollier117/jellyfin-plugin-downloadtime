using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Daily missing-media scan.</summary>
public class ScanTask : IScheduledTask
{
    private readonly ScanRunner _runner;
    private readonly VirtualEpisodeWriter _writer;
    private readonly ILibraryReader _libraryReader;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(ScanRunner runner, VirtualEpisodeWriter writer, ILibraryReader libraryReader, ILogger<ScanTask> logger)
    {
        _runner = runner;
        _writer = writer;
        _libraryReader = libraryReader;
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

        // Virtual placeholder reconciliation. Runs even when the feature is
        // OFF so turning it off cleans up previously created placeholders.
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var ownedBySeries = _libraryReader.GetSeries().ToDictionary(s => s.Id, s => s.Episodes);
        var applied = 0;
        foreach (var (seriesId, (diff, catalog)) in _runner.Scan.LastDiffs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owned = ownedBySeries.TryGetValue(seriesId, out var eps)
                ? eps
                : (IReadOnlyList<OwnedEpisode>)Array.Empty<OwnedEpisode>();
            var plan = VirtualEpisodePlanner.Plan(diff, catalog, owned, _writer.GetExisting(seriesId), config.CreateVirtualEpisodes);
            if (plan.Creates.Count > 0 || plan.Deletes.Count > 0)
            {
                applied += _writer.Apply(seriesId, plan);
            }
        }
        if (applied > 0)
        {
            _logger.LogInformation("Download Time: applied {Count} virtual placeholder operation(s).", applied);
        }
    }
}
