// Edge-case inventory:
// - Save then Current returns the same data; new store instance re-reads from disk.
// - no file yet -> Current null.
// - corrupt file -> Current null, no throw.
using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Xunit;

namespace Jellyfin.Plugin.DownloadTime.Tests;

public class ReportStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-report-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ScanReport Sample()
    {
        var t = new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);
        return new ScanReport(t, t.AddMinutes(4),
            new[] { new SeriesReportDto(Guid.NewGuid(), "American Gods", "Tvdb", false, false, null,
                Array.Empty<string>(),
                new[] { new MissingEpisodeDto(2, 5, "The Ways of the Dead", t, "Gap", "6767322") }) },
            new[] { new CollectionReportDto("John Wick Collection", "John Wick",
                new[] { new MissingMovieDto(324552, "John Wick: Chapter 2", t) }) },
            Array.Empty<string>());
    }

    [Fact]
    public void SaveThenRead_SameData_AndPersistsAcrossInstances()
    {
        var store = new ReportStore(_dir);
        Assert.Null(store.Current);
        store.Save(Sample());
        Assert.NotNull(store.Current);
        var reread = new ReportStore(_dir);
        Assert.Equal("American Gods", reread.Current!.Series[0].Name);
        Assert.Equal("Gap", reread.Current.Series[0].Missing[0].Kind);
        Assert.Equal(324552, reread.Current.Collections[0].Missing[0].TmdbId);
    }

    [Fact]
    public void CorruptFile_NullCurrent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "report.json"), "{broken");
        Assert.Null(new ReportStore(_dir).Current);
    }
}
