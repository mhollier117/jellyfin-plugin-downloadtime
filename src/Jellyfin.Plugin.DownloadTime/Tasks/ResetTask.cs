using Jellyfin.Plugin.DownloadTime.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Deletes every virtual placeholder Download Time ever created.</summary>
public class ResetTask : IScheduledTask
{
    private readonly VirtualEpisodeWriter _writer;
    private readonly ILogger<ResetTask> _logger;

    public ResetTask(VirtualEpisodeWriter writer, ILogger<ResetTask> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public string Name => "Remove all Download Time placeholders";
    public string Key => "DownloadTimeReset";
    public string Description => "Deletes every virtual missing-episode placeholder created by Download Time.";
    public string Category => "Download Time";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var n = _writer.DeleteAllPlaceholders();
        _logger.LogInformation("Download Time reset: removed {Count} placeholders.", n);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
