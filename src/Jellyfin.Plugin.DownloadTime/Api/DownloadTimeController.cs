using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DownloadTime.Api;

[ApiController]
[Route("DownloadTime")]
public class DownloadTimeController : ControllerBase
{
    private readonly ReportStore _store;
    private readonly ScanRunner? _runner;

    public DownloadTimeController(ReportStore store, ScanRunner? runner)
    {
        _store = store;
        _runner = runner;
    }

    /// <summary>Last scan report; empty report when no scan has run yet.</summary>
    [HttpGet("Report")]
    [Authorize]
    public ActionResult<ScanReport> GetReport()
        => Ok(_store.Current ?? new ScanReport(
            default, default,
            Array.Empty<SeriesReportDto>(), Array.Empty<CollectionReportDto>(),
            new[] { "No scan has run yet." }));

    /// <summary>Kicks off a scan in the background. 409 if one is already running.</summary>
    [HttpPost("Scan")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StartScan([FromQuery] bool fullRefresh = false)
    {
        if (_runner is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Scan runner not available.");
        }
        if (_runner.IsScanning)
        {
            return Conflict("A scan is already running.");
        }
        _ = Task.Run(() => _runner.RunAsync(fullRefresh, null, CancellationToken.None));
        return Accepted();
    }
}
