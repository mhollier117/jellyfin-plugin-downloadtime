using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Bridges configuration to ScanService and persists results.</summary>
public class ScanRunner
{
    private readonly ScanService _scan;
    private readonly ReportStore _store;
    private readonly Func<PluginConfiguration> _config;

    public ScanRunner(ScanService scan, ReportStore store, Func<PluginConfiguration> config)
    {
        _scan = scan;
        _store = store;
        _config = config;
    }

    public bool IsScanning => _scan.IsScanning;
    public ScanService Scan => _scan;

    public static ScanSettings ToSettings(PluginConfiguration c) => new(
        c.EnableTvLane, c.EnableAnimeLane, c.EnableMovieLane,
        c.GraceHours, c.IncludeSpecials, c.MovieReleaseBufferDays,
        new HashSet<string>(c.ExcludedItemIds, StringComparer.OrdinalIgnoreCase),
        TimeSpan.FromDays(c.ContinuingTtlDays), TimeSpan.FromDays(c.EndedTtlDays));

    public async Task<ScanReport> RunAsync(bool fullRefresh, IProgress<double>? progress, CancellationToken ct)
    {
        var report = await _scan.ScanAsync(ToSettings(_config()), fullRefresh, progress, ct).ConfigureAwait(false);
        _store.Save(report);
        return report;
    }
}
