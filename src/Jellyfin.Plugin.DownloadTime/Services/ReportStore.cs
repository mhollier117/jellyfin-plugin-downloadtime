using System.Text.Json;
using Jellyfin.Plugin.DownloadTime.Model;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Persists the last scan report under the plugin data dir.</summary>
public class ReportStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private ScanReport? _current;
    private bool _loaded;

    public ReportStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "report.json");
    }

    public ScanReport? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try
                    {
                        if (File.Exists(_path))
                        {
                            _current = JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(_path), JsonOpts);
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        _current = null;
                    }
                }
                return _current;
            }
        }
    }

    public void Save(ScanReport report)
    {
        lock (_lock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(report, JsonOpts));
            _current = report;
            _loaded = true;
        }
    }
}
