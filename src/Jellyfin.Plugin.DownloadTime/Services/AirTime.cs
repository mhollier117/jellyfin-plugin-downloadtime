namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>Air-time rule (spec §3): a date-only air date counts as aired at 23:59 UTC that day.</summary>
public static class AirTime
{
    public static DateTimeOffset FromDate(int year, int month, int day)
        => new(year, month, day, 23, 59, 0, TimeSpan.Zero);
}
